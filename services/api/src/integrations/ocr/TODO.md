# TODO — OCR and vision integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Choose on-device OCR and/or server-side OCR providers after reviewing platform support and cost. *Decided
      2026-11-06: Tesseract, server-side, in the worker image (`Dockerfile` target `worker`: `tesseract-ocr`,
      `tesseract-ocr-eng`, `Redaction__TesseractCommand=tesseract`). Nothing leaves the worker. A cloud vision API
      would be a second `ITextFinder` (`workers/image-redaction/TextFinder.cs`) if Tesseract proves too weak on real
      photos; the finder rules and storage wouldn't change.*
- [x] Define the normalized extraction schema. *Counts only, by decision: `submissions.extracted` holds `ocr`, `words`,
      `order_numbers_redacted`, `checked_at`; the numbers themselves are never stored, only the first one's SHA-256 in
      `order_ref_hash` (normalized to capitals and digits).*
- [x] Treat OCR output as an untrusted signal, not proof that a document is genuine. *It only redacts and hashes; status
      and points are staff decisions. A match with another verified submission sends the file to staff, it never
      rejects.*
- [x] Queue server-side backstop checks only when needed and permitted. *Runs inside `submissions.redact` on verified
      tickets, screenshots, and receipts only, after staff verified them.*
- [x] Minimize transfer and retention of original sensitive images. *The image reaches Tesseract as a temporary file
      on the worker, deleted after the call; nothing leaves the worker.*
- [x] Record provider/check versions and errors for auditability. *An `ocr_extract` check per file (counts), an
      `order_ref` check on a duplicate; Tesseract failures and timeouts are logged and count as "didn't run".*
- [x] Add tests with synthetic/redacted fixtures, low-quality images, and missing fields. *RedactionJobTests: the finder
      rules (labels, long digit runs, dates/times/prices/seats excluded), TSV parsing, a fake Tesseract end to end,
      the duplicate case, OCR off and failing. Low-quality real tickets: not yet (David's photos, PROJECT_TODO).*

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. *See PROJECT_TODO Phase 7, 2026-11-06.*
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations. *`workers/image-redaction/README.md`.*
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
