using System.Globalization;

namespace DedupTool;

/// <summary>
/// Top-level documents (no parentId) with the same SHA-256 collapse to the
/// ordinally smallest DocID. An attachment is kept when that ancestor is
/// kept, even if the same hash was already emitted for another family.
/// Rows are emitted in ascending ordinal DocID order.
/// </summary>
public static class Deduper
{
    public static DedupResult Run(Batch batch)
    {
        // Index so we can find parents when filling in FamilyID / SentDate
        // and when deciding whether an attachment's family survived.
        var byId = new Dictionary<string, Document>();
        foreach (var d in batch.Documents)
        {
            byId[d.DocId] = d;
        }

        // Hash suppression applies only to top-level documents. Attachment
        // hashes do not enter this set, so a shared PDF cannot strip a
        // second email of its own attachment row. The survivor is the
        // ordinally smallest DocID, not whichever document arrived first.
        var winnerByHash = new Dictionary<string, string>();
        foreach (var d in batch.Documents)
        {
            if (d.ParentId != null)
                continue;
            if (!winnerByHash.TryGetValue(d.Sha256, out var winnerId)
                || StringComparer.Ordinal.Compare(d.DocId, winnerId) < 0)
                winnerByHash[d.Sha256] = d.DocId;
        }

        var keptRoots = new HashSet<string>(winnerByHash.Values);

        var rows = new List<LoadFileRow>();
        var suppressed = 0;

        foreach (var d in batch.Documents)
        {
            var root = RootOf(d, byId);
            if (!keptRoots.Contains(root.DocId))
            {
                suppressed++;
                continue;
            }

            rows.Add(new LoadFileRow
            {
                DocId = d.DocId,
                FamilyId = root.DocId,
                ParentId = d.ParentId ?? "",
                Custodian = d.Custodian,
                DocType = d.DocType,
                FileName = d.FileName,
                SentDate = SentDateFor(root),
                Hash = d.Sha256,
            });
        }

        rows.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.DocId, right.DocId));
        return new DedupResult(rows, suppressed);
    }

    private static Document RootOf(Document document, Dictionary<string, Document> byId)
    {
        var seen = new HashSet<string>();
        var current = document;
        while (current.ParentId != null)
        {
            if (!seen.Add(current.DocId))
                throw new InvalidOperationException($"Parent chain contains a cycle at '{current.DocId}'.");
            current = byId[current.ParentId];
        }
        return current;
    }

    /// <summary>
    /// Loose files have an empty SentDate. Every other row uses the top-level
    /// document's timestamp, converted to UTC as yyyy-MM-ddTHH:mm:ssZ.
    /// </summary>
    private static string SentDateFor(Document root)
    {
        if (root.DocType == "loose" || string.IsNullOrEmpty(root.SentDate))
            return "";

        // "K" accepts a missing offset and then uses the machine zone.
        // Require a trailing Z or a numeric offset before parsing.
        if (!HasExplicitTimeZone(root.SentDate)
            || !DateTimeOffset.TryParseExact(
                root.SentDate,
                "yyyy-MM-dd'T'HH:mm:ssK",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw new InvalidOperationException(
                $"SentDate '{root.SentDate}' on '{root.DocId}' is not an ISO-8601 timestamp with an offset.");
        }

        return parsed.ToUniversalTime().ToString("yyyy-MM-dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
    }

    private static bool HasExplicitTimeZone(string sentDate)
    {
        if (sentDate.EndsWith('Z') || sentDate.EndsWith('z'))
            return true;

        // Date hyphens sit inside yyyy-MM-dd. An offset sign is after that.
        var sign = sentDate.LastIndexOfAny(new[] { '+', '-' });
        if (sign < "yyyy-MM-ddT".Length)
            return false;

        var zone = sentDate.AsSpan(sign + 1);
        if (zone.Length == 0)
            return false;
        foreach (var c in zone)
        {
            if (c != ':' && (c < '0' || c > '9'))
                return false;
        }
        return true;
    }
}
