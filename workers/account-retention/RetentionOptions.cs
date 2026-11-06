namespace FandomScore.Worker.AccountRetention;

/// <summary>
/// Retention periods that are still owner decisions (<c>Retention</c> section; PROJECT_TODO "Decide retention for ...",
/// privacy README "Retention"). Each is unset by default, which keeps the record for good, as today. Setting one
/// (e.g. <c>Retention__ReadNotifications=180.00:00:00</c>) turns that pruning on in the daily <c>privacy.retention</c> job
/// (<c>prune_optional_records()</c>, migration 0060). The periods that are decided or set provisionally live with
/// their feature (sessions, push records, client signals: migrations 0053, 0054, 0057; the play ledger:
/// <c>Spotify__PlayHistoryRetention</c>).
/// </summary>
public sealed class RetentionOptions
{
    public const string Section = "Retention";

    /// <summary>Delete notifications this long after they were read (proposal: 180 days).</summary>
    public TimeSpan? ReadNotifications { get; set; }

    /// <summary>Delete notifications this long after they were written, read or not (proposal: 1 year).</summary>
    public TimeSpan? Notifications { get; set; }

    /// <summary>Forget which fan made an outbound ticket or merch click this long after it (proposal: 1 year).</summary>
    public TimeSpan? ClickLinks { get; set; }

    /// <summary>Clear a submission's OCR text this long after it was verified or rejected (proposal: 30 days).</summary>
    public TimeSpan? OcrText { get; set; }

    public bool Any => ReadNotifications is not null || Notifications is not null || ClickLinks is not null || OcrText is not null;
}
