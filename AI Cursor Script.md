# Deduplication algorithm review
_Exported on 10/22/2026 at 16:22:29 CST from Cursor (3.26.16)_

---

**User**

Act like a senion net engineer reviewing this repository, 
inspect @SPEC.md , the code, existing tests , @data/batch-sample.json  , @data/current-tool-output.csv  , 

1) explain me the current deduplication algorithm and csv generation process or flow, 
2) why an email might lose attachements after deduplication ? (if its possible validate before and after to confirm this is true)
3) root causes, show me the code and sample data 
4) what does exactly sha256 in this context, how it gets populated 
5) identify ambiguitis and contradictions in @SPEC.md  
6) propose an implementation plan, including test , regresion tests and risks.

do not invent requirements or silently make assumptions, distinguish confirmed findings from hyphoteses, keep the existing command line contract and exact csv column order 

---

**Cursor**

I’ll review the spec, code, tests, and sample data as a senior .NET engineer, then separate confirmed behavior from hypotheses. First I’ll read the spec and the implementation so the flow is grounded in what the repository actually does.

The checked-in CSV matches a fresh run. I’ll reverse the batch next to see whether survivor choice depends on input order, which the spec says it must not.

The sample run matches the checked-in CSV, and reversing the batch moves which email keeps the shared files. I’ll record that as a review you can open beside the chat, with confirmed results separated from decisions the spec does not settle.

An email loses attachments because deduplication drops later documents that share a `sha256`, including an attachment whose parent email is still written to the load file. I re-ran the tool on `data/batch-sample.json`: it printed `16 documents in, 12 out, 4 suppressed`, and the CSV matched `data/current-tool-output.csv` exactly. Two of those four suppressed rows are the only children of emails that remain.

The structured review is in [Dedup algorithm review](C:\Users\Usuario\.cursor\projects\c-Users-Usuario-Downloads-KLD-Senior-Engineer-Assessment-DedupeTool-1-DedupTool\canvases\dedup-algorithm-review.canvas.tsx). A canvas is a live view beside the chat; I used one so the before/after counts and the spec conflicts stay visible while you read the code.

## 1. Current algorithm and CSV flow

`DedupTool <batch.json> <loadfile.csv>` reads the whole JSON file, deserializes it with case-insensitive property names, runs `Deduper.Run`, writes the CSV, and prints one summary line. Bad argument count returns exit code 2.

The duplicate rule is one global set of hash strings. The first document with a given `sha256` is kept. Every later document with that string is counted as suppressed and skipped. Document type, custodian, and `parentId` do not affect that decision.

```22:40:src/DedupTool/Deduper.cs
        foreach (var d in batch.Documents)
        {
            if (!seen.Add(d.Sha256))
            {
                // Already emitted a doc with this content.
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
```

For a kept row, `FamilyID` is the document’s own `docId`, or its immediate parent’s `docId` when `parentId` is set. `SentDate` is the raw input string, or the immediate parent’s raw string. There is no walk to the top of a `parentId` chain and no conversion to UTC.

The writer emits a fixed header, then each row with `string.Join(",", ...)` and a LF. Fields are not quoted.

```11:22:src/DedupTool/LoadFileWriter.cs
    public const string Header = "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash";

    public static string ToCsv(IEnumerable<LoadFileRow> rows)
    {
        // Ingest job expects LF line endings regardless of platform.
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (var r in rows)
        {
            sb.Append(string.Join(",",
                r.DocId, r.FamilyId, r.ParentId, r.Custodian,
                r.DocType, r.FileName, r.SentDate, r.Hash)).Append('\n');
```

Column order is `DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash`. Row order follows surviving input order. The summary line is `{batchId}: {in} documents in, {out} out, {suppressed} suppressed as duplicates.`

The three tests cover a second loose file with the same hash, two loose files with different hashes, and that the CSV text starts with the header. Nothing loads the sample, builds an email family, or checks dates.

## 2. Why an email can lose attachments

Confirmed on this batch. Input child counts come from `parentId`. Output child counts are load-file rows whose `ParentID` is that email.

| Email | Custodian | Attachments in | Attachment rows out |
|---|---|---:|---:|
| DOC-000001 `Q3 budget review.eml` | jsmith | 2 | 2 |
| DOC-000004 `FW vendor report.eml` | jsmith | 1 | 0 |
| DOC-000006 `Lunch.eml` | jsmith | 0 | 0 |
| DOC-000009 `Q3 budget review.eml` | mlee | 2 | 0 |
| DOC-000012 `Re Q3 budget review.eml` | mlee | 0 | 0 |
| DOC-000015 `Team offsite.eml` | mlee | 1 | 1 |

DOC-000004 and DOC-000009 are in the CSV. Their attachment documents are not. DOC-000015 keeps `agenda.docx` because that hash occurs once.

I also ran the same 16 documents in reverse order. The summary stayed `16 in, 12 out, 4 suppressed`, and the survivor set changed. DOC-000009 then kept both attachments, and DOC-000001 kept none. DOC-000004 still lost the PDF in that reversal because DOC-000011 is seen first and already owns that hash. Which family is stripped depends on input order.

## 3. Root cause, in the code and the sample

There are two separate facts in this batch.

**Confirmed cause of the missing attachments.** The same file bytes appear under more than one parent. The first row occupies the hash, and later attachment rows are discarded even though their parent email is kept.

`vendor-report.pdf` is one hash, `812ffd6153e7…`, on three documents:

- DOC-000003, attachment of DOC-000001, kept
- DOC-000005, attachment of DOC-000004, suppressed
- DOC-000011, attachment of DOC-000009, suppressed

`Q3-budget.xlsx` is one hash, `d0687f0fe9d3…`, on DOC-000002 (kept) and DOC-000010 (suppressed).

DOC-000004 is a different message from DOC-000001: subject `FW: vendor report`, recipient `ray.kim@example.com`, sent `2024-03-06T09:10:00-05:00`, and a forward body. Same custodian. The loss happens inside one custodian’s mail, because the PDF hash was already used by DOC-000003.

DOC-000009 is the other mailbox copy of the Q3 message, and it is also kept. Its two attachments are then removed because those hashes were already used by DOC-000002 and DOC-000003. The empty email and the missing cross-mailbox rule are stacked on this document. Removing DOC-000009 as a duplicate email would still leave DOC-000004 with no PDF.

The fourth suppressed document is DOC-000013, mlee’s loose `policy.docx`, because DOC-000008 already used hash `5395fc0e907d…`. That one follows the loose-file hash rule. It is not an attachment-loss case.

**Confirmed code limitation the sample does not exercise.** Family assignment uses one parent hop. The sample’s parents are already top-level emails, so the emitted `FamilyID` values match the top-level id for the rows that survive. A grandchild attachment is described in the spec and is absent from this batch.

## 4. What `sha256` is here, and how it is populated

The spec defines `sha256` as the SHA-256 of the raw file bytes as collected, present on every input document. This repository never sees those bytes and never computes a hash. There is no hash API in the project.

The value is populated in three steps:

