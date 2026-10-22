namespace DedupTool;

/// <summary>
/// Top-level documents (no parentId) are deduplicated by SHA-256.
/// The first one in input order is kept. An attachment is kept when its
/// top-level ancestor is kept, even if the same hash was already emitted
/// for another family.
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
        // second email of its own attachment row.
        var seenRootHash = new HashSet<string>();
        var keptRoots = new HashSet<string>();
        foreach (var d in batch.Documents)
        {
            if (d.ParentId != null)
                continue;
            if (seenRootHash.Add(d.Sha256))
                keptRoots.Add(d.DocId);
        }

        var rows = new List<LoadFileRow>();
        var suppressed = 0;

        foreach (var d in batch.Documents)
        {
            if (!keptRoots.Contains(RootOf(d, byId).DocId))
            {
                suppressed++;
                continue;
            }

            var familyId = d.DocId;
            var sentDate = d.SentDate ?? "";

            if (d.ParentId != null)
            {
                // Attachments belong to their parent's family and carry its date.
                var parent = byId[d.ParentId];
                familyId = parent.DocId;
                sentDate = parent.SentDate ?? "";
            }

            rows.Add(new LoadFileRow
            {
                DocId = d.DocId,
                FamilyId = familyId,
                ParentId = d.ParentId ?? "",
                Custodian = d.Custodian,
                DocType = d.DocType,
                FileName = d.FileName,
                SentDate = sentDate,
                Hash = d.Sha256,
            });
        }

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
}
