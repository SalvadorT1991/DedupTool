# Decisions

Keep this short and honest. Bullet points are fine. We read this before we read your code.

## Where the spec was unclear, contradictory, or silent

- **Question:** Sections 3.2 and 3.6 say two attachments with the same `sha256` are duplicates and suppressed duplicates are omitted. Section 3.5 says a family is produced intact, so a reviewer who opens an email sees every attachment that was on it. The sample hits both at once: `vendor-report.pdf` is DOC-000003 on DOC-000001 and DOC-000005 on DOC-000004.
- **Decision:** Family integrity wins for attachments. A top-level document is still deduplicated by `sha256` (first in input order). A document with a `parentId` is emitted when its top-level ancestor is emitted, and omitted when that ancestor is omitted. The same attachment hash may therefore appear on more than one kept email.
- **Also considered:** Keep suppressing attachment rows by hash, which is what the previous code did and what a literal reading of 3.2 requires. That is the reported bug: DOC-000004 stayed in the load file and lost its only attachment. Re-parenting the surviving PDF onto every email that had contained it would show the right bytes on the wrong `DocID` / `ParentID`.
- **Trade-off:** The load file is no longer globally unique by attachment hash. DOC-000003, DOC-000005, and DOC-000011 are three rows with one PDF hash. I did not ask the team; the ticket and section 3.5 describe the reviewer-visible failure, and section 3.2 cannot be satisfied at the same time for two different emails that happen to carry the same file.
- **Not decided here:** Section 3.3 versus section 4 (cross-mailbox copies versus a self-contained custodian set). The fixed CSV has one `Custodian` column, so one surviving row cannot name both custodians. Cross-mailbox email dedup is not implemented. Loose-file dedup stays global and first-seen, so DOC-000013 is still omitted and mlee's set is still missing `policy.docx`. That section 4 conflict remains open.

## What I changed and why

- `Deduper` no longer puts every document through one hash set. Top-level documents (`parentId` null), including loose files and emails, still collapse by `sha256` in input order. That keeps the existing loose-file tests and still drops a second email only when the raw hash actually matches.
- Attachments follow the parent chain. Shared hashes across kept families stay. Attachments of a suppressed top-level document are suppressed with it, so the load file does not gain orphaned children.
- A parent cycle throws instead of hanging. The spec says the parent is always present and says nothing about cycles.
- Command line, summary sentence, header, and column order are unchanged. `SentDate` and `FamilyID` are still copied from the immediate parent, as before.

## What I didn't get to

- Cross-mailbox email identity (section 3.3). DOC-000001 and DOC-000009 both remain. The spec does not define the match key, and there is no message id on the document.
- Section 4 for loose files. I did not change the loose-file tests; global first-seen hash dedup is still what they assert.
- Order-independent survivor selection (section 6.1). Which top-level document wins a hash still depends on input order.
- UTC `SentDate` (`2024-03-05T14:22:00-05:00` is still copied through; the spec example is `2024-03-05T19:22:00Z`).
- `FamilyID` / attachment date for a nested chain still use the immediate parent, not the top-level email. Inclusion does walk to the root. The sample has no grandchild, so its family ids were already the top-level ids.
- CSV quoting. The sample's output fields have no comma, quote, or newline.
- Input the spec says will not happen: missing parent, duplicate `docId`, empty `sha256`.

## What I'd do next

- Write down the email match key and whether dedup is per custodian or per batch before coding section 3.3. A key that strips `FW:` / `Re:` or compares raw bodies would collapse DOC-000004 or DOC-000012 into the Q3 email; those are different messages in the sample.
- Decide section 4 for loose files separately. The current CSV cannot list both custodians on DOC-000008.
- Then make the top-level winner independent of input order, format `SentDate` as UTC, and set nested `FamilyID` from the root. Add a permutation test. Do not treat `data/current-tool-output.csv` as the oracle; it is the attachment-loss baseline (16 in, 12 out, 4 suppressed).

## If you reimplemented in another language

Stayed on .NET 8. The command is still `DedupTool <batch.json> <loadfile.csv>`, the summary sentence is unchanged, and the header is still `DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash`.

## Working with the AI

The review correctly separated the hash-set bug from unimplemented section 3.3, and the reversal run showed the survivor set really does depend on order. I rejected treating a normalized from/subject/body key as a requirement: the spec never defines it. The implementation follows the written choice (keep attachment rows on every kept email, do not dedupe mailbox copies yet) rather than a literal reading of section 3.2.