1. The producer of the batch writes the hex string into JSON field `sha256`.
2. `Program.Main` deserializes it onto `Document.Sha256`. Case-insensitive matching is why `sha256` binds to `Sha256`.
3. `Deduper` puts that exact string in the `HashSet` and copies it to `LoadFileRow.Hash`, which becomes the `Hash` column.

```14:18:src/DedupTool/Program.cs
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var batch = JsonSerializer.Deserialize<Batch>(File.ReadAllText(args[0]), options)
                    ?? throw new InvalidOperationException("Batch file was empty.");

        var result = Deduper.Run(batch);
```

`sizeBytes` is deserialized and never read. The string is not normalized. In this sample every email has a distinct hash, so email-to-email hash equality does not change the fixture. DOC-000001 is `f01c98532558…` and DOC-000009 is `5fac3b2c2bcf…`.

What the sample does show about that pair, as observed differences: custodians `jsmith` and `mlee`; sizes `18234` and `18310`; `from` differs only by case (`john.smith@example.com` versus `John.Smith@example.com`); the body is the same text with LF on DOC-000001 and CRLF on DOC-000009 (lengths 137 and 143); `sentDate` is `2024-03-05T14:22:00-05:00` versus `2024-03-05T19:22:00Z`. Those two timestamps are the same instant. Subject, the single recipient, and the file name match.

## 5. Ambiguities and contradictions in SPEC.md

These are conflicts in the spec text, or places where the spec is silent. I am not choosing a winner.

**Attachment identity versus intact families.** Section 3.2 says two attachments with the same `sha256` are duplicates. Section 3.6 says suppressed duplicates are omitted. Section 3.5 says a family must be produced intact and a reviewer who opens an email must see every attachment that was on it. DOC-000001 and DOC-000004 are different emails that share one PDF. Satisfying 3.2 and 3.6 removes that PDF from the second email. Satisfying 3.5 keeps both attachment rows. The ticket in the README describes the 3.5 outcome. The spec states both.

**Cross-mailbox email duplicates versus a self-contained custodian set.** Section 3.3 says mailbox copies are duplicates even when `sha256` differs, and section 3.4 says deduplication is across the whole batch. Section 4 says each custodian’s production set must be complete and self-contained. The load file has one `Custodian` column, and the column list is frozen, so one surviving row cannot record both custodians. Suppressing DOC-000009 removes the Q3 email from mlee’s set. Keeping both rows leaves the cross-mailbox duplicate in the load file.

**The same tension for loose files.** Sections 3.1 and 3.4 say identical loose-file hashes are batch-wide duplicates. The current output does that: DOC-000013 is omitted and DOC-000008 remains. Section 4 still says mlee’s set must stand alone. The spec does not say which section wins.

**Email equality is undefined.** Section 3.3 says the tool must recognize mailbox copies. It does not name the fields, and the input model has no message id. Any match rule built from `from`, `to`, `subject`, `body`, and `sentDate` would be a decision. The sample gives one positive pair and two messages that are different on those fields: the forward DOC-000004 and the reply DOC-000012. It does not show display names, reordered recipients, or a mailbox copy whose attachment set differs.

**Which copy survives is undefined, and the implementation depends on input order.** Section 6.1 requires the same load file regardless of document order. Section 5 says row order is not significant. The reversal run changes the row set, so it fails 6.1 even if “same load file” means the same rows in any order. The spec does not say whether the winner is the lowest `docId`, the earliest sent instant, or one copy per custodian.

**Sent date and family root are specified. Nested mail is not in the fixture.** Section 5 requires `YYYY-MM-DDTHH:MM:SSZ` and says attachments carry the top-level email’s date. The checked-in CSV still has `2024-03-05T14:22:00-05:00` on the jsmith rows. The spec’s own example, `2024-03-05T19:22:00Z`, is the UTC form of that instant. The sample has no attachment-of-an-attachment, so the one-hop `FamilyID` bug is visible in code and unproven by this output.

**An attached email has no consistent type.** The field table says `parentId` is set for attachments and null otherwise. The same section says an attachment may itself be an email that has attachments. The spec does not say whether that embedded message is `docType: email` with a parent, or `docType: attachment` without the email fields. The sample does not contain one.

**Smaller gaps, not demonstrated by this batch.** Quoting is unspecified; none of the eight output fields in the sample contain a comma, quote, or newline, so the unquoted writer happens to stay aligned. Hash case, missing parents, and duplicate `docId`s are unspecified in behavior and absent from the sample. Section 6.3 requires the three counts and does not lock the current sentence; the program already prints those counts.

## 6. Implementation plan, tests, and risks

Do not start the ambiguous parts until the choice is written in `DECISIONS.md`. The command stays `DedupTool <batch.json> <loadfile.csv>`, including exit code 2 for the wrong number of arguments and the current summary sentence. The header stays exactly `DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash`.

Decisions the spec does not make:

1. For a kept email, emit every descendant even when an attachment hash was already emitted for another kept family. That is the behavior that fixes DOC-000004 and matches section 3.5. It conflicts with a literal reading of 3.2 and 3.6. The other reading continues to strip shared attachments.
2. Custodian scope, separately for emails and for loose files. Global suppression, or one surviving copy per custodian. Section 4 and sections 3.1–3.4 disagree, and the frozen CSV cannot store both custodians on one row.
3. The email match key, and the survivor rule that makes the result independent of input order. The sample can reject a bad key. It cannot supply the key.
4. Leave loose-file behavior unchanged until decision 2 is made. Changing DOC-000013 would be a product choice, not a consequence of the attachment ticket.

Specified work that can proceed once those decisions are recorded next to it:

- Convert kept `SentDate` values to UTC with no fractional seconds, and leave loose-file dates empty.
- Set `FamilyID` and attachment `SentDate` from the top of the `parentId` chain. Prove the walk with a synthetic nested fixture, because the sample has no grandchild.
- Apply the chosen survivor rule everywhere a duplicate is still suppressed, so a permutation cannot move an attachment from one family to another.

Tests:

- Regression for the ticket: if DOC-000004 is kept, the load file contains `vendor-report.pdf` with `ParentID` DOC-000004. This case does not depend on the email key.
- The same assertion for whichever Q3 family the custodian decision keeps, including both of its children.
- Permutation test: shuffled input yields the same multiset of rows. The reversal run is the failing baseline.
- Date test: DOC-000001’s instant becomes `2024-03-05T19:22:00Z`, and its kept attachments carry that same value. Loose rows stay empty.
- Contract test: header equals the constant, every data line has eight fields, line ending is LF. The existing test only checks a prefix, so an extra column after `Hash` would still pass.
- Negative fixtures from the sample: DOC-000004 and DOC-000012 remain distinct from DOC-000001.
- Hold the DOC-000001 versus DOC-000009 expectation, and the DOC-000013 expectation, until decisions 2 and 3 exist. `current-tool-output.csv` documents today’s bug. It is the wrong oracle for the corrected file.
- Keep the two existing loose-file hash tests only if decision 2 keeps global loose-file dedup.

Risks:

