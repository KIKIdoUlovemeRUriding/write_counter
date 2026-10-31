using System.IO;
using System.Text.Json;
using InkFlow.Models;

namespace InkFlow.Services;

/// <summary>Stores per-day statistics in a small JSON history file.</summary>
public sealed class HistoryService
{
    private readonly List<DailyRecord> _records = new();
    private DateTime _todayDate = DateTime.Today;

    public HistoryService()
    {
        Load();
        EnsureToday(DateTime.Today);
    }

    public IReadOnlyList<DailyRecord> Records => _records;

    public DailyRecord Today { get; private set; } = new();

    public string? LastError { get; private set; }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.HistoryFile))
            {
                return;
            }

            var json = File.ReadAllText(AppPaths.HistoryFile);
            var loaded = JsonSerializer.Deserialize<List<DailyRecord>>(json, JsonDefaults.Options);
            if (loaded is null)
            {
                return;
            }

            _records.Clear();
            foreach (var record in loaded.Where(r => !string.IsNullOrWhiteSpace(r.Date)))
            {
                if (_records.Any(existing => existing.Date == record.Date))
                {
                    continue;
                }

                _records.Add(record);
            }

            _records.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));

            // Keep two years of history at most.
            var cutoff = DateTime.Today.AddDays(-730).ToString("yyyy-MM-dd");
            _records.RemoveAll(r => string.CompareOrdinal(r.Date, cutoff) < 0);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public DailyRecord EnsureToday(DateTime date)
    {
        _todayDate = date.Date;
        var key = _todayDate.ToString("yyyy-MM-dd");
        var existing = _records.FirstOrDefault(r => r.Date == key);
        if (existing is null)
        {
            existing = new DailyRecord { Date = key };
            _records.Add(existing);
            _records.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
        }

        Today = existing;
        return Today;
    }

    /// <summary>True when the stored "today" no longer matches the given date.</summary>
    public bool IsStale(DateTime date) => _todayDate != date.Date;

    public void UpdateToday(long words, long writingSeconds, long restSeconds, int breaks, double bestSpeed)
    {
        Today.Words = Math.Max(0, words);
        Today.WritingSeconds = Math.Max(0, writingSeconds);
        Today.RestSeconds = Math.Max(0, restSeconds);
        Today.Breaks = Math.Max(0, breaks);
        Today.BestSpeed = Math.Max(0, bestSpeed);
    }

    public void ResetToday()
    {
        Today.Words = 0;
        Today.WritingSeconds = 0;
        Today.RestSeconds = 0;
        Today.Breaks = 0;
        Today.BestSpeed = 0;
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            var json = JsonSerializer.Serialize(_records, JsonDefaults.Options);
            FileUtil.WriteAllTextAtomic(AppPaths.HistoryFile, json);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>Records for the last <paramref name="days"/> days, oldest first. Missing days are zero-filled.</summary>
    public List<DailyRecord> RecentDays(int days, DateTime? today = null)
    {
        var end = (today ?? DateTime.Today).Date;
        var result = new List<DailyRecord>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var date = end.AddDays(-i);
            var key = date.ToString("yyyy-MM-dd");
            var record = _records.FirstOrDefault(r => r.Date == key);
            result.Add(record?.Clone() ?? new DailyRecord { Date = key });
        }

        return result;
    }

    public long WordsInMonth(DateTime month, long liveTodayWords)
    {
        var prefix = month.ToString("yyyy-MM");
        long total = 0;
        foreach (var record in _records)
        {
            if (record.Date.StartsWith(prefix, StringComparison.Ordinal))
            {
                total += record.Date == DateTime.Today.ToString("yyyy-MM-dd") ? liveTodayWords : record.Words;
            }
        }

        return total;
    }

    /// <summary>Consecutive days (ending today) that have any words written.</summary>
    public int Streak(long liveTodayWords)
    {
        var byDate = _records.ToDictionary(r => r.Date, r => r.Words);
        byDate[DateTime.Today.ToString("yyyy-MM-dd")] = liveTodayWords;

        var streak = 0;
        var cursor = DateTime.Today;
        // Allow the streak to survive a not-yet-started morning: start from yesterday if today is empty.
        if (!byDate.TryGetValue(cursor.ToString("yyyy-MM-dd"), out var todayWords) || todayWords <= 0)
        {
            cursor = cursor.AddDays(-1);
        }

        while (true)
        {
            var key = cursor.ToString("yyyy-MM-dd");
            if (byDate.TryGetValue(key, out var words) && words > 0)
            {
                streak++;
                cursor = cursor.AddDays(-1);
            }
            else
            {
                break;
            }
        }

        return streak;
    }

    public long TotalWords(long liveTodayWords)
    {
        var todayKey = DateTime.Today.ToString("yyyy-MM-dd");
        long total = 0;
        foreach (var record in _records)
        {
            total += record.Date == todayKey ? liveTodayWords : record.Words;
        }

        return total;
    }
}
