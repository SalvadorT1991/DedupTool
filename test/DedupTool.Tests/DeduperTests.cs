using System.Text.Json;
using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class DeduperTests
{
    private static Document Loose(string id, string custodian, string hash) => new()
    {
        DocId = id, Custodian = custodian, DocType = "loose", FileName = id + ".txt", Sha256 = hash, SizeBytes = 10,
    };

    private static Document Email(string id, string hash) => new()
    {
        DocId = id,
        Custodian = "c1",
        DocType = "email",
        FileName = id + ".eml",
        Sha256 = hash,
        SizeBytes = 10,
        SentDate = "2024-03-05T14:22:00-05:00",
        From = "a@example.com",
        To = new List<string> { "b@example.com" },
        Subject = id,
        Body = "body",
    };

    private static Document Attachment(string id, string parentId, string hash, string fileName) => new()
    {
        DocId = id,
        Custodian = "c1",
        DocType = "attachment",
        ParentId = parentId,
        FileName = fileName,
        Sha256 = hash,
        SizeBytes = 10,
    };

    [Fact]
    public void IdenticalLooseDocuments_SecondIsSuppressed()
    {
        var batch = new Batch
        {
            Documents = new() { Loose("A", "c1", "h1"), Loose("B", "c2", "h1") },
        };

        var result = Deduper.Run(batch);

        Assert.Single(result.Rows);
        Assert.Equal("A", result.Rows[0].DocId);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void DistinctDocuments_AllKept()
    {
        var batch = new Batch
        {
            Documents = new() { Loose("A", "c1", "h1"), Loose("B", "c1", "h2") },
        };

        var result = Deduper.Run(batch);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void LoadFile_StartsWithFixedHeader()
    {
        var csv = LoadFileWriter.ToCsv(Array.Empty<LoadFileRow>());

        Assert.StartsWith("DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash", csv);
    }

    [Fact]
    public void SharedAttachmentHash_EachKeptEmailRetainsItsOwnCopy()
    {
        const string pdfHash = "812ffd6153e7cc868416c5311a64daad44ef784610adf830f27a080f89af757c";
        var batch = new Batch
        {
            Documents = new()
            {
                Email("DOC-000001", "email-hash-1"),
                Attachment("DOC-000003", "DOC-000001", pdfHash, "vendor-report.pdf"),
                Email("DOC-000004", "email-hash-2"),
                Attachment("DOC-000005", "DOC-000004", pdfHash, "vendor-report.pdf"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Equal(0, result.Suppressed);
        var onForward = Assert.Single(result.Rows, r => r.DocId == "DOC-000005");
        var onOriginal = Assert.Single(result.Rows, r => r.DocId == "DOC-000003");
        Assert.Equal("DOC-000004", onForward.ParentId);
        Assert.Equal("DOC-000004", onForward.FamilyId);
        Assert.Equal("vendor-report.pdf", onForward.FileName);
        Assert.Equal(pdfHash, onForward.Hash);
        Assert.Equal(onOriginal.Hash, onForward.Hash);
    }

    [Fact]
    public void IdenticalEmailHash_OmitsTheSecondFamilyTogether()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                Email("E1", "same-email"),
                Attachment("A1", "E1", "pdf-1", "a.pdf"),
                Email("E2", "same-email"),
                Attachment("A2", "E2", "pdf-2", "b.pdf"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Equal(new[] { "E1", "A1" }, result.Rows.Select(r => r.DocId).ToArray());
        Assert.Equal(2, result.Suppressed);
        Assert.Equal("E1", result.Rows.Single(r => r.DocId == "A1").ParentId);
    }

    [Fact]
    public void NestedAttachment_StaysWithTheKeptEmail_WhenTheHashAlreadyAppeared()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                Email("E1", "email-1"),
                Attachment("A1", "E1", "pdf", "first.pdf"),
                Email("E2", "email-2"),
                Attachment("MSG", "E2", "eml", "inner.eml"),
                Attachment("A2", "MSG", "pdf", "inner.pdf"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Equal(new[] { "E1", "A1", "E2", "MSG", "A2" }, result.Rows.Select(r => r.DocId).ToArray());
        Assert.Equal(0, result.Suppressed);
        Assert.Equal("MSG", result.Rows.Single(r => r.DocId == "A2").ParentId);
    }

    [Fact]
    public void SampleBatch_Doc000004_KeepsVendorReportDespiteSharedHash()
    {
        var batch = LoadSample();
        var result = Deduper.Run(batch);

        var onForward = Assert.Single(result.Rows, r => r.DocId == "DOC-000005");
        var onOriginal = Assert.Single(result.Rows, r => r.DocId == "DOC-000003");
        Assert.Contains(result.Rows, r => r.DocId == "DOC-000004");
        Assert.Equal("DOC-000004", onForward.ParentId);
        Assert.Equal("DOC-000004", onForward.FamilyId);
        Assert.Equal("jsmith", onForward.Custodian);
        Assert.Equal("attachment", onForward.DocType);
        Assert.Equal("vendor-report.pdf", onForward.FileName);
        Assert.Equal("2024-03-06T09:10:00-05:00", onForward.SentDate);
        Assert.Equal(onOriginal.Hash, onForward.Hash);
        Assert.Equal("812ffd6153e7cc868416c5311a64daad44ef784610adf830f27a080f89af757c", onForward.Hash);
    }

    [Fact]
    public void SampleBatch_EveryRetainedEmail_ContainsEachOfItsAttachments()
    {
        var batch = LoadSample();
        var result = Deduper.Run(batch);
        var emitted = result.Rows.ToDictionary(r => r.DocId);
        var children = batch.Documents
            .Where(d => d.ParentId != null)
            .ToLookup(d => d.ParentId!);

        foreach (var email in batch.Documents.Where(d => d.DocType == "email" && d.ParentId == null))
        {
            if (!emitted.ContainsKey(email.DocId))
                continue;

            foreach (var attachment in Descendants(email.DocId, children))
            {
                Assert.True(emitted.ContainsKey(attachment.DocId), $"{email.DocId} lost {attachment.DocId} ({attachment.FileName}).");
                Assert.Equal(attachment.ParentId ?? "", emitted[attachment.DocId].ParentId);
                Assert.Equal(attachment.Sha256, emitted[attachment.DocId].Hash);
            }
        }

        // This batch has no identical email hashes, and cross-mailbox dedup is
        // intentionally not implemented, so both mailbox copies stay.
        Assert.Contains("DOC-000001", emitted.Keys);
        Assert.Contains("DOC-000009", emitted.Keys);
        Assert.Contains("DOC-000010", emitted.Keys);
        Assert.Contains("DOC-000011", emitted.Keys);

        // Loose-file hash dedup is unchanged: mlee's copy of policy.docx is still omitted.
        Assert.Contains("DOC-000008", emitted.Keys);
        Assert.DoesNotContain("DOC-000013", emitted.Keys);
        Assert.Equal(15, result.Rows.Count);
        Assert.Equal(1, result.Suppressed);
    }

    private static IEnumerable<Document> Descendants(string parentId, ILookup<string, Document> children)
    {
        foreach (var child in children[parentId])
        {
            yield return child;
            foreach (var nested in Descendants(child.DocId, children))
                yield return nested;
        }
    }

    private static Batch LoadSample()
    {
        var path = FindUp("data", "batch-sample.json");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<Batch>(File.ReadAllText(path), options)
            ?? throw new InvalidOperationException("Sample batch was empty.");
    }

    private static string FindUp(params string[] relativeParts)
    {
        var starts = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
        foreach (var start in starts)
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }

        throw new FileNotFoundException("Could not find data/batch-sample.json from the test directory.");
    }
}