- A match key that strips `Re:` or `FW:`, or that treats quoted body text as identity, would collapse the forward or the reply. The sample shows those are different messages.
- Emitting the same `Hash` on two kept attachment rows may be what a literal reading of section 3.2 forbids. How the ingest job treats duplicate `Hash` values is not in this repository.
- UTC formatting changes every jsmith date relative to the checked-in CSV. The spec requires that format. This repo does not contain the ingest parser, so tolerance of the current offset form is unknown.
- Per-custodian retention keeps section 4 and leaves cross-mailbox copies in the review set. Global retention does the opposite and drops a severed custodian’s copy.
- The tool holds the raw JSON, the object graph, and the entire CSV string at once. Section 6.2 names batches up to about two million documents. There is no measurement here of whether that fits.
- A parent walk needs a cycle guard. The spec says the parent is always present and does not mention cycles. A missing parent currently throws from `byId[d.ParentId]` with no summary line.
- Adding quotes around fields would change rows the ingest job already accepts. The sample does not contain a comma in an output field, so quoting is an unresolved contract risk.
- `DECISIONS.md` is still empty. A code change that picks any of the four decisions without recording it is the failure mode this exercise calls out.

---

**User**


Requirements:

1. Fix the confirmed attachment-loss bug.
2. Preserve the existing command-line contract, CSV header, column order, and summary format.
3. Add regression tests proving that:
   - DOC-000004 retains vendor-report.pdf even though the same attachment hash appears under DOC-000001.
   - Every retained email family contains all attachments required by the chosen family-integrity behavior.
   - The existing loose-file deduplication tests still pass unless a change is necessary and explicitly justified.
4. Do not implement cross-mailbox email deduplication yet.
5. Do not silently resolve the conflict between global attachment deduplication and complete email families. Record the chosen interpretation and its trade-off in DECISIONS.md.
6. Run dotnet test and the sample batch through the tool. Report the actual results.
7. Show the files changed, explain the algorithm change, and identify any remaining specification conflicts.

Important: do not merely keep the first document for each hash. Preserve attachment relationships when applying deduplication. Keep the implementation minimal and suitable for a .NET 8 codebase.

---

**Cursor**

The attachment-loss bug is a family being split by a global hash set. I’ll keep first-hash dedup for top-level documents, always emit the attachments of a document that survives, and leave cross-mailbox email dedup untouched.

Tests passed: 8 of 8. The sample run failed because `--nologo` was passed into the tool as an argument. I’ll rerun it with the real two-argument contract.

Each kept email now keeps every attachment that belongs to it, including when another email in the batch has the same file hash. Cross-mailbox email deduplication is unchanged.

`dotnet test` passed **8 tests, 0 failed, 0 skipped** (42 ms). The sample run exited 0 and printed:

`batch-sample: 16 documents in, 15 out, 1 suppressed as duplicates.`

The only suppressed document is still DOC-000013, mlee’s loose `policy.docx`. These three attachment rows are back:

| DocID | Parent | File | Hash prefix |
|---|---|---|---|
| DOC-000005 | DOC-000004 | vendor-report.pdf | `812ffd6153e7…` (same as DOC-000003) |
| DOC-000010 | DOC-000009 | Q3-budget.xlsx | `d0687f0fe9d3…` (same as DOC-000002) |
| DOC-000011 | DOC-000009 | vendor-report.pdf | `812ffd6153e7…` |

DOC-000001 and DOC-000009 are both still in the file, each with both attachments. The header, column order, summary sentence, and `DedupTool <batch.json> <loadfile.csv` contract are unchanged.

## Algorithm

Top-level documents (`parentId` null) still dedupe by `sha256`, first in input order. That is why the existing loose-file tests did not need to change, and why a second email is dropped only when its raw hash matches.

A document with a parent is kept exactly when that top-level ancestor is kept. Its own hash is not a reason to drop it, and it does not occupy the top-level hash set. If the ancestor loses the hash comparison, its attachments are omitted with it, so the load file does not gain orphan rows.

```20:36:src/DedupTool/Deduper.cs
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
```

`FamilyID` and `SentDate` are still taken from the immediate parent. The command, the summary line, and `LoadFileWriter` were not modified.

## Files

- `src/DedupTool/Deduper.cs` — family membership decides attachment inclusion.
- `test/DedupTool.Tests/DeduperTests.cs` — five regressions added. The original three tests are unchanged and passed.
- `DECISIONS.md` — records the section 3.2 / 3.6 versus section 3.5 choice and the trade-off: DOC-000003, DOC-000005, and DOC-000011 are three rows with one PDF hash.

The new tests cover DOC-000004 retaining `vendor-report.pdf` with the shared hash, every retained sample email keeping each input attachment, a nested attachment whose hash was already used, and a duplicate email hash omitting that whole family together. The sample test also locks the deferred behavior: both Q3 mailbox copies remain, and DOC-000013 stays suppressed.

## Remaining spec conflicts

The attachment choice is explicit, not a claim that both spec sentences are satisfied. Sections 3.2 and 3.6 still say duplicate attachment hashes are omitted. This load file keeps them when the emails are different.

These are still open:

- **Section 3.3.** Mailbox copies are not recognized. DOC-000001 and DOC-000009 both remain because their hashes differ. The match key is still undefined, and the input has no message id.
- **Section 4 against loose-file dedup.** mlee still loses `policy.docx` to jsmith’s DOC-000008. One `Custodian` column cannot name both people.
- **Section 4 against a future email dedup.** Suppressing DOC-000009 later would remove that email from mlee’s set. That choice is not made here.
- **Section 6.1.** Which top-level document wins a shared hash still follows input order.
- **Section 5 dates.** jsmith rows are still `2024-03-05T14:22:00-05:00`. The spec’s form for that instant is `2024-03-05T19:22:00Z`.
- **Nested `FamilyID`.** Inclusion walks to the root. The column still uses the immediate parent. This sample has no grandchild, so its family ids are the top-level emails.
- **Quoting, missing parents, duplicate `docId`s.** Unspecified or unexercised. A parent cycle now throws instead of hanging.

---

**User**

Continue from the current implementation. Do not rewrite working code or change the CLI contract.

Implement the following two explicit requirements:

1. SentDate must be emitted in UTC using exactly `yyyy-MM-ddTHH:mm:ssZ`. Convert offset-aware input timestamps to the equivalent UTC instant. Attachment dates must inherit the root email's SentDate.
2. FamilyID must always contain the top-level root document's DocID, not the immediate parent's DocID. Preserve the current attachment-inclusion behavior.

Add focused regression tests for:
- Converting `2024-03-05T14:22:00-05:00` to `2024-03-05T19:22:00Z`.
- Preserving an already-UTC timestamp in the required format.
- An attachment inheriting its root email's UTC date.
- A nested attachment receiving the top-level root's FamilyID.

Keep the existing attachment regression tests passing. Do not implement cross-mailbox email deduplication or change loose-file deduplication in this step. Update DECISIONS.md only to reflect decisions actually implemented. Run `dotnet test` and the sample CLI command, and report the actual results. Do not claim success for any test or requirement that was not verified.

---

**Cursor**

I’ll add UTC `SentDate` formatting and root-level `FamilyID` on top of the current deduper, then lock both with regression tests and run the suite plus the sample batch.

