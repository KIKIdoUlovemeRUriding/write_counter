namespace InkFlow.Models;

/// <summary>One calendar day of writing stats. Persisted in history.json.</summary>
public sealed class DailyRecord
{
    /// <summary>Local date, formatted yyyy-MM-dd.</summary>
    public string Date { get; set; } = string.Empty;

    public long Words { get; set; }

    public long WritingSeconds { get; set; }

    public long RestSeconds { get; set; }

    public int Breaks { get; set; }

    public double BestSpeed { get; set; }

    public DailyRecord Clone() => new()
    {
        Date = Date,
        Words = Words,
        WritingSeconds = WritingSeconds,
        RestSeconds = RestSeconds,
        Breaks = Breaks,
        BestSpeed = BestSpeed,
    };
}
