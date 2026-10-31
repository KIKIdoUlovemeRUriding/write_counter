using System.Windows.Threading;
using InkFlow.Core;
using InkFlow.Models;

namespace InkFlow.Services;

/// <summary>
/// The heart of InkFlow: counts writing time, rest time, words and speed,
/// and raises reminders. All counters are kept in memory and checkpointed
/// into <see cref="HistoryService"/> as the session progresses.
/// </summary>
public sealed class StatsEngine : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly DispatcherTimer _timer;

    private readonly Queue<int> _speedDeltas = new();
    private readonly Queue<bool> _activeTicks = new();
    private readonly List<double> _speedHistory = new();

    private DateTime _currentDate;
    private DateTime _lastTick;
    private DateTime _lastActivity;

    private bool _started;
    private bool _paused;
    private bool _autoPaused;
    private bool _breakMode;

    private long _sessionWords;
    private long _sessionWritingSeconds;
    private long _sessionRestSeconds;
    private int _sessionBreaks;

    private long _baseWords;
    private long _baseWritingSeconds;
    private long _baseRestSeconds;
    private int _baseBreaks;
    private double _baseBestSpeed;

    private long _lastSpeedWords;
    private long _lastDocumentCount;
    private double _speed;
    private double _maxSpeedSession;

    private int _continuousWritingSeconds;
    private int _restRunSeconds;
    private bool _breakCountedForRun;
    private bool _breakDueNotified;
    private int _lastReminderFocus;
    private bool _goalNotified;
    private int _tickCounter;
    private bool _previewHold;

    public StatsEngine(SettingsService settings, HistoryService history)
    {
        _settings = settings;
        _history = history;
        _currentDate = DateTime.Today;
        _history.EnsureToday(_currentDate);
        LoadBaseFromHistory();

        _lastTick = DateTime.Now;
        _lastActivity = DateTime.Now;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += OnTick;
        _timer.Start();

        _settings.SettingChanged += OnSettingChanged;
    }

    public event EventHandler? BreakReminderDue;

    public event EventHandler? GoalReached;

    public event EventHandler? DayChanged;

    /// <summary>Raised once per second after all counters have been updated.</summary>
    public event EventHandler? Tick;

    // ---------------------------------------------------------------- state

    public bool IsStarted => _started;

    public bool IsPaused => _paused;

    public bool IsAutoPaused => _autoPaused;

    public bool IsBreakMode => _breakMode;

    public bool IsCurrentlyActive
        => _started && !_paused && !_breakMode &&
           (DateTime.Now - _lastActivity).TotalSeconds <= _settings.Settings.IdleThresholdSeconds;

    public bool IsWriting => IsCurrentlyActive;

    public bool IsResting => _started && !_paused && !IsCurrentlyActive;

    public string StatusKind
    {
        get
        {
            if (!_started)
            {
                return "idle";
            }

            if (_paused)
            {
                return "paused";
            }

            if (_breakMode)
            {
                return "resting";
            }

            return IsCurrentlyActive ? "writing" : "resting";
        }
    }

    public string StatusText
    {
        get
        {
            if (!_started)
            {
                return "等待开始";
            }

            if (_paused)
            {
                return _autoPaused ? "已自动暂停" : "已暂停";
            }

            if (_breakMode)
            {
                return "休息中";
            }

            return IsCurrentlyActive ? "码字中" : "休息中";
        }
    }

    public bool ShowStartHint => !_started;

    // ---------------------------------------------------------------- counters

    public long SessionWords => _sessionWords;

    public long WordsToday => _baseWords + _sessionWords;

    public long SessionWritingSeconds => _sessionWritingSeconds;

    public long WritingSecondsToday => _baseWritingSeconds + _sessionWritingSeconds;

    public long SessionRestSeconds => _sessionRestSeconds;

    public long RestSecondsToday => _baseRestSeconds + _sessionRestSeconds;

    public long SessionElapsedSeconds => _sessionWritingSeconds + _sessionRestSeconds;

    public int BreaksSession => _sessionBreaks;

    public int BreaksToday => _baseBreaks + _sessionBreaks;

    public int ContinuousWritingSeconds => _continuousWritingSeconds;

    public int RestRunSeconds => _restRunSeconds;

    public double Speed => _speed;

    public double SessionMaxSpeed => _maxSpeedSession;

    public double TodayBestSpeed => Math.Max(_history.Today.BestSpeed, _maxSpeedSession);

    public double AverageSpeedSession
        => _sessionWritingSeconds >= 10 ? _sessionWords * 60.0 / _sessionWritingSeconds : 0;

    public double AverageSpeedToday
        => WritingSecondsToday >= 10 ? WordsToday * 60.0 / WritingSecondsToday : 0;

    public int DailyGoal => _settings.Settings.DailyGoal;

    public double GoalFraction
    {
        get
        {
            var goal = DailyGoal;
            return goal <= 0 ? 0 : Math.Clamp(WordsToday / (double)goal, 0, 1);
        }
    }

    public long GoalRemaining => Math.Max(0, DailyGoal - WordsToday);

    public int GoalPercent => (int)Math.Round(GoalFraction * 100);

    public double[] SpeedHistory => _speedHistory.ToArray();

    public int BreakReminderMinutes => _settings.Settings.BreakReminderMinutes;

    public int BreakReminderRemainingSeconds
    {
        get
        {
            var interval = _settings.Settings.BreakReminderMinutes;
            if (interval <= 0)
            {
                return -1;
            }

            return Math.Max(0, interval * 60 - _continuousWritingSeconds);
        }
    }

    public string BreakHintText
    {
        get
        {
            var interval = _settings.Settings.BreakReminderMinutes;
            if (interval <= 0)
            {
                return "休息提醒已关闭";
            }

            if (!_started)
            {
                return $"每 {interval} 分钟提醒休息";
            }

            var remaining = BreakReminderRemainingSeconds;
            return remaining <= 0
                ? "该起来活动一下了"
                : $"距下次休息 {remaining / 60:00}:{remaining % 60:00}";
        }
    }

    // ---------------------------------------------------------------- public API

    /// <summary>Called for any keyboard input (editor or app-level) to prove the writer is active.</summary>
    public void NoteActivity()
    {
        var now = DateTime.Now;
        if (!_started)
        {
            _started = true;
            _lastTick = now;
        }

        if (_autoPaused)
        {
            _paused = false;
            _autoPaused = false;
        }

        if (!_paused)
        {
            _breakMode = false;
        }

        _lastActivity = now;
        NotifyStatus();
    }

    /// <summary>Keeps the session alive when typing happens in another application.</summary>
    public void NoteExternalActivity(int charDelta)
    {
        NoteActivity();

        if (charDelta != 0 && _settings.Settings.ExternalKeyEstimate && !_paused)
        {
            ApplyWordDelta(charDelta);
        }
    }

    /// <summary>
    /// Applies a character delta measured in Microsoft Word. Word reports its
    /// document total, so the caller sends only the difference between polls.
    /// </summary>
    public void ApplyExternalWordCount(long delta)
    {
        if (delta == 0)
        {
            return;
        }

        NoteActivity();
        ApplyWordDelta(delta);
    }

    /// <summary>Resets the word baseline without counting the loaded document as new writing.</summary>
    public void ResetDocumentBaseline(long documentCount)
    {
        _lastDocumentCount = Math.Max(0, documentCount);
        _lastSpeedWords = _sessionWords;
    }

    /// <summary>Called whenever the editor document changes; only growth is counted.</summary>
    public void UpdateDocumentCount(long documentCount)
    {
        if (documentCount < 0)
        {
            documentCount = 0;
        }

        var delta = documentCount - _lastDocumentCount;
        _lastDocumentCount = documentCount;
        if (delta == 0)
        {
            return;
        }

        NoteActivity();
        ApplyWordDelta(delta);
    }

    public void TogglePause()
    {
        if (!_started)
        {
            StartSession();
            return;
        }

        if (_paused)
        {
            Resume();
        }
        else
        {
            Pause();
        }
    }

    /// <summary>Starts (or resumes) the clock. Used by the "开始" button.</summary>
    public void StartSession()
    {
        _started = true;
        _paused = false;
        _autoPaused = false;
        _breakMode = false;
        _lastActivity = DateTime.Now;
        _lastTick = DateTime.Now;
        _lastSpeedWords = _sessionWords;
        Persist();
        NotifyAll();
    }

    public void Pause()
    {
        _paused = true;
        _autoPaused = false;
        _breakMode = false;
        Persist();
        NotifyAll();
    }

    public void Resume()
    {
        _started = true;
        _paused = false;
        _autoPaused = false;
        _breakMode = false;
        _lastActivity = DateTime.Now;
        _lastTick = DateTime.Now;
        NotifyAll();
    }

    /// <summary>Consciously steps away; writing stops counting until the next keystroke.</summary>
    public void BeginBreak()
    {
        if (!_started)
        {
            StartSession();
        }

        _paused = false;
        _autoPaused = false;
        _breakMode = true;
        _lastActivity = DateTime.Now.AddSeconds(-(_settings.Settings.IdleThresholdSeconds + 1));
        NotifyAll();
    }

    /// <summary>Commits the current session and starts a fresh one without touching today's totals.</summary>
    public void StartNewSession()
    {
        Persist();
        _baseWords = _history.Today.Words;
        _baseWritingSeconds = _history.Today.WritingSeconds;
        _baseRestSeconds = _history.Today.RestSeconds;
        _baseBreaks = _history.Today.Breaks;
        _baseBestSpeed = _history.Today.BestSpeed;

        _sessionWords = 0;
        _sessionWritingSeconds = 0;
        _sessionRestSeconds = 0;
        _sessionBreaks = 0;
        _lastSpeedWords = 0;
        _speed = 0;
        _maxSpeedSession = 0;
        _continuousWritingSeconds = 0;
        _restRunSeconds = 0;
        _breakCountedForRun = false;
        _breakDueNotified = false;
        _lastReminderFocus = 0;
        _goalNotified = WordsToday >= DailyGoal && DailyGoal > 0;
        _speedDeltas.Clear();
        _activeTicks.Clear();
        _speedHistory.Clear();

        _started = true;
        _paused = false;
        _autoPaused = false;
        _breakMode = false;
        _lastActivity = DateTime.Now;
        _lastTick = DateTime.Now;

        Persist();
        NotifyAll();
    }

    /// <summary>Clears today's record (dangerous; exposed in settings).</summary>
    public void ResetToday()
    {
        _history.ResetToday();
        _history.Save();

        _baseWords = 0;
        _baseWritingSeconds = 0;
        _baseRestSeconds = 0;
        _baseBreaks = 0;
        _baseBestSpeed = 0;
        _sessionWords = 0;
        _sessionWritingSeconds = 0;
        _sessionRestSeconds = 0;
        _sessionBreaks = 0;
        _maxSpeedSession = 0;
        _speed = 0;
        _lastSpeedWords = 0;
        _continuousWritingSeconds = 0;
        _restRunSeconds = 0;
        _breakCountedForRun = false;
        _breakDueNotified = false;
        _goalNotified = false;
        _started = false;
        _paused = false;
        _autoPaused = false;
        _breakMode = false;
        _speedDeltas.Clear();
        _activeTicks.Clear();
        _speedHistory.Clear();
        NotifyAll();
    }

    public void Persist()
    {
        if (!_started && _sessionWords == 0 && _sessionWritingSeconds == 0 && _sessionRestSeconds == 0)
        {
            return;
        }

        _history.UpdateToday(
            WordsToday,
            WritingSecondsToday,
            RestSecondsToday,
            BreaksToday,
            Math.Max(_history.Today.BestSpeed, _maxSpeedSession));
        _history.Save();
    }

    /// <summary>Dev-only: fills the session with sample data for screenshot previews.</summary>
    internal void SeedSessionForPreview(long words, long writingSeconds, long restSeconds, int breaks, double speed)
    {
        _started = true;
        _paused = false;
        _previewHold = true;
        _sessionWords = words;
        _sessionWritingSeconds = writingSeconds;
        _sessionRestSeconds = restSeconds;
        _sessionBreaks = breaks;
        _continuousWritingSeconds = 16 * 60;
        _restRunSeconds = 0;
        _speed = speed;
        _maxSpeedSession = Math.Max(_maxSpeedSession, speed * 1.18);
        _lastSpeedWords = words;
        _goalNotified = WordsToday >= DailyGoal;
        _speedHistory.Clear();
        var random = new Random(20);
        for (var i = 0; i < 60; i++)
        {
            var baseValue = speed + Math.Sin(i / 5.0) * 11 + random.NextDouble() * 7;
            _speedHistory.Add(Math.Max(0, baseValue));
        }

        NotifyAll();
    }

    // ---------------------------------------------------------------- internals

    private void LoadBaseFromHistory()
    {
        _baseWords = _history.Today.Words;
        _baseWritingSeconds = _history.Today.WritingSeconds;
        _baseRestSeconds = _history.Today.RestSeconds;
        _baseBreaks = _history.Today.Breaks;
        _baseBestSpeed = _history.Today.BestSpeed;
    }

    private void ApplyWordDelta(long delta)
    {
        if (delta == 0)
        {
            return;
        }

        var before = _sessionWords;
        _sessionWords = Math.Max(0, _sessionWords + delta);
        if (_sessionWords == before)
        {
            return;
        }

        if (_sessionWords >= DailyGoal && DailyGoal > 0 && !_goalNotified)
        {
            _goalNotified = true;
            GoalReached?.Invoke(this, EventArgs.Empty);
        }

        NotifyAll();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        if (now.Date != _currentDate)
        {
            RollOverDay();
        }

        var elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        if (_previewHold)
        {
            NotifyAll();
            Tick?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (elapsed <= 0 || elapsed > 5)
        {
            // The machine slept or the UI thread stalled: do not invent minutes of work.
            UpdateSpeedState(now, false);
            NotifyAll();
            return;
        }

        var active = _started && !_paused && !_breakMode &&
                     (now - _lastActivity).TotalSeconds <= _settings.Settings.IdleThresholdSeconds;

        if (_started && !_paused)
        {
            if (active)
            {
                _sessionWritingSeconds++;
                _continuousWritingSeconds++;
                _restRunSeconds = 0;
                _breakCountedForRun = false;
            }
            else
            {
                _sessionRestSeconds++;
                _restRunSeconds++;

                if (_restRunSeconds >= 60 && !_breakCountedForRun)
                {
                    _sessionBreaks++;
                    _breakCountedForRun = true;
                    _continuousWritingSeconds = 0;
                    _breakDueNotified = false;
                    _lastReminderFocus = 0;
                    Persist();
                }
            }
        }

        var autoPauseMinutes = _settings.Settings.AutoPauseMinutes;
        if (_started && !_paused && autoPauseMinutes > 0 &&
            (now - _lastActivity).TotalMinutes >= autoPauseMinutes)
        {
            _paused = true;
            _autoPaused = true;
            _breakMode = false;
            Persist();
        }

        if (_started && !_paused && !_breakMode && _settings.Settings.BreakReminderMinutes > 0)
        {
            var due = _settings.Settings.BreakReminderMinutes * 60;
            if (!_breakDueNotified && _continuousWritingSeconds >= due)
            {
                _breakDueNotified = true;
                _lastReminderFocus = _continuousWritingSeconds;
                BreakReminderDue?.Invoke(this, EventArgs.Empty);
            }
            else if (_breakDueNotified && _continuousWritingSeconds - _lastReminderFocus >= 300)
            {
                _lastReminderFocus = _continuousWritingSeconds;
                BreakReminderDue?.Invoke(this, EventArgs.Empty);
            }
        }

        if (_started && !_goalNotified && DailyGoal > 0 && WordsToday >= DailyGoal)
        {
            _goalNotified = true;
            GoalReached?.Invoke(this, EventArgs.Empty);
        }

        UpdateSpeedState(now, active);

        _tickCounter++;
        if (_started && !_paused && _tickCounter % 15 == 0)
        {
            Persist();
        }

        NotifyAll();
        Tick?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSpeedState(DateTime now, bool active)
    {
        var gain = Math.Max(0, _sessionWords - _lastSpeedWords);
        _lastSpeedWords = _sessionWords;

        // A paste should not produce an absurd "words per minute" spike.
        gain = Math.Min(gain, 40);

        _speedDeltas.Enqueue((int)gain);
        _activeTicks.Enqueue(active);
        while (_speedDeltas.Count > 60)
        {
            _speedDeltas.Dequeue();
            _activeTicks.Dequeue();
        }

        if (!active)
        {
            // Resting / paused: keep the last writing speed visible instead of
            // letting the rolling window decay to zero. The window itself keeps
            // advancing, so resuming starts from fresh activity.
            _speedHistory.Add(_speed);
            while (_speedHistory.Count > 60)
            {
                _speedHistory.RemoveAt(0);
            }

            return;
        }

        var total = 0;
        foreach (var value in _speedDeltas)
        {
            total += value;
        }

        var activeCount = 0;
        foreach (var value in _activeTicks)
        {
            if (value)
            {
                activeCount++;
            }
        }

        _speed = activeCount >= 3 ? Math.Min(240, total * 60.0 / activeCount) : 0;
        if (_speed > _maxSpeedSession)
        {
            _maxSpeedSession = _speed;
        }

        _speedHistory.Add(_speed);
        while (_speedHistory.Count > 60)
        {
            _speedHistory.RemoveAt(0);
        }
    }

    private void RollOverDay()
    {
        Persist();
        _currentDate = DateTime.Today;
        _history.EnsureToday(_currentDate);
        LoadBaseFromHistory();

        _sessionWords = 0;
        _sessionWritingSeconds = 0;
        _sessionRestSeconds = 0;
        _sessionBreaks = 0;
        _maxSpeedSession = 0;
        _speed = 0;
        _lastSpeedWords = 0;
        _continuousWritingSeconds = 0;
        _restRunSeconds = 0;
        _breakCountedForRun = false;
        _breakDueNotified = false;
        _lastReminderFocus = 0;
        _goalNotified = false;
        _speedDeltas.Clear();
        _activeTicks.Clear();
        _speedHistory.Clear();

        DayChanged?.Invoke(this, EventArgs.Empty);
        NotifyAll();
    }

    private void OnSettingChanged(object? sender, string propertyName)
    {
        switch (propertyName)
        {
            case nameof(AppSettings.DailyGoal):
                _goalNotified = DailyGoal > 0 && WordsToday >= DailyGoal;
                NotifyAll();
                break;
            case nameof(AppSettings.BreakReminderMinutes):
                _breakDueNotified = false;
                _lastReminderFocus = 0;
                NotifyAll();
                break;
            case nameof(AppSettings.IdleThresholdSeconds):
                NotifyAll();
                break;
        }
    }

    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsWriting));
        OnPropertyChanged(nameof(IsResting));
        OnPropertyChanged(nameof(IsCurrentlyActive));
        OnPropertyChanged(nameof(IsStarted));
        OnPropertyChanged(nameof(ShowStartHint));
    }

    private void NotifyAll() => OnPropertyChanged(string.Empty);

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _settings.SettingChanged -= OnSettingChanged;
        Persist();
    }
}