The spec already fixes both of these: dates are UTC instants, and `FamilyID` is the root document. I’ll change only that output mapping and leave hash dedup and the command line as they are.

Both requirements are in place. `dotnet test` passed **12 tests, 0 failed, 0 skipped** (70 ms). The sample command exited 0 and printed `batch-sample: 16 documents in, 15 out, 1 suppressed as duplicates.`

`SentDate` is now the root document’s instant, formatted `yyyy-MM-ddTHH:mm:ssZ`. `FamilyID` is that root’s `DocID`. `ParentID` is still the immediate parent. Attachment inclusion, loose-file dedup, and the command line are unchanged. DOC-000013 is still the only suppressed row, and both Q3 mailbox copies are still present.

The sample CSV shows the conversion:

- DOC-000001 `2024-03-05T14:22:00-05:00` is now `2024-03-05T19:22:00Z`. Its attachments DOC-000002 and DOC-000003 carry that same value, with `FamilyID` DOC-000001.
- DOC-000004 `2024-03-06T09:10:00-05:00` is now `2024-03-06T14:10:00Z`. DOC-000005 inherits it and still has `ParentID` DOC-000004.
- DOC-000009 was already `2024-03-05T19:22:00Z`; DOC-000010 and DOC-000011 inherit it.
- DOC-000015 `2024-03-07T16:00:00Z` is unchanged, and DOC-000016 inherits it.
- Loose rows DOC-000007, DOC-000008, and DOC-000014 still have an empty `SentDate`.

The four new tests passed: offset `2024-03-05T14:22:00-05:00` to `2024-03-05T19:22:00Z`, an already-UTC value left in that form, a direct attachment taking the root email’s UTC date instead of its own, and a nested attachment receiving the top-level `FamilyID` and the root instant. The sample has no grandchild, so the nested `FamilyID` result comes from that unit test, not from the CLI file.

One existing assertion had to move with the date rule. `SampleBatch_Doc000004_KeepsVendorReportDespiteSharedHash` still checks that DOC-000005 is kept with the shared PDF hash and `ParentID` DOC-000004. Its expected `SentDate` is now `2024-03-06T14:10:00Z`.

`DECISIONS.md` records those two spec rules as implemented. A timestamp that is not whole seconds plus `Z` or a numeric offset fails the run. Fractional-second rounding is still undecided because neither the sample nor these tests contain a fraction. Cross-mailbox email dedup and the section 4 loose-file conflict are still open.

---

**User**

Review the current implementation against the original specification and the current DECISIONS.md.

Do not modify code yet.

Focus on:
1. Section 3.3: propose a deterministic cross-mailbox email matching strategy when hashes differ and no message ID is available. Identify false-positive and false-negative risks.
2. Section 4: explain the conflict between global loose-file deduplication and per-custodian completeness.
3. Section 6.1: propose a deterministic survivor-selection rule that does not depend on input order.
4. CSV correctness: check whether fields containing commas, quotes, or line breaks are serialized correctly.
5. Timestamp validation: identify any remaining gaps, including fractional seconds, without changing the current behavior.

For each item, classify it as:
- An explicit requirement that can be implemented now.
- An ambiguity requiring a documented assumption.
- A genuine specification conflict that should be raised with stakeholders.

Recommend the smallest safe next change for this time-limited exercise. Do not claim a requirement is satisfied unless the code and tests demonstrate it.

---

**Cursor**

I’ll review the current deduper, tests, spec, and decisions without changing them, and separate what the code actually proves from what is still an assumption or a conflict.

The smallest safe next change is section 6.1: pick one order-independent survivor and emit rows in a stable order. Cross-mailbox matching, custodian completeness, CSV quoting, and fractional seconds should stay as they are until a decision is written down. The classification is in [Spec gap review](C:\Users\Usuario\.cursor\projects\c-Users-Usuario-Downloads-KLD-Senior-Engineer-Assessment-DedupeTool-1-DedupTool\canvases\spec-gap-review.canvas.tsx).

No repository code was changed. I did call the current library with extra inputs. Those probes are not in the test suite.

## 1. Section 3.3 — ambiguity, then a stakeholder conflict

**Class:** the duty to recognize mailbox copies is explicit. The match key is an assumption. Suppressing the losing custodian’s copy conflicts with section 4.

The code still dedupes top-level documents only by `sha256`, first in input order. DOC-000001 and DOC-000009 have different hashes, and `SampleBatch_EveryRetainedEmail_ContainsEachOfItsAttachments` asserts that both emails, plus DOC-000010 and DOC-000011, are emitted. Section 3.3 is not satisfied. There is no message id on the model.

A key that fits the one pair in the sample, and that does not merge the forward or the reply:

Compare two top-level emails only when all of these are equal:

- UTC instant, using the conversion already implemented. No tolerance.
- `from`, trimmed, ordinal case-insensitive, as the whole string. No display-name parser.
- `to`, each entry trimmed and case-folded, compared as a set.
- `subject`, trimmed, ordinal, with `Re:` and `FW:` left in place.
- `body` after converting CR and CRLF to LF. No other whitespace folding.

Leave `sha256`, `sizeBytes`, `custodian`, `docId`, and `fileName` out of the key. DOC-000001 and DOC-000009 match on this key: same instant, same subject, same single recipient, address case and line endings are the only body/`from` differences. DOC-000004 and DOC-000012 do not: different subject, body, recipients or sender, and a different instant.

False positives if the key is loosened:

- Stripping `Re:` or `FW:` would treat the forward and the reply as the Q3 email. `DECISIONS.md` already rejects that.
- Ignoring subject or body would do the same.
- A mail gateway that adds a disclaimer, or two different messages that share this header and body, is not in the sample. The key cannot show that those will stay apart.

False negatives if the key is strict in the ways the sample does not cover:

- `John Smith <john.smith@example.com>` versus the bare address. The sample has bare addresses only.
- Recipient order, if the lists are compared as sequences. The sample’s pair has one recipient.
- A one-second difference between Date headers. The sample’s instants match.
- A body difference other than line endings. The spec mentions encodings; this input’s `body` is already plain text, and the sample only shows LF versus CRLF.

Even with that key, dropping DOC-000009 removes mlee’s email and both of its attachments, while the CSV can name only jsmith. That is the section 4 conflict. Recognition can be written down now. Omission should wait for the stakeholders.

## 2. Section 4 — stakeholder conflict

**Class:** genuine specification conflict. Do not pick a winner in code.

Sections 3.1, 3.4, and 3.6 say identical loose-file hashes are duplicates across the whole batch and the duplicate is omitted. Section 4 says each custodian’s set must stand alone if that custodian is later severed. Section 1 also says reviewers should not see the same content twice. The load file has one `Custodian` column, and the column list is frozen, so the surviving row cannot name both people.

The sample and the tests implement the global reading. `IdenticalLooseDocuments_SecondIsSuppressed` keeps the first loose file. The sample test keeps DOC-000008 and asserts DOC-000013 is absent. mlee’s production is then missing `policy.docx`. Keeping DOC-000013 would satisfy section 4 and contradict 3.1 and 3.4. The same shape appears if section 3.3 later suppresses DOC-000009. `DECISIONS.md` already leaves this open. It still is.

