namespace FandomScore.Worker.ImageRedaction;

/// <summary>
/// Configuration section <c>Redaction</c>: printed order numbers on tickets, screenshots, and receipts are found with
/// OCR only when a Tesseract command is configured (<c>Redaction__TesseractCommand=tesseract</c>, with the binary and
/// its language data in the image or on the host). Unset, the worker does what it did before: barcodes and QR codes
/// only, and staff draw boxes over printed numbers. Which OCR to run for real (Tesseract in the image, or a cloud
/// vision API) is the open decision in <c>services/api/src/integrations/ocr/TODO.md</c>; this is the plumbing for the
/// Tesseract answer.
/// </summary>
public sealed class RedactionOptions
{
    public const string Section = "Redaction";

    /// <summary>The Tesseract executable (a name on PATH or a full path). Empty turns OCR off.</summary>
    public string TesseractCommand { get; set; } = "";

    /// <summary>Tesseract language data to use.</summary>
    public string TesseractLanguage { get; set; } = "eng";

    /// <summary>How long one image may take; a slower run is treated as "no text found".</summary>
    public TimeSpan TesseractTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public bool OcrEnabled => !string.IsNullOrWhiteSpace(TesseractCommand);
}
