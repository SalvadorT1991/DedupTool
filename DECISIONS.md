# Decisions

Keep this short and honest. Bullet points are fine. We read this before we read your code.

## Where the spec was unclear, contradictory, or silent

- **Question:** Sections 3.2 and 3.6 say two attachments with the same `sha256` are duplicates and suppressed duplicates are omitted. Section 3.5 says a family is produced intact, so a reviewer who opens an email sees every attachment that was on it. The sample hits both at once: `vendor-report.pdf` is DOC-000003 on DOC-000001 and DOC-000005 on DOC-000004.
- **Decision:** Family integrity wins for attachments. A top-level document is still deduplicated by `sha256`. The survivor is the ordinally smallest `DocID` (`StringComparer.Ordinal`), not the first row in the file. A document with a `parentId` is emitted when its top-level ancestor is emitted, and omitted when that ancestor is omitted. The same attachment hash may therefore appear on more than one kept email.
- **Also considered:** Keep suppressing attachment rows by hash, which is what the previous code did and what a literal reading of 3.2 requires. That is the reported bug: DOC-000004 stayed in the load file and lost its only attachment. Re-parenting the surviving PDF onto every email that had contained it would show the right bytes on the wrong `DocID` / `ParentID`.
- **Trade-off:** The load file is no longer globally unique by attachment hash. DOC-000003, DOC-000005, and DOC-000011 are three rows with one PDF hash. I did not ask the team; the ticket and section 3.5 describe the reviewer-visible failure, and section 3.2 cannot be satisfied at the same time for two different emails that happen to carry the same file.
- **Question:** Section 6.1 requires the same load file regardless of input order. Section 5 says row order is not significant. The spec does not say which duplicate wins.
- **Decision:** Among top-level documents with the same `sha256`, keep the ordinally smallest `DocID`. Emit every surviving row, attachments included, in ascending ordinal `DocID` order. A child may sort before its parent. `ParentID` is still the immediate parent. Loose-file duplicates are still global; only the survivor changed from "first in the file" to "smallest DocID". On the sample, DOC-000008 remains and DOC-000013 is still omitted, because `DOC-000008` is the smaller id.
- **Not decided here:** Section 3.3 versus section 4 (cross-mailbox copies versus a self-contained custodian set). The fixed CSV has one `Custodian` column, so one surviving row cannot name both custodians. Cross-mailbox email dedup is not implemented. mlee's set is still missing `policy.docx`. That section 4 conflict remains open.

## What I changed and why

- `Deduper` no longer puts every document through one hash set. Top-level documents (`parentId` null), including loose files and emails, still collapse by `sha256`. The kept id is the ordinal minimum, so a later smaller `DocID` wins over an earlier larger one. A second email is dropped only when the raw hash actually matches.
- Attachments follow the parent chain. Shared hashes across kept families stay. Attachments of a suppressed top-level document are suppressed with it, so the load file does not gain orphaned children.
- A parent cycle throws instead of hanging. The spec says the parent is always present and says nothing about cycles.
- `FamilyID` is the top-level document's `docId` for every row in that family, including a nested attachment. `ParentID` stays the immediate parent. This is section 5, which already stated it; the previous code used one parent hop.
- `SentDate` is the root document's timestamp written as `yyyy-MM-ddTHH:mm:ssZ`. Tests verify `2024-03-05T19:22:00Z` stays that string, and `2024-03-05T14:22:00-05:00` becomes `2024-03-05T19:22:00Z`. Attachments use the root email's instant. A nested attachment's `FamilyID` is the top-level id. Loose files stay empty, including a loose file that carries a timestamp string. The format specifier `K` treats a missing offset as the machine's local zone. The code now rejects that shape before parsing. `2024-03-05T19:22:00` throws `InvalidOperationException`. The test asserts the throw and does not encode a local offset, so the expected result is the same in every timezone. Fractional seconds still do not match the whole-second pattern. No rounding or truncation rule is chosen, and no test defines one.
- Surviving rows are sorted by `DocID` with `StringComparer.Ordinal` before the CSV is written. Tests compare `LoadFileWriter.ToCsv` across several input orders and require the same text. That is identical CSV text from the writer. It is not a check of the bytes `File.WriteAllText` puts on disk.
- Command line, summary sentence, header, and column order are unchanged.

## What I didn't get to

- Cross-mailbox email identity (section 3.3). DOC-000001 and DOC-000009 both remain. The spec does not define the match key, and there is no message id on the document.
- Section 4 for loose files. Dedup is still global. The sample still omits DOC-000013.
- CSV quoting. The sample's output fields have no comma, quote, or newline.
- Input the spec says will not happen: missing parent, duplicate `docId`, empty `sha256`.

## What I'd do next

- Write down the email match key and whether dedup is per custodian or per batch before coding section 3.3. A key that strips `FW:` / `Re:` or compares raw bodies would collapse DOC-000004 or DOC-000012 into the Q3 email; those are different messages in the sample.
- Decide section 4 for loose files separately. The current CSV cannot list both custodians on DOC-000008. Do not treat `data/current-tool-output.csv` as the oracle; it is the attachment-loss baseline (16 in, 12 out, 4 suppressed).

## If you reimplemented in another language

Stayed on .NET 8. The command is still `DedupTool <batch.json> <loadfile.csv>`, the summary sentence is unchanged, and the header is still `DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash`.

## Working with the AI

The review correctly separated the hash-set bug from unimplemented section 3.3, and the reversal run showed the survivor set really does depend on order. I rejected treating a normalized from/subject/body key as a requirement: the spec never defines it. The implementation follows the written choice (keep attachment rows on every kept email, do not dedupe mailbox copies yet) rather than a literal reading of section 3.2.