## 3. Section 6.1 — explicit requirement; the tie-break is an assumption

**Class:** implement the stability requirement now. Document the winner and the meaning of “same file.”

```28:33:src/DedupTool/Deduper.cs
        foreach (var d in batch.Documents)
        {
            if (d.ParentId != null)
                continue;
            if (seenRootHash.Add(d.Sha256))
                keptRoots.Add(d.DocId);
        }
```

Rows are then written in input order. No test permutes the batch. The loose-file test asserts that `A`, the earlier document, survives. Section 6.1 is not demonstrated.

Section 5 says row order is not significant, while 6.1 says the same load file. Those can mean the same rows in any order, or the same bytes. A rule that satisfies the stricter reading:

- Among top-level documents with the same `sha256`, keep the ordinal-minimum `docId`. `docId` is a string, so this is character order, not a numeric parse. `DOC-10` sorts before `DOC-9`. The sample ids are zero-padded, so the two readings agree there.
- Emit the surviving rows sorted by `docId`. Section 5 allows that. A child may then appear before its parent; `ParentID` and `FamilyID` still link them.
- Attachments stay with whichever root won. Their own hashes still do not suppress them.

On this sample the only top-level hash collision is DOC-000008 and DOC-000013. DOC-000008 is both the first document and the lower id, and the input is already in id order. That the sample CSV would be unchanged is a deduction from those ids, not a permutation run.

Earliest `sentDate` is a weaker tie-break: loose files have no date. Ordinal `docId` exists on every top-level document.

## 4. CSV — ambiguity; ask before changing the writer

**Class:** the spec never defines escaping. Quoting and not quoting can both break ingest. Raise it. Do not treat the sample file as proof.

`LoadFileWriter.ToCsv` joins the eight fields with commas and appends LF. It does not quote. The only CSV test checks that the text starts with the header. The sample’s custodians and file names have spaces and no comma, quote, or line break, so that file stays on eight columns. I ran three rows through the current writer:

- Custodian `smith, jr` produced an extra column.
- File name `a,"b".txt` copied the quote marks through.
- A file name containing a newline became two physical lines.

Spaces in names such as `Q3 budget review.eml` do not shift columns. A comma, quote, or newline does. RFC 4180 quoting would fix that and would also change bytes the ingest job has never been shown to accept. The spec’s warning is about the header and column order, not about quotes. That is an ingest question, not a dedup rule.

## 5. Timestamps — tested forms pass; other forms are unverified or wrong

**Class:** ambiguity. Leave the behavior. The output rule for whole-second `Z` and offset inputs is implemented and tested. Fractional policy and missing offsets are not.

Demonstrated by tests: `2024-03-05T14:22:00-05:00` becomes `2024-03-05T19:22:00Z`; `2024-03-05T19:22:00Z` stays that string; an attachment and a nested attachment take the root instant. The parser is `yyyy-MM-dd'T'HH:mm:ssK`.

I also ran forms the suite does not cover:

- `2024-03-05T19:22:00.1Z` and `2024-03-05T19:22:00.500Z` throw `InvalidOperationException`. Nothing is rounded or truncated. `DECISIONS.md` already says rounding is not decided. ISO 8601 allows a fraction; the output spec forbids one. Rejecting is a choice, and no test locks the exception.
- `2024-03-05T19:22:00+00:00` and `2024-03-05T14:22:00-0500` both emitted `2024-03-05T19:22:00Z`. That is current behavior, not a test.
- `2024-03-05 14:22:00-05:00` throws. The spec’s example uses `T`.
- `2024-03-05T19:22:00` with no offset did not throw. On this UTC-6 machine it became `2024-03-06T01:22:00Z`. The same bytes would become a different instant on another machine. `DECISIONS.md` says a value without `Z` or a numeric offset fails the run. The code does not do that, and no test covers it. Spec-conforming email dates in the sample all have an offset, so the sample path does not hit this.

Loose files take the empty-string branch in `SentDateFor`. The sample run showed empty `SentDate` cells for them. No test asserts that cell.

## Next change

Implement only the section 6.1 rule above, record the ordinal-`docId` assumption in `DECISIONS.md`, and add a permutation test that would fail on today’s first-seen loop. Keep the attachment tests, the loose-file tests, and the UTC tests as they are.

Leave these for a written decision or an ingest answer: the section 3.3 key, whether a cross-custodian duplicate is omitted, CSV quoting, and fractional seconds. The missing-offset local conversion is a real hole, and closing it would change behavior the decisions file already describes as a failure. It is still a separate, smaller patch from 6.1, and it is not a reason to reopen the formats the tests already lock.

---

**User**

Implement only the deterministic behavior required by section 6.1.

Requirements:
1. For top-level documents with the same SHA-256 value, retain the document with the ordinally smallest DocID using StringComparer.Ordinal.
2. Emit output rows in ascending ordinal DocID order, including attachments. Do not assume a parent must appear before its children.
3. Preserve the current family-integrity behavior: attachments are included or excluded with their root, and attachment hashes do not suppress attachments.
4. Do not implement cross-mailbox email matching or change loose-file deduplication rules beyond deterministic survivor selection.
5. Add regression tests that run the same batch in multiple input orders and verify the same surviving documents and identical CSV output. Include a shared-hash case where the ordinally smaller DocID appears later in the input.
6. Keep all existing tests passing.
7. Document the ordinal DocID tie-break and output ordering in DECISIONS.md. Do not claim byte-for-byte determinism unless the tests verify it.
8. Run dotnet test and the sample CLI command. Report actual results and files changed.

Do not modify timestamp handling, CSV serialization, or other unrelated behavior in this change.

---

**Cursor**

Section 6.1 is the only change: the smallest `DocID` wins a shared top-level hash, and rows are emitted in that same order. I’ll leave dates, CSV quoting, and email matching as they are.

Section 6.1 is now deterministic. `dotnet test` passed **14 tests, 0 failed, 0 skipped** (66 ms). The sample command exited 0 and printed `batch-sample: 16 documents in, 15 out, 1 suppressed as duplicates.`

The kept set is unchanged on that batch. DOC-000008 is still the surviving `policy.docx`, and DOC-000013 is still the only suppressed document. Rows come out in ascending `DocID` order. Both Q3 emails remain, each with its attachments. Dates and the CSV writer were not changed.

Among top-level documents that share a `sha256`, the survivor is the ordinally smallest `DocID` (`StringComparer.Ordinal`). Attachments are still included or excluded with that root, and an attachment hash still does not suppress another attachment. After selection, every surviving row is sorted with the same comparer, so a child can sort before its parent.

Two new tests passed. `SharedHash_KeepsOrdinalSmallestDocId_WhenItAppearsLater` puts DOC-000013 before DOC-000008 and keeps DOC-000008. `InputOrder_DoesNotChangeSurvivorsOrCsv` runs one batch in three orders, including a reversed order where the smaller ids and an attachment appear after or before their counterparts. All three produce the survivors `A-loose`, `A-mail`, and `M-child`, and the same `LoadFileWriter.ToCsv` text. That test checks writer text. It does not compare the bytes `File.WriteAllText` stores on disk.

