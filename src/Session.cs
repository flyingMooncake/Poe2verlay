using System.Globalization;
using System.Text.Json;

namespace Poe2Verlay;

/// <summary>
/// A farming session the user starts and stops. Time only counts while actually playing: the game in front and some
/// keyboard / mouse input in the last <see cref="AppSettings.SessionIdleSeconds"/> (alt-tab, the Esc menu, going to
/// eat = paused). Profit = the runs finished with Ctrl+O while it runs; maps come from Client.txt.
/// </summary>
internal sealed class FarmSession
{
    public sealed class State
    {
        public DateTime Started { get; set; }
        public double PlayedSeconds { get; set; }
        public int Maps { get; set; }
        /// <summary>Runs by their Ctrl+I time: a second Ctrl+O on the same run replaces it.</summary>
        public Dictionary<string, RunTotal> Runs { get; set; } = new();
        /// <summary>Played seconds at each Ctrl+I, for the run's own played time.</summary>
        public Dictionary<string, double> RunStarts { get; set; } = new();
    }

    public sealed class RunTotal
    {
        public double Min { get; set; }
        public double Max { get; set; }
    }

    static string FilePath => Path.Combine(AppSettings.Dir, "session.json");
    static string CsvPath => Path.Combine(AppSettings.Dir, "sessions.csv");

    readonly bool _persist;
    State? _state;
    DateTime _lastTick = DateTime.UtcNow, _lastSave;
    double _lastIdle, _countedIdle;

    /// <param name="persist">false = memory only (self-test), never touches the user's session files.</param>
    public FarmSession(bool persist = true)
    {
        _persist = persist;
        if (!persist) return;
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) is { } s &&
                DateTime.Now - s.Started < TimeSpan.FromHours(24))
                _state = s;
        }
        catch (Exception ex)
        {
            Log.Write("session load failed: " + ex.Message);
        }
    }

    public bool Running => _state != null;
    /// <summary>Running, but the clock is stopped right now.</summary>
    public bool Paused { get; private set; }
    public DateTime Started => _state?.Started ?? DateTime.Now;
    public TimeSpan Played => TimeSpan.FromSeconds(_state?.PlayedSeconds ?? 0);
    public TimeSpan PausedTime => Running ? DateTime.Now - Started - Played : TimeSpan.Zero;
    public int Runs => _state?.Runs.Count ?? 0;
    public int Maps => _state?.Maps ?? 0;
    public double ProfitMin => _state?.Runs.Values.Sum(r => r.Min) ?? 0;
    public double ProfitMax => _state?.Runs.Values.Sum(r => r.Max) ?? 0;

    public void Start()
    {
        _state = new State { Started = DateTime.Now };
        Paused = false;
        _lastTick = DateTime.UtcNow;
        Save();
        Log.Write("session started");
    }

    /// <summary>Stop and append the session to sessions.csv.</summary>
    public void Stop()
    {
        if (_state == null) return;
        if (_persist)
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                const string header = "start,end,played_minutes,paused_minutes,runs,maps,profit_min_div,profit_max_div,div_per_hour_min,div_per_hour_max";
                var hours = Math.Max(Played.TotalHours, 1e-9);
                var line = string.Join(",",
                    Started.ToString("yyyy-MM-dd HH:mm:ss", inv), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv),
                    Played.TotalMinutes.ToString("0.0", inv), PausedTime.TotalMinutes.ToString("0.0", inv),
                    Runs, Maps, ProfitMin.ToString("0.####", inv), ProfitMax.ToString("0.####", inv),
                    (ProfitMin / hours).ToString("0.##", inv), (ProfitMax / hours).ToString("0.##", inv));
                if (!File.Exists(CsvPath)) File.WriteAllText(CsvPath, header + Environment.NewLine);
                File.AppendAllText(CsvPath, line + Environment.NewLine);
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                Log.Write("session save failed: " + ex.Message);
            }
        }
        Log.Write($"session stopped: {Fmt.Duration(Played)} played, {Runs} runs, {Maps} maps, {Fmt.Range(ProfitMin, ProfitMax, signed: true)} div");
        _state = null;
        Paused = false;
    }

    /// <summary>Called about once a second.</summary>
    public void Tick(bool gameInFront, double idleLimitSeconds)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _lastTick).TotalSeconds, 0, 5);
        _lastTick = now;
        if (_state == null) return;

        var idle = Native.IdleSeconds();
        if (idle < _lastIdle) _countedIdle = 0; // there was input since the last tick
        _lastIdle = idle;

        var playing = gameInFront && idle < idleLimitSeconds;
        if (playing)
        {
            _state.PlayedSeconds += dt;
            _countedIdle += dt;
        }
        else if (!Paused && gameInFront)
        {
            // Went idle (Esc menu, away from the keyboard): the time since the last input wasn't play either.
            _state.PlayedSeconds = Math.Max(0, _state.PlayedSeconds - Math.Min(_countedIdle, idle));
        }
        if (!playing) _countedIdle = 0;
        if (playing == Paused) Log.Write(playing ? "session resumed" : $"session paused ({(gameInFront ? "idle" : "game not in front")})");
        Paused = !playing;

        if ((now - _lastSave).TotalSeconds >= 30) Save();
    }

    /// <summary>Client.txt said a new area was generated: maps and logbook islands count as maps.</summary>
    public void AreaEntered(string area)
    {
        if (_state == null) return;
        if (area.StartsWith("Map", StringComparison.Ordinal) || area.StartsWith("ExpeditionLogBook", StringComparison.Ordinal))
        {
            _state.Maps++;
            Save();
        }
    }

    public void RunStarted(InventorySnapshot start)
    {
        if (_state == null) return;
        _state.RunStarts[Key(start.Time)] = _state.PlayedSeconds;
        Save();
    }

    /// <summary>Count a finished run (again: replaces it). Returns the run's played time when it started in this session.</summary>
    public TimeSpan? RunFinished(RunReport report)
    {
        if (_state == null) return null;
        var key = Key(report.Start.Snapshot.Time);
        _state.Runs[key] = new RunTotal { Min = report.ProfitMin, Max = report.ProfitMax };
        Save();
        return _state.RunStarts.TryGetValue(key, out var at) ? TimeSpan.FromSeconds(_state.PlayedSeconds - at) : null;
    }

    static string Key(DateTime t) => t.ToString("O", CultureInfo.InvariantCulture);

    public void Save()
    {
        _lastSave = DateTime.UtcNow;
        if (_state == null || !_persist) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_state));
        }
        catch (Exception ex)
        {
            Log.Write("session save failed: " + ex.Message);
        }
    }
}
