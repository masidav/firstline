using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace FandomScore.Worker.ImageRedaction;

/// <summary>A word OCR read, with its box in the image's pixels and the reader's confidence (0..100).</summary>
/// <param name="Line">Which line of text it sits on (same value for words on one line).</param>
public sealed record OcrWord(string Text, SKRect Box, float Confidence, int Line);

/// <summary>Finds printed words in an image. Null means OCR isn't available or didn't run (not "no words").</summary>
public interface ITextFinder
{
    bool Enabled { get; }

    Task<IReadOnlyList<OcrWord>?> FindWordsAsync(byte[] content, CancellationToken cancellationToken);
}

/// <summary>OCR off: the worker redacts codes only, as before.</summary>
public sealed class NoTextFinder : ITextFinder
{
    public static readonly NoTextFinder Instance = new();

    public bool Enabled => false;

    public Task<IReadOnlyList<OcrWord>?> FindWordsAsync(byte[] content, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OcrWord>?>(null);
}

/// <summary>
/// Runs the Tesseract command line on the image (<c>tesseract &lt;file&gt; stdout --psm 11 tsv</c>: sparse text, one
/// row per word with its box) and maps the boxes back to the image's pixels. The image goes to Tesseract as a PNG of
/// at most <see cref="MaxSide"/> px on the long side, through a temporary file that is deleted afterwards; nothing of
/// the text is logged. A failure or timeout counts as "didn't run" (null), never as "nothing to redact".
/// </summary>
public sealed partial class TesseractCliTextFinder(IOptions<RedactionOptions> options, ILogger<TesseractCliTextFinder> logger) : ITextFinder
{
    public const int MaxSide = 2000;

    private readonly RedactionOptions _options = options.Value;

    public bool Enabled => _options.OcrEnabled;

    public async Task<IReadOnlyList<OcrWord>?> FindWordsAsync(byte[] content, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return null;
        }

        using var bitmap = CodeRedactor.TryDecode(content);
        if (bitmap is null)
        {
            return null;
        }

        var scale = Math.Min(1f, MaxSide / (float)Math.Max(bitmap.Width, bitmap.Height));
        using var resized = scale < 1
            ? bitmap.Resize(new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)), new SKSamplingOptions(SKFilterMode.Linear))
            : null;
        using var image = SKImage.FromBitmap(resized ?? bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);

        var file = Path.Combine(Path.GetTempPath(), $"ocr-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(file, png.ToArray(), cancellationToken);
            var output = await RunAsync(file, cancellationToken);
            return output is null ? null : ParseTsv(output, 1 / scale);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<string?> RunAsync(string file, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_options.TesseractCommand)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(file);
        start.ArgumentList.Add("stdout");
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add(_options.TesseractLanguage);
        start.ArgumentList.Add("--psm");
        start.ArgumentList.Add("11");
        start.ArgumentList.Add("tsv");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TesseractTimeout);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Tesseract did not start.");
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                LogFailed(logger, process.ExitCode);
                return null;
            }

            await errors;
            return await output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogTimedOut(logger, _options.TesseractTimeout);
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            LogNotRun(logger, e);
            return null;
        }
    }

    /// <summary>
    /// Tesseract's TSV: a header, then one row per element; words are level 5 with
    /// <c>page_num block_num par_num line_num</c> naming their line. Boxes are scaled by <paramref name="factor"/>.
    /// </summary>
    public static IReadOnlyList<OcrWord> ParseTsv(string tsv, float factor = 1)
    {
        var words = new List<OcrWord>();
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in tsv.Split('\n').Skip(1))
        {
            var cells = row.TrimEnd('\r').Split('\t');
            if (cells.Length < 12 || cells[0] != "5")
            {
                continue;
            }

            var text = cells[11].Trim();
            if (text.Length == 0
                || !int.TryParse(cells[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left)
                || !int.TryParse(cells[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var top)
                || !int.TryParse(cells[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(cells[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            {
                continue;
            }

            var lineKey = $"{cells[1]}/{cells[2]}/{cells[3]}/{cells[4]}";
            if (!lines.TryGetValue(lineKey, out var line))
            {
                line = lines.Count;
                lines[lineKey] = line;
            }

            float.TryParse(cells[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence);
            words.Add(new OcrWord(text, new SKRect(left * factor, top * factor, (left + width) * factor, (top + height) * factor), confidence, line));
        }

        return words;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tesseract exited with code {ExitCode}; no printed text was redacted for this file.")]
    private static partial void LogFailed(ILogger logger, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tesseract took longer than {Timeout}; no printed text was redacted for this file.")]
    private static partial void LogTimedOut(ILogger logger, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tesseract could not be run (Redaction:TesseractCommand); no printed text was redacted for this file.")]
    private static partial void LogNotRun(ILogger logger, Exception exception);
}