Two existing assertions had to follow the new order. `IdenticalEmailHash_OmitsTheSecondFamilyTogether` now expects `A1`, `E1`. `NestedAttachment_StaysWithTheKeptEmail_WhenTheHashAlreadyAppeared` now expects `A1`, `A2`, `E1`, `E2`, `MSG`. The kept documents, parent links, and suppression counts in those tests are the same.

Files changed: `src/DedupTool/Deduper.cs`, `test/DedupTool.Tests/DeduperTests.cs`, and `DECISIONS.md`. The ordinal tie-break and the `DocID` sort are recorded there. Cross-mailbox matching and the section 4 loose-file conflict are still open.

---

**User**

Fix the timestamp validation inconsistency documented in DECISIONS.md.

A timestamp representing an email date must include an explicit UTC designator (Z) or numeric UTC offset. Reject timestamps without an explicit timezone instead of interpreting them using the machine's local timezone.

Add tests proving:
- A timestamp ending in Z is accepted.
- A timestamp with a numeric UTC offset is accepted and converted correctly to UTC.
- A timestamp without an explicit timezone is rejected consistently, regardless of the machine's local timezone.
- Existing attachment date inheritance, nested FamilyID, and loose-file empty SentDate behavior remain unchanged.

Do not introduce a fractional-second rounding policy. Keep fractional-second behavior unchanged and document it as unresolved.

Run dotnet test and the sample CLI command, report actual results, and update DECISIONS.md to reflect the verified behavior only.

---

**Cursor**

The missing-offset path still falls through to the machine’s local zone. I’ll reject those timestamps before any conversion, and leave fractional-second handling as it is.

Timestamps without an explicit zone are now rejected before any conversion. `dotnet test` passed **16 tests, 0 failed, 0 skipped** (67 ms). The sample command exited 0 and printed `batch-sample: 16 documents in, 15 out, 1 suppressed as duplicates.`

The sample dates are unchanged. DOC-000001 is `2024-03-05T19:22:00Z`, and DOC-000002 and DOC-000003 carry that value. DOC-000005 is `2024-03-06T14:10:00Z`. Loose rows DOC-000007, DOC-000008, and DOC-000014 still have an empty `SentDate`. DOC-000013 is still the only suppressed document.

`yyyy-MM-dd'T'HH:mm:ssK` treats a missing offset as the machine zone. The parser now runs only when the text ends in `Z` or `z`, or has a numeric offset after the date. `2024-03-05T19:22:00` throws `InvalidOperationException`. The test checks that throw and does not encode a local offset, so the expected result is the same in every timezone.

The suite covers the cases you listed:

- `UtcSentDate_IsPreservedInTheRequiredFormat` keeps `2024-03-05T19:22:00Z`.
- `OffsetSentDate_IsConvertedToUtc` turns `2024-03-05T14:22:00-05:00` into `2024-03-05T19:22:00Z`.
- `SentDate_WithoutExplicitTimeZone_IsRejected` rejects the bare timestamp.
- `Attachment_InheritsRootEmailUtcSentDate`, `NestedAttachment_ReceivesTopLevelFamilyIdAndRootSentDate`, and `LooseFile_SentDate_StaysEmpty` passed. The loose-file test also keeps `SentDate` empty when the loose document itself holds an offset timestamp.

Fractional seconds are unchanged and still unresolved. A separate call, not a unit test, showed `2024-03-05T19:22:00.1Z` and `2024-03-05T19:22:00.500Z` still throw `InvalidOperationException`. No rounding or truncation rule was added. `DECISIONS.md` says that, and it does not treat the throw as a chosen policy.

Files changed: `src/DedupTool/Deduper.cs`, `test/DedupTool.Tests/DeduperTests.cs`, and `DECISIONS.md`.

---

**User**

Review LoadFileWriter.cs and the CSV requirements in the original specification.

Do not modify code.

1. Identify the exact behavior for fields containing commas, double quotes, CR, or LF.
2. Explain how these values could break the eight-column output.
3. Determine whether the current tests cover CSV escaping and exact column counts.
4. Check the repository for any documented downstream CSV format or parser expectations.
5. Recommend the smallest safe change, distinguishing confirmed requirements from assumptions about the downstream consumer.

Do not claim that RFC 4180 compatibility is required unless the specification or repository establishes it. Report findings and suggested regression tests only.

---

**Cursor**

I’ll review the writer, the spec, and the tests as they stand, and look through the repo for any ingest or CSV parser contract before recommending a change.

The writer copies commas, quotes, and line breaks through as raw characters. The specification fixes the header and the eight-column order, and it does not require RFC 4180 quoting. No code was changed.

## Exact behavior

`ToCsv` writes the header, then each row with `string.Join(",", ...)` and a single `\n`. Nothing is wrapped in quotes, and a quote inside a value is not doubled. I ran the current writer on one row at a time.

A comma is another delimiter. Custodian `smith, jr` produced:

`D,F,P,smith, jr,loose,plain.txt,,H`

That data line contains 8 commas, so a comma split sees 9 fields. `smith` and ` jr` land in different columns.

A double quote is copied and does not add a column by itself. File name `say"hello".txt` produced:

`D,F,P,smith,loose,say"hello".txt,,H`

That line has 7 commas, which is still 8 fields, and both quote characters are still in the file name.

A CR stays inside the line. File name `a\rb.txt` stayed on one data line, still with 7 commas, and the CR remained between `a` and `b`. The writer only breaks records on the `\n` it appends.

An LF breaks the physical record. File name `a\nb.txt` produced two data lines:

`D,F,P,smith,loose,a`
`b.txt,,H`

The first of those has 5 commas. A CR+LF in the file name did the same, with the CR left at the end of the first fragment.

Empty `ParentID` or `SentDate` becomes a pair of commas, `,,`. That is one empty column, not a missing column. The sample already uses that shape.

## How the eight columns break

The ingest contract in section 5 is those eight names in that order. A comma in `Custodian` or `FileName` shifts everything after it, so `DocType`, `SentDate`, and `Hash` are read from the wrong text. An LF does that and also creates a second physical line with no header of its own. A CR does not change a split-on-`\n` count, but a reader that splits on `\r`, `\n`, or `\r\n` can still cut the row. A quote does not move the comma boundaries unless the field also contains a comma. A reader that treats a field starting with `"` as a quoted field would instead strip or reinterpret those characters. `string.Join` does not do that.

`DocType`, `SentDate`, and `Hash` are produced in forms that contain none of these characters. `DocID`, `FamilyID`, `ParentID`, `Custodian`, and `FileName` are copied from input. In this sample, the file names have spaces only. Spaces do not shift columns. `Q3 budget review.eml` is unquoted in `data/current-tool-output.csv`. Newlines in email bodies stay in the JSON. They are not CSV columns.

## What the tests cover

They do not cover escaping or an exact column count.

`LoadFile_StartsWithFixedHeader` checks that an empty file starts with the header text. A header with an extra column after `Hash` would still pass. It never inspects a data line.

`InputOrder_DoesNotChangeSurvivorsOrCsv` requires the same CSV text across input orders. Those fixture names are `shared.pdf`, `z.pdf`, and similar. None contain a comma, quote, CR, or LF, and the test never counts fields.

