using System.Globalization;
using System.Text;

namespace DedupTool;

/// <summary>
/// Top-level documents that share a SHA-256, and top-level emails that share
/// a normalized message identity, collapse to the ordinally smallest DocID.
/// An attachment is kept only when that ancestor is kept. Rows are emitted in
/// ascending ordinal DocID order.
/// </summary>
public static class Deduper
{
    private static readonly string[] SentDateFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    };

    public static DedupResult Run(Batch batch)
    {
        var byId = new Dictionary<string, Document>(batch.Documents.Count, StringComparer.Ordinal);
        foreach (var d in batch.Documents)
            byId[d.DocId] = d;

        var keptRoots = KeptRootIds(batch.Documents);
        var rootCache = new Dictionary<string, Document>(batch.Documents.Count, StringComparer.Ordinal);

        var rows = new List<LoadFileRow>();
        var suppressed = 0;
        foreach (var d in batch.Documents)
        {
            var root = RootOf(d, byId, rootCache);
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

    /// <summary>
    /// Equivalence is the union of equal top-level hashes and equal email
    /// identities. The survivor of each class is the ordinal-minimum DocID.
    /// </summary>
    private static HashSet<string> KeptRootIds(List<Document> documents)
    {
        var topLevel = new List<Document>();
        foreach (var d in documents)
        {
            if (d.ParentId == null)
                topLevel.Add(d);
        }

        var parent = new int[topLevel.Count];
        for (var i = 0; i < parent.Length; i++)
            parent[i] = i;

        var hashOwner = new Dictionary<string, int>(topLevel.Count, StringComparer.Ordinal);
        var identityOwner = new Dictionary<EmailKey, int>();
        for (var i = 0; i < topLevel.Count; i++)
        {
            var doc = topLevel[i];
            if (hashOwner.TryGetValue(doc.Sha256, out var sameHash))
                Union(parent, i, sameHash);
            else
                hashOwner[doc.Sha256] = i;

            if (!TryCreateEmailKey(doc, out var key))
                continue;
            if (identityOwner.TryGetValue(key, out var sameMail))
                Union(parent, i, sameMail);
            else
                identityOwner[key] = i;
        }

        var best = new Dictionary<int, int>();
        for (var i = 0; i < topLevel.Count; i++)
        {
            var root = Find(parent, i);
            if (!best.TryGetValue(root, out var current)
                || StringComparer.Ordinal.Compare(topLevel[i].DocId, topLevel[current].DocId) < 0)
                best[root] = i;
        }

        var kept = new HashSet<string>(best.Count, StringComparer.Ordinal);
        foreach (var index in best.Values)
            kept.Add(topLevel[index].DocId);
        return kept;
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a != b)
            parent[b] = a;
    }

    /// <summary>
    /// A key exists only when from, sentDate, subject, body, and to are all
    /// present and the timestamp has an explicit zone. Incomplete emails are
    /// not merged with each other through this key.
    /// </summary>
    private static bool TryCreateEmailKey(Document doc, out EmailKey key)
    {
        key = default;
        if (!string.Equals(doc.DocType, "email", StringComparison.Ordinal))
            return false;
        if (doc.From == null || doc.Subject == null || doc.Body == null || doc.To == null || doc.SentDate == null)
            return false;

        var from = doc.From.Trim().ToLowerInvariant();
        if (from.Length == 0)
            return false;
        if (!TryParseSentDate(doc.SentDate, out var sent))
            return false;

        key = new EmailKey(
            from,
            sent.ToUniversalTime().Ticks, // full instant; output truncation is separate
            doc.Subject.Trim().ToLowerInvariant(),
            NormalizeBody(doc.Body),
            NormalizeRecipients(doc.To));
        return true;
    }

    private static string NormalizeBody(string body)
    {
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c != '\r')
            {
                sb.Append(c);
                continue;
            }

            sb.Append('\n');
            if (i + 1 < body.Length && body[i + 1] == '\n')
                i++;
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Order is preserved. The spec does not say a reordered recipient list
    /// is the same message, so the list is not sorted.
    /// </summary>
    private static string NormalizeRecipients(List<string> recipients)
    {
        var sb = new StringBuilder();
        foreach (var raw in recipients)
        {
            var item = (raw ?? "").Trim().ToLowerInvariant();
            sb.Append(item.Length.ToString(CultureInfo.InvariantCulture));
            sb.Append('\u001f');
            sb.Append(item);
            sb.Append('\u001e');
        }
        return sb.ToString();
    }

    private static Document RootOf(
        Document document,
        Dictionary<string, Document> byId,
        Dictionary<string, Document> rootCache)
    {
        if (rootCache.TryGetValue(document.DocId, out var cached))
            return cached;

        var chain = new List<Document>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = document;
        while (true)
        {
            if (rootCache.TryGetValue(current.DocId, out cached))
            {
                foreach (var node in chain)
                    rootCache[node.DocId] = cached;
                return cached;
            }

            if (current.ParentId == null)
            {
                foreach (var node in chain)
                    rootCache[node.DocId] = current;
                rootCache[current.DocId] = current;
                return current;
            }

            if (!seen.Add(current.DocId))
                throw new InvalidOperationException($"Parent chain contains a cycle at '{current.DocId}'.");

            chain.Add(current);
            current = byId[current.ParentId];
        }
    }

    /// <summary>
    /// Loose files have an empty SentDate. Every other row uses the top-level
    /// document's timestamp, converted to UTC and truncated to a whole second.
    /// </summary>
    private static string SentDateFor(Document root)
    {
        if (root.DocType == "loose" || string.IsNullOrEmpty(root.SentDate))
            return "";

        if (!TryParseSentDate(root.SentDate, out var parsed))
        {
            throw new InvalidOperationException(
                $"SentDate '{root.SentDate}' on '{root.DocId}' is not an ISO-8601 timestamp with an offset.");
        }

        var utc = parsed.ToUniversalTime();
        var wholeSecond = utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond;
        return new DateTimeOffset(wholeSecond, TimeSpan.Zero)
            .ToString("yyyy-MM-dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
    }

    private static bool TryParseSentDate(string sentDate, out DateTimeOffset parsed)
    {
        parsed = default;
        if (!HasExplicitTimeZone(sentDate))
            return false;

        return DateTimeOffset.TryParseExact(
            sentDate,
            SentDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out parsed);
    }

    private static bool HasExplicitTimeZone(string sentDate)
    {
        if (sentDate.EndsWith('Z') || sentDate.EndsWith('z'))
            return true;

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

    private readonly record struct EmailKey(string From, long SentUtcTicks, string Subject, string Body, string Recipients);
}
