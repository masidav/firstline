# OCR and vision integration

There is no separate OCR service. Printed order numbers on proof photos are found by the redaction worker
(`workers/image-redaction`, `TextFinder.cs` and `OrderNumberFinder.cs`, migration 0061) with the Tesseract command
line when `Redaction__TesseractCommand` is set; the README there has the rules, what is stored (counts and one hash,
never the text), and how the duplicate-order case reaches staff. The provider decision (Tesseract in the worker image
or a cloud vision API) is in `TODO.md`.