The other tests assert row fields in memory. They do not split the CSV.

## What the repository says about the consumer

Section 5 says the file is a CSV, the header text and column order are fixed, and the ingest job breaks if those change. It does not mention quotes, embedded commas, CR, LF inside a field, or RFC 4180.

The only other format note is the comment in `LoadFileWriter`: the ingest job expects LF record endings, and the column order should not change without the ingest team. `README.md` says to preserve the load-file format. `DECISIONS.md` records that quoting is still open because the sample fields have no comma, quote, or newline. There is no parser, schema, or example of a quoted field in the repository. `current-tool-output.csv` is an unquoted baseline, including empty columns and spaces in file names.

## Smallest safe change

Leave `LoadFileWriter` as it is. Quoting is an assumption about the consumer. The spec does not require it, and quoting every field would change rows the existing output leaves unquoted, including file names that contain only spaces. Quoting only when a comma, quote, or line break is present would leave this sample unchanged, but only if the ingest job understands quotes. That is not established.

The safe addition is tests, not a new serializer:

- Split the header and each sample data line on `\n`, then on comma, and require exactly 8 fields. Require no `\r` in that text. This locks the contract the sample actually meets, which `StartsWith` does not.
- One characterization test each for a comma, a quote, a CR, and an LF, asserting today’s raw `string.Join` text. That makes a later quoting change visible. It does not define the eventual ingest rule.
- Hold the desired encoded form until the ingest team says whether a quoted field is accepted. Do not treat RFC 4180 as that answer.

---

**User**

You are working inside the existing DedupTool repository. Make changes directly in the local workspace. Follow `SPEC.md` as the source of truth. Do not ask the user to paste source code into an external service.

## Goal
Implement cross-mailbox email deduplication according to section 3.3 while preserving family integrity and deterministic output.

## Before changing code
1. Read `SPEC.md`, `DECISIONS.md`, `README.md`, all source files, and existing tests.
2. Understand the current models and how JSON fields are deserialized.
3. Identify any contradictions between global deduplication, complete email families, and each custodian's self-contained production set. Do not silently ignore these constraints.
4. Make the smallest safe set of changes and explain the design decisions in `DECISIONS.md`.

## Email identity
Emails collected from different mailboxes can represent the same message even when their raw SHA-256 hashes differ due to header encoding, casing, or line endings.

Implement a deterministic email identity key using only fields available in the input schema. At minimum, evaluate:
- Sender address normalized case-insensitively.
- SentDate normalized to a UTC instant, respecting the explicit ISO-8601 offset.
- Subject normalized for casing and surrounding whitespace.
- Plain-text body normalized for CRLF/LF line endings and consistent whitespace handling.
- Recipients normalized case-insensitively and sorted deterministically if the specification supports treating recipient order as irrelevant.

Do not use filename, custodian, or raw email SHA-256 as the sole email identity. Do not assume that `Re:` messages are duplicates merely because their subjects match. Avoid overly aggressive normalization that could merge distinct messages. Document the chosen normalization policy and its limitations.

## Family integrity
- If an email is retained, retain all attachments in its parent chain, including nested attachments.
- Do not deduplicate attachment rows in a way that strips an attachment from a retained family.
- Ensure nested parent chains are handled safely and cycles do not cause infinite loops.
- Choose retained representatives deterministically, independent of input document order.

## Custodian completeness
`SPEC.md` requires each custodian's production set to be complete and self-contained. Analyze how this requirement interacts with global cross-mailbox email deduplication.
Do not invent an undocumented policy. Implement the interpretation best supported by the specification and clearly document any unavoidable trade-off. If both requirements cannot be satisfied by a single global CSV row set, explain the conflict and choose the least destructive, specification-consistent behavior.

## Tests
Add or update automated tests to cover:
1. Same email with different raw hashes, sender casing, equivalent time zones, and CRLF versus LF body line endings.
2. Different emails with similar subjects but different senders, timestamps, or bodies must not be incorrectly merged.
3. Input order does not change the output.
4. A retained email keeps all of its attachments.
5. Nested attachments and parent-chain safety.
6. Existing loose-file deduplication by SHA-256 remains correct.
7. Shared attachment hashes do not remove attachments from otherwise retained families.
8. Invalid or missing email identity fields are handled predictably, without accidental merging.
9. The sample batch behavior is explicitly tested.

## CSV and timestamps
Review the CSV writer against `SPEC.md`. Preserve the exact required header and column order. Properly escape CSV fields containing commas, quotes, or newlines according to standard CSV rules if compatible with the required format. Accept ISO-8601 timestamps with explicit offsets, including fractional seconds if supported by the schema, and emit the specified UTC representation consistently.

## Performance and determinism
- Avoid unnecessary nested scans; use dictionaries or hash sets where appropriate.
- Keep the implementation practical for batches of up to approximately 2 million documents.
- Avoid reliance on input ordering, current culture, local machine timezone, or nondeterministic collection iteration.

## Validation
1. Run `dotnet test`.
2. Run `dotnet build`.
3. Run the CLI against `data/batch-sample.json` and write the generated CSV to a temporary output file.
4. Verify the exact CSV header and sample output counts.
5. Inspect `git diff` to ensure only intentional source, test, and documentation changes are present.
6. Do not commit or push anything.
7. Report the commands executed, test results, sample counts, changed files, assumptions, and remaining limitations honestly.

Do not claim all requirements are satisfied if a requirement remains unresolved. Preserve the existing project structure and .NET target framework.

---

**Cursor**

I’ll read the spec, decisions, and current code first so cross-mailbox dedup follows the existing family and ordering rules instead of inventing a second policy.

Section 3.3 and section 4 cannot both be true in one CSV: a duplicate mailbox copy has to be omitted, and the file has only one custodian column. I’ll suppress the whole duplicate family, keep the ordinally smallest email with its attachments, and record that trade-off.

Cross-mailbox copies now collapse to one family. `dotnet test` passed **22 tests, 0 failed, 0 skipped** (96 ms). `dotnet build` finished with **0 warnings and 0 errors**. The sample command exited 0 and printed `batch-sample: 16 documents in, 12 out, 4 suppressed as duplicates.`

The header is `DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash`. The 12 rows are DOC-000001 through DOC-000008, then DOC-000012, DOC-000014, DOC-000015, and DOC-000016. DOC-000001 is kept with both attachments. DOC-000009 and its two attachments are omitted because that message matches DOC-000001 after address case, UTC instant, and line-ending normalization, and `DOC-000001` is the smaller id. DOC-000004 still has `vendor-report.pdf`. The reply DOC-000012 stays. DOC-000013 is still the omitted loose file. Nothing was committed or pushed. `git status` shows only `DECISIONS.md`, `src/DedupTool/Deduper.cs`, `src/DedupTool/LoadFileWriter.cs`, and `test/DedupTool.Tests/DeduperTests.cs`.

