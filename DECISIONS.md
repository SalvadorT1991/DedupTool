# Decisions

Keep this short and honest. Bullet points are fine. We read this before we read your code.

## Where the spec was unclear, contradictory, or silent

- **Question:** Sections 3.2 and 3.6 say attachments with the same `sha256` are duplicates and omitted. Section 3.5 says a kept email's family arrives intact.
- **Decision:** Family integrity wins. Attachment hashes do not suppress attachment rows. A kept root keeps every descendant. The same PDF may appear on more than one kept email (DOC-000003 and DOC-000005).
- **Question:** Section 3.3 says mailbox copies are duplicates even when `sha256` differs. It does not name the fields. There is no message id. Section 4 says each custodian's set must stand alone. The CSV has one `Custodian` column and the column list cannot grow. Section 3.4 and 3.6 say dedup is across the batch and duplicates are omitted. Section 1 says reviewers do not see the same content twice.
- **Decision:** One CSV cannot satisfy both. Section 3.3, 3.4, 3.6, and section 1 require the duplicate content to be omitted. Section 4 requires mlee's own copy to remain if mlee is later produced alone. The frozen header has one `Custodian` column, so the kept row cannot also name mlee. Behavior is global omission of the duplicate family: smallest `DocID`, attachments of that copy kept, the other custodian's copy omitted. Sample result: DOC-000001 with DOC-000002 and DOC-000003; DOC-000009, DOC-000010, and DOC-000011 omitted. DOC-000013 still loses to DOC-000008. This does not comply with section 4. Clarification needed from the assessment owner before any other behavior: if a custodian is severed, must that custodian's duplicate family be emitted anyway?
- **Email key, because the spec is silent:** present `from`, `sentDate`, `subject`, `body`, and `to` only. `from`, `subject`, and each `to` entry are trimmed and lowercased with the invariant culture. The key stores `ToUniversalTime().Ticks` before the CSV second is truncated. `19:22:00.100Z` does not match `19:22:00.200Z`. `19:22:00.500Z` matches `14:22:00.500-05:00`. The timestamp must have an explicit `Z` or numeric offset. `body` converts CR and CRLF to LF, then trims the ends. Internal spaces and blank lines stay. Recipient order stays; the spec does not say order is irrelevant. `Re:` and `FW:` are not stripped. File name, custodian, and raw `sha256` are not the identity. A null `from`, `subject`, `body`, `to`, or `sentDate`, a blank `from`, or an unparseable `sentDate` builds no key, so those emails are not merged with each other unless their raw hashes match. A kept email with a bad `sentDate` still throws on output.
- **Limitations of that key:** a display name will not match a bare address. A reordered `to` list will not match. A gateway footer or a body-case change will not match. Two messages that really are the same and differ only in those ways stay duplicated. Different sub-second instants stay distinct even though the CSV second is the same.
- **Question:** Section 6.1 requires the same load file regardless of input order. Section 5 says row order is not significant. The spec does not name the winner.
- **Decision:** Every equivalence class of top-level documents keeps the ordinal-minimum `DocID`. Classes are equal `sha256` (loose files and emails) union equal email keys. Rows, including attachments, are sorted by `DocID` with `StringComparer.Ordinal`.
- **Question:** The output `SentDate` may not contain a fraction, and the input is ISO 8601, which may. Rounding was previously unresolved.
- **Decision:** Accept a whole-second or fractional timestamp with an explicit zone. Emit UTC truncated toward zero to a whole second, so `19:22:00.900Z` stays `19:22:00Z`. A missing zone still throws and is not read as local time.
- **Question:** Section 5 says the file is a CSV and fixes the header. It does not mention quotes. The sample fields have no comma, quote, or line break.
- **Decision:** Quote a field only when it contains `,`, `"`, CR, or LF, and double internal quotes. Plain fields, including `Q3 budget review.eml` and empty columns, stay unquoted, so this sample's column text does not gain quote marks. This is an assumption that the ingest job accepts quoted fields. The spec does not name RFC 4180. Header text and column order are unchanged.

## What I changed and why

- Cross-mailbox emails with a complete key collapse even when `sha256` differs. The kept email keeps its attachments. The other copy's family is omitted.
- Loose-file dedup is still global `sha256` plus ordinal `DocID`. DOC-000013 is still omitted.
- Attachment hashes still do not strip a kept family. DOC-000004 still has `vendor-report.pdf`.
- Parent cycles throw. Each document's root is memoized, so a batch is one pass over documents plus parent links, not a pairwise comparison.
- Fractional input is truncated only when formatting `SentDate`. Identity uses the full UTC tick count.
- The CSV writer quotes only fields that need it.

## What I didn't get to

- Section 4 for the losing custodian. Waiting on the clarification above. The schema cannot record both custodians on one row.
- Display-name parsing, recipient reordering, and body changes beyond line endings and surrounding trim.

## What I'd do next

- Ask whether a severed custodian must receive the duplicate family anyway. That choice contradicts 3.3 inside this CSV.
- Ask the ingest job whether quoted fields are accepted before relying on them for unseen batches.
- Do not treat `data/current-tool-output.csv` as the oracle. It is the pre-fix baseline (16 in, 12 out, 4 suppressed) from before family integrity.

## If you reimplemented in another language

Stayed on .NET 8. The command is still `DedupTool <batch.json> <loadfile.csv>`. The summary sentence is unchanged. The header is still `DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash`.

## Working with the AI

Earlier passes kept mailbox copies apart until the key and the section 4 trade-off were written down. The key does not strip `Re:` or `FW:`. Recipient order is not sorted, because the spec never says order is irrelevant.
