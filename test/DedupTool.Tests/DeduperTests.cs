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

    private static Document Mail(
        string id, string hash, string subject, string? from, string sentDate, string? body, params string[] to) => new()
    {
        DocId = id,
        Custodian = "c1",
        DocType = "email",
        FileName = id + ".eml",
        Sha256 = hash,
        SizeBytes = 10,
        SentDate = sentDate,
        From = from,
        To = new List<string>(to),
        Subject = subject,
        Body = body,
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

        Assert.Equal(new[] { "A1", "E1" }, result.Rows.Select(r => r.DocId).ToArray());
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

        Assert.Equal(new[] { "A1", "A2", "E1", "E2", "MSG" }, result.Rows.Select(r => r.DocId).ToArray());
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
        Assert.Equal("2024-03-06T14:10:00Z", onForward.SentDate);
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

        Assert.Equal(new[]
        {
            "DOC-000001", "DOC-000002", "DOC-000003", "DOC-000004", "DOC-000005",
            "DOC-000006", "DOC-000007", "DOC-000008", "DOC-000012", "DOC-000014",
            "DOC-000015", "DOC-000016",
        }, result.Rows.Select(r => r.DocId).ToArray());
        Assert.DoesNotContain(result.Rows, r => r.DocId is "DOC-000009" or "DOC-000010" or "DOC-000011" or "DOC-000013");
        Assert.Contains("DOC-000008", emitted.Keys);
        Assert.Equal("2024-03-05T19:22:00Z", emitted["DOC-000002"].SentDate);
        Assert.Equal("DOC-000001", emitted["DOC-000002"].FamilyId);
        Assert.Equal(12, result.Rows.Count);
        Assert.Equal(4, result.Suppressed);

        var csv = LoadFileWriter.ToCsv(result.Rows);
        Assert.StartsWith(LoadFileWriter.Header + "\n", csv);
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.Equal(8, line.Split(',').Length);
    }

    [Fact]
    public void OffsetSentDate_IsConvertedToUtc()
    {
        var email = Email("E1", "h1");
        email.SentDate = "2024-03-05T14:22:00-05:00";

        var result = Deduper.Run(new Batch { Documents = new() { email } });

        Assert.Equal("2024-03-05T19:22:00Z", result.Rows.Single().SentDate);
    }

    [Fact]
    public void UtcSentDate_IsPreservedInTheRequiredFormat()
    {
        var email = Email("E1", "h1");
        email.SentDate = "2024-03-05T19:22:00Z";

        var result = Deduper.Run(new Batch { Documents = new() { email } });

        Assert.Equal("2024-03-05T19:22:00Z", result.Rows.Single().SentDate);
    }

    [Fact]
    public void SentDate_WithoutExplicitTimeZone_IsRejected()
    {
        const string bare = "2024-03-05T19:22:00";
        var email = Email("E", "h");
        email.SentDate = bare;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Deduper.Run(new Batch { Documents = new() { email } }));

        Assert.Contains(bare, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LooseFile_SentDate_StaysEmpty()
    {
        var plain = Loose("L1", "c1", "h1");
        var stamped = Loose("L2", "c1", "h2");
        stamped.SentDate = "2024-03-05T14:22:00-05:00";

        var result = Deduper.Run(new Batch { Documents = new() { plain, stamped } });

        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal("", row.SentDate));
    }

    [Fact]
    public void Attachment_InheritsRootEmailUtcSentDate()
    {
        var email = Email("E1", "h1");
        email.SentDate = "2024-03-05T14:22:00-05:00";
        var attachment = Attachment("A1", "E1", "pdf", "a.pdf");
        attachment.SentDate = "1999-01-01T00:00:00Z";

        var result = Deduper.Run(new Batch { Documents = new() { email, attachment } });

        var row = result.Rows.Single(r => r.DocId == "A1");
        Assert.Equal("E1", row.FamilyId);
        Assert.Equal("E1", row.ParentId);
        Assert.Equal("2024-03-05T19:22:00Z", row.SentDate);
    }

    [Fact]
    public void NestedAttachment_ReceivesTopLevelFamilyIdAndRootSentDate()
    {
        var email = Email("E2", "email-2");
        email.SentDate = "2024-03-07T16:00:00Z";
        var embedded = Attachment("MSG", "E2", "eml", "inner.eml");
        embedded.SentDate = "2024-03-05T14:22:00-05:00";
        var nested = Attachment("A2", "MSG", "pdf", "inner.pdf");

        var result = Deduper.Run(new Batch { Documents = new() { email, embedded, nested } });

        var row = result.Rows.Single(r => r.DocId == "A2");
        Assert.Equal("E2", row.FamilyId);
        Assert.Equal("MSG", row.ParentId);
        Assert.Equal("2024-03-07T16:00:00Z", row.SentDate);
        Assert.Equal("E2", result.Rows.Single(r => r.DocId == "MSG").FamilyId);
    }

    [Fact]
    public void SharedHash_KeepsOrdinalSmallestDocId_WhenItAppearsLater()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                Loose("DOC-000013", "mlee", "policy"),
                Loose("DOC-000008", "jsmith", "policy"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Equal("DOC-000008", Assert.Single(result.Rows).DocId);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void InputOrder_DoesNotChangeSurvivorsOrCsv()
    {
        var documents = new List<Document>
        {
            Email("Z-mail", "same-mail"),
            Attachment("Z-child", "Z-mail", "pdf", "z.pdf"),
            Email("A-mail", "same-mail"),
            Attachment("M-child", "A-mail", "pdf", "shared.pdf"),
            Loose("Z-loose", "c2", "loose-hash"),
            Loose("A-loose", "c1", "loose-hash"),
        };
        var orders = new List<List<Document>>
        {
            documents,
            documents.AsEnumerable().Reverse().ToList(),
            new() { documents[5], documents[1], documents[3], documents[0], documents[4], documents[2] },
        };

        string? csv = null;
        string[]? ids = null;
        foreach (var order in orders)
        {
            var result = Deduper.Run(new Batch { Documents = order.ToList() });
            var theseIds = result.Rows.Select(r => r.DocId).ToArray();
            var thisCsv = LoadFileWriter.ToCsv(result.Rows);

            Assert.Equal(new[] { "A-loose", "A-mail", "M-child" }, theseIds);
            Assert.Equal(3, result.Suppressed);
            Assert.DoesNotContain(result.Rows, r => r.DocId == "Z-mail" || r.DocId == "Z-child" || r.DocId == "Z-loose");
            var keptAttachment = result.Rows.Single(r => r.DocId == "M-child");
            Assert.Equal("A-mail", keptAttachment.ParentId);
            Assert.Equal("A-mail", keptAttachment.FamilyId);
            Assert.Equal("pdf", keptAttachment.Hash);

            if (csv == null)
            {
                csv = thisCsv;
                ids = theseIds;
            }
            else
            {
                Assert.Equal(ids, theseIds);
                Assert.Equal(csv, thisCsv);
            }
        }
    }

    [Fact]
    public void CrossMailboxEmail_DifferentHashAndCasing_CollapsesToSmallestDocId()
    {
        var later = Email("Z-copy", "hash-z");
        later.From = "John.Smith@example.com";
        later.SentDate = "2024-03-05T19:22:00Z";
        later.Subject = "Q3 budget review";
        later.Body = "Hi\r\nthere";
        later.To = new List<string> { "Maria.Lee@example.com" };
        var earlier = Email("A-copy", "hash-a");
        earlier.From = "john.smith@example.com";
        earlier.SentDate = "2024-03-05T14:22:00-05:00";
        earlier.Subject = "  Q3 Budget Review  ";
        earlier.Body = "Hi\nthere";
        earlier.To = new List<string> { "maria.lee@example.com" };
        var attachment = Attachment("M-file", "A-copy", "pdf", "budget.pdf");

        DedupResult? first = null;
        foreach (var docs in new[]
        {
            new List<Document> { later, earlier, attachment },
            new List<Document> { attachment, earlier, later },
        })
        {
            var result = Deduper.Run(new Batch { Documents = docs });
            Assert.Equal(new[] { "A-copy", "M-file" }, result.Rows.Select(r => r.DocId).ToArray());
            Assert.Equal(1, result.Suppressed);
            Assert.Equal("A-copy", result.Rows.Single(r => r.DocId == "M-file").ParentId);
            Assert.Equal("2024-03-05T19:22:00Z", result.Rows.Single(r => r.DocId == "A-copy").SentDate);
            var csv = LoadFileWriter.ToCsv(result.Rows);
            if (first == null)
                first = result;
            else
                Assert.Equal(LoadFileWriter.ToCsv(first.Rows), csv);
        }
    }

    [Fact]
    public void SimilarSubjects_DoNotMergeDistinctMessages()
    {
        var original = Mail("A", "h1", "Q3 budget review", "john.smith@example.com", "2024-03-05T19:22:00Z", "body", "maria.lee@example.com");
        var reply = Mail("B", "h2", "Re: Q3 budget review", "john.smith@example.com", "2024-03-05T19:22:00Z", "body", "maria.lee@example.com");
        var otherSender = Mail("C", "h3", "Q3 budget review", "ray.kim@example.com", "2024-03-05T19:22:00Z", "body", "maria.lee@example.com");
        var otherInstant = Mail("D", "h4", "Q3 budget review", "john.smith@example.com", "2024-03-05T19:22:01Z", "body", "maria.lee@example.com");
        var otherBody = Mail("E", "h5", "Q3 budget review", "john.smith@example.com", "2024-03-05T19:22:00Z", "body plus", "maria.lee@example.com");
        var otherOrder = Mail("F", "h6", "Q3 budget review", "john.smith@example.com", "2024-03-05T19:22:00Z", "body", "ray.kim@example.com", "maria.lee@example.com");
        var reversed = Mail("G", "h7", "Q3 budget review", "john.smith@example.com", "2024-03-05T19:22:00Z", "body", "maria.lee@example.com", "ray.kim@example.com");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { original, reply, otherSender, otherInstant, otherBody, otherOrder, reversed },
        });

        Assert.Equal(7, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void IncompleteEmailIdentity_DoesNotMergeDifferentHashes()
    {
        var missingFromA = Mail("A", "h1", "same", "   ", "2024-03-05T19:22:00Z", "body", "a@example.com");
        var missingFromB = Mail("B", "h2", "same", null, "2024-03-05T19:22:00Z", "body", "a@example.com");
        var missingToA = Mail("C", "h3", "same", "a@example.com", "2024-03-05T19:22:00Z", "body");
        missingToA.To = null;
        var missingToB = Mail("D", "h4", "same", "a@example.com", "2024-03-05T19:22:00Z", "body");
        missingToB.To = null;
        var missingSubject = Mail("E", "h5", "same", "a@example.com", "2024-03-05T19:22:00Z", "body", "a@example.com");
        missingSubject.Subject = null;
        var missingBody = Mail("F", "h6", "same", "a@example.com", "2024-03-05T19:22:00Z", "body", "a@example.com");
        missingBody.Body = null;

        var result = Deduper.Run(new Batch
        {
            Documents = new() { missingFromA, missingFromB, missingToA, missingToB, missingSubject, missingBody },
        });

        Assert.Equal(new[] { "A", "B", "C", "D", "E", "F" }, result.Rows.Select(r => r.DocId).ToArray());
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void FractionalSecondsAlone_DoNotMerge()
    {
        var earlier = Mail("A", "h1", "subject", "a@example.com", "2024-03-05T19:22:00.100Z", "body", "b@example.com");
        var later = Mail("B", "h2", "subject", "a@example.com", "2024-03-05T19:22:00.200Z", "body", "b@example.com");

        var result = Deduper.Run(new Batch { Documents = new() { later, earlier } });

        Assert.Equal(new[] { "A", "B" }, result.Rows.Select(r => r.DocId).ToArray());
        Assert.Equal(0, result.Suppressed);
        Assert.All(result.Rows, row => Assert.Equal("2024-03-05T19:22:00Z", row.SentDate));
    }

    [Fact]
    public void EquivalentFractionalInstants_WithDifferentOffsets_Merge()
    {
        var zulu = Mail("Z-copy", "hz", "subject", "a@example.com", "2024-03-05T19:22:00.500Z", "body", "b@example.com");
        var offset = Mail("A-copy", "ho", "subject", "a@example.com", "2024-03-05T14:22:00.500-05:00", "body", "b@example.com");

        var result = Deduper.Run(new Batch { Documents = new() { zulu, offset } });

        var row = Assert.Single(result.Rows);
        Assert.Equal("A-copy", row.DocId);
        Assert.Equal(1, result.Suppressed);
        Assert.Equal("2024-03-05T19:22:00Z", row.SentDate);
    }

    [Fact]
    public void FractionalSentDate_TruncatesForOutput_AndDoesNotMergeDistinctInstants()
    {
        var first = Mail("A", "h1", "subject", "a@example.com", "2024-03-05T19:22:00.900Z", "body", "b@example.com");
        var second = Mail("B", "h2", "subject", "a@example.com", "2024-03-05T14:22:00.100-05:00", "body", "b@example.com");

        var result = Deduper.Run(new Batch { Documents = new() { first, second } });

        Assert.Equal(new[] { "A", "B" }, result.Rows.Select(r => r.DocId).ToArray());
        Assert.All(result.Rows, row => Assert.Equal("2024-03-05T19:22:00Z", row.SentDate));
    }

    [Fact]
    public void ParentCycle_ThrowsInsteadOfLooping()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                Attachment("A", "B", "h1", "a.bin"),
                Attachment("B", "A", "h2", "b.bin"),
            },
        };

        Assert.Throws<InvalidOperationException>(() => Deduper.Run(batch));
    }

    [Fact]
    public void Csv_QuotesCommaQuoteAndLineBreaks_WithoutChangingPlainFields()
    {
        var plain = new LoadFileRow
        {
            DocId = "D", FamilyId = "F", ParentId = "", Custodian = "smith",
            DocType = "loose", FileName = "Q3 budget review.eml", SentDate = "", Hash = "H",
        };
        var special = new LoadFileRow
        {
            DocId = "D2", FamilyId = "F2", ParentId = "", Custodian = "smith, jr",
            DocType = "loose", FileName = "say\"hello\".txt", SentDate = "", Hash = "H",
        };
        var broken = new LoadFileRow
        {
            DocId = "D3", FamilyId = "F3", ParentId = "", Custodian = "smith",
            DocType = "loose", FileName = "a\r\nb", SentDate = "", Hash = "H",
        };

        var plainCsv = LoadFileWriter.ToCsv(new[] { plain });
        Assert.Equal(LoadFileWriter.Header + "\nD,F,,smith,loose,Q3 budget review.eml,,H\n", plainCsv);

        var specialCsv = LoadFileWriter.ToCsv(new[] { special, broken });
        Assert.Contains("\"smith, jr\"", specialCsv, StringComparison.Ordinal);
        Assert.Contains("\"say\"\"hello\"\".txt\"", specialCsv, StringComparison.Ordinal);
        Assert.Contains("\"a\r\nb\"", specialCsv, StringComparison.Ordinal);
        Assert.StartsWith(LoadFileWriter.Header + "\n", specialCsv);
    }

    [Fact]
    public void Write_StoresTheSameTextAsToCsv()
    {
        var rows = new[]
        {
            new LoadFileRow
            {
                DocId = "D", FamilyId = "F", ParentId = "", Custodian = "smith, jr",
                DocType = "loose", FileName = "say\"hello\".txt", SentDate = "", Hash = "H",
            },
            new LoadFileRow
            {
                DocId = "D2", FamilyId = "F2", ParentId = "", Custodian = "smith",
                DocType = "loose", FileName = "a\nb", SentDate = "", Hash = "H",
            },
        };
        var expected = LoadFileWriter.ToCsv(rows);
        var path = Path.Combine(Path.GetTempPath(), "dedup-write-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            LoadFileWriter.Write(path, rows);
            var bytes = File.ReadAllBytes(path);
            var text = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);

            Assert.Equal(expected, text);
            Assert.Equal(expected, File.ReadAllText(path));
            Assert.StartsWith(LoadFileWriter.Header + "\n", text);
            Assert.Contains("\"smith, jr\"", text, StringComparison.Ordinal);
            Assert.Contains("\"say\"\"hello\"\".txt\"", text, StringComparison.Ordinal);
            Assert.Contains("\"a\nb\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\uFEFF', text);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
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