Two top-level emails match when `from`, `subject`, and each `to` entry are trimmed and lowercased with the invariant culture, `sentDate` is the same UTC instant, and `body` matches after CR/CRLF become LF and the ends are trimmed. Recipient order is kept, because the spec does not say order is irrelevant. `Re:` and `FW:` are not removed. File name, custodian, and raw `sha256` are not that key. A missing or blank `from`, a null `subject`, `body`, or `to`, or a `sentDate` without an explicit zone does not form a key, so those emails are not merged unless the raw hashes match. The survivor in each class is still the ordinal-minimum `DocID`, and rows are still sorted that way. A kept email keeps every descendant. A parent cycle throws.

Section 4 is not satisfied. The CSV cannot name both custodians, so mlee's copy of the Q3 email is gone with its attachments, and mlee's `policy.docx` is still gone. Keeping both copies would ignore section 3.3. One complete family is the smaller loss, and `DECISIONS.md` says so.

Also still limited: a display name will not match a bare address, a reordered recipient list will not match, and a body change other than line endings or surrounding whitespace will not match. Fractional timestamps are accepted and truncated toward zero to a whole second, so `19:22:00.900Z` stays `19:22:00Z`. Different fractions remain different messages. Fields are quoted only when they contain a comma, quote, or line break. That is an assumption that the ingest job accepts quotes. The spec does not name RFC 4180. This sample's plain fields stay unquoted. The tests compare `ToCsv` text. They do not compare the bytes `File.WriteAllText` writes to disk.

---

**User**

Perform a final specification-compliance review of the current DedupTool implementation. Do not commit or push.

1. Re-read SPEC.md, DECISIONS.md, and the current implementation and tests.
2. Verify that email identity compares the full UTC instant, including fractional seconds. Do not truncate timestamps before identity comparison. Add tests for timestamps that differ only by fractional seconds and for equivalent timestamps with different offsets.
3. Review the contradiction between global email deduplication (section 3.3) and complete self-contained custodian productions (section 4). Determine whether the current single-CSV design can satisfy both. Do not silently change behavior or claim compliance if it cannot. Document the trade-off and any clarification required from the assessment owner.
4. Review CSV escaping and newline behavior against the exact load-file requirements. Add tests for commas, quotes, embedded newlines, and the actual file-writing path, not only ToCsv string output.
5. Verify that malformed or missing identity fields cannot accidentally merge unrelated emails.
6. Check determinism, family integrity, nested attachments, cycle detection, and behavior independent of input order.
7. Review algorithmic complexity for batches of approximately 2 million documents; avoid quadratic comparisons.
8. Run dotnet test, dotnet build, and the sample CLI command. Report exact results and all unresolved requirements.
9. Do not change requirements or invent assumptions. Make only justified code/test/documentation changes. Do not commit or push.

At the end, provide a concise compliance table mapping each requirement in SPEC.md to implementation evidence, tests, and any unresolved limitation.

---

**Cursor**

I’ll re-read the spec, decisions, and current tests first, then add only the checks that are still missing: full-precision identity, file-level CSV output, and a complexity pass that stays linear.

Identity already uses the full UTC tick count, and output truncation happens only when the CSV date is formatted. I’ll lock that with tests, record that one CSV cannot satisfy both section 3.3 and section 4, and memoize parent walks so a long chain stays linear.

A single CSV cannot satisfy both section 3.3 and section 4. Behavior was left as global omission of the duplicate family. `dotnet test` passed **25 tests, 0 failed, 0 skipped** (94 ms). `dotnet build` finished with **0 warnings and 0 errors**. The sample command exited 0 and printed `batch-sample: 16 documents in, 12 out, 4 suppressed as duplicates.` The header is `DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash`, followed by 12 data rows. Nothing was committed or pushed.

Email identity already stored `ToUniversalTime().Ticks` before the CSV second is truncated. Two new tests lock that. `19:22:00.100Z` and `19:22:00.200Z` both stay in the file and both print `2024-03-05T19:22:00Z`. `19:22:00.500Z` and `14:22:00.500-05:00` collapse to `A-copy`, and the printed date is still `2024-03-05T19:22:00Z`. Null `subject` and null `body` now sit with the existing missing-field case: those emails are not merged when their hashes differ. `Write` was checked by reading the temp file's bytes back and comparing them with `ToCsv`, including a comma, a doubled quote, and an embedded newline. Parent walks are memoized, so cost follows the number of documents and links rather than a pairwise comparison. That was not timed on a two-million-document batch.

Section 4 remains unmet. DOC-000009 and its attachments are omitted with mlee, and DOC-000013 is still omitted. The header cannot name both custodians. `DECISIONS.md` asks the assessment owner whether a severed custodian must still receive that duplicate family. Quoted fields are still an assumption: the spec never mentions them, and the ingest job has not said it accepts quotes.

| Spec | Evidence | Tests | Limitation |
|---|---|---|---|
| 1. Same content once, with its context | Kept family is complete; duplicate family is omitted | Sample ids; attachment and nested tests | Section 4 copies are not produced |
| 2. JSON document fields | Case-insensitive deserialize in `Program.Main` | Sample file is loaded by tests | No separate test for a missing parent |
| 3.1 Loose files, same `sha256` | Global hash class, smallest `DocID` | `IdenticalLooseDocuments_SecondIsSuppressed`; sample omits DOC-000013 | Losing custodian is the section 4 gap |
| 3.2 Attachments, same `sha256` | Not applied | DOC-000004 keeps `vendor-report.pdf` | Conflicts with 3.5; 3.5 was chosen |
| 3.3 Mailbox copies | Full UTC ticks plus normalized from, subject, body, and ordered `to` | Cross-mailbox test; fraction tests; sample drops DOC-000009 | Key is not in the spec. Display names, reordered recipients, and body edits beyond line endings do not match |
| 3.4 Across the batch | One union-find over top-level documents | Sample mixes jsmith and mlee | Same section 4 consequence |
| 3.5 Family intact | Descendants follow the kept root | Nested `FamilyID`; shared-hash attachments; sample | Suppressed root takes its attachments with it |
| 3.6 Duplicates omitted | Not written to the CSV | Suppressed counts | — |
| 4. Custodian set stands alone | Cannot, with this header | Sample lacks mlee's Q3 family and `policy.docx` | Unresolved. Owner must say whether the duplicate family is re-emitted |
| 5. Header and eight columns | Exact header string; quote only `,`, `"`, CR, LF | Header test; sample line splits; `Write_StoresTheSameTextAsToCsv` | Quote acceptance by ingest is not specified |
| 5. `FamilyID` / `ParentID` / `SentDate` | Root id; immediate parent; UTC whole second, fraction truncated only on output | Nested test; offset, `Z`, and fraction tests | Truncation rather than rounding is a documented choice |
| 5. Row order insignificant | Rows sorted by ordinal `DocID` | `InputOrder_DoesNotChangeSurvivorsOrCsv` compares writer text | Disk bytes are covered by the `Write` test, not by the order test |
| 6.1 Same file for any input order | Winner is minimum `DocID`; rows are sorted | Order test and two orders of the mailbox pair | — |
| 6.2 About two million documents | Hash map plus union-find; roots memoized | Not run at that size | No measured timing |
| 6.3 One summary line | Sample process printed the in/out/suppressed line | No unit test around `Main` | — |

