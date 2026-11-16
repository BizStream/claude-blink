using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudeBlink;

/// <summary>Tray icon + LED driver. Polls the session folder and renders the current status's effect.</summary>
sealed class TrayApp : ApplicationContext
{
    const int PollMs = 250, RenderMs = 15, PreviewMs = 4000;

    readonly Blink1 _led = new();
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = PollMs };
    readonly System.Windows.Forms.Timer _renderTimer = new() { Interval = RenderMs };
    readonly Stopwatch _clock = Stopwatch.StartNew();

    Config _cfg = Config.Load();
    DateTime _cfgMtime = SafeMtime(Config.FilePath);

    List<Session> _sessions = [];
    Status _agg = (Status)(-1);      // most urgent status across sessions
    Status _second = Status.Idle;     // next most urgent (split mode)
    IntPtr _hIcon;
    SettingsForm? _settings;

    // effect engine: one channel drives both LEDs together, or two drive them separately (split mode)
    readonly LedChannel _both, _led1, _led2;

    // preview from the Settings window
    Config? _previewCfg;          // settings being edited (null = no preview)
    Status? _previewStatus;       // one status to show, or null for the live statuses
    long _previewUntilMs;

    volatile bool _settingsRequested, _exitRequested;

    readonly Updater _updater = new();

    public TrayApp(EventWaitHandle showSettings, EventWaitHandle exitRequest, bool openSettingsNow)
    {
        new Thread(() => { if (exitRequest.WaitOne()) _exitRequested = true; }) { IsBackground = true }.Start();
        _settingsRequested = openSettingsNow;
        _both = new LedChannel(_led, 0);
        _led1 = new LedChannel(_led, 1);
        _led2 = new LedChannel(_led, 2);
        new Thread(() => { while (showSettings.WaitOne()) _settingsRequested = true; }) { IsBackground = true }.Start();

        _menu.Opening += (_, _) => BuildMenu();
        _tray = new NotifyIcon { ContextMenuStrip = _menu, Text = "Claude Blink", Visible = true };
        // Left-click opens the same menu as right-click. NotifyIcon only does that for right-click, so call its
        // own (private) ShowContextMenu, which positions the menu and handles focus correctly.
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(_tray, null);
        };

        _pollTimer.Tick += (_, _) => Guard("poll", Poll);
        _renderTimer.Tick += (_, _) => Guard("render", Render);
        SyncStartup();
        Poll();
        Render();
        _pollTimer.Start();
        _renderTimer.Start();

        Application.ApplicationExit += (_, _) => Shutdown();
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => _led.Off();
    }

    // A bad tick must never stop the timers; log once per distinct error.
    string? _lastError;
    void Guard(string where, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            var key = where + ex.GetType() + ex.Message;
            if (key != _lastError) { _lastError = key; Log.Error(where, ex); }
        }
    }

    // ---- sessions ----

    void Poll()
    {
        if (_exitRequested)
        {
            ExitThread();
            return;
        }
        if (_settingsRequested)
        {
            _settingsRequested = false;
            ShowSettings();
        }
        ReloadConfigIfChanged();
        _sessions = SessionStore.LoadAll(_cfg);
        var present = _sessions.Select(s => s.Status).Distinct().OrderByDescending(s => s).ToList();
        var agg = present.Count == 0 ? Status.Idle : present[0];
        _second = present.Count > 1 ? present[1] : agg;
        if (agg != _agg)
        {
            _agg = agg;
            UpdateTrayIcon();
        }
        WriteTrayState();
        _tray.Text = Truncate($"Claude Blink — {Summary()}", 127);

        _updater.Tick();
        // Apply a downloaded update only while nothing is happening, so the restart never hides a status.
        if (_updater.Ready is not null && agg is Status.Idle or Status.Done && _previewCfg is null && _settings is not { IsDisposed: false })
            ApplyUpdate();
    }

    void ApplyUpdate()
    {
        // ApplyUpdatesAndRestart exits the process at once, skipping ApplicationExit, so tidy up first.
        _pollTimer.Stop();
        _renderTimer.Stop();
        _led.Off();
        _tray.Visible = false;
        try { _updater.ApplyAndRestart(); }
        catch (Exception ex)
        {
            Log.Error("update-apply", ex);
            _updater.Discard(); // don't retry every poll; the next scheduled check tries again
            _tray.Visible = true;
            _pollTimer.Start();
            _renderTimer.Start();
        }
    }

    // ---- LED effects ----

    void Render()
    {
        long now = _clock.ElapsedMilliseconds;
        if (_previewCfg is not null && now >= _previewUntilMs) _previewCfg = null;
        var cfg = _previewCfg ?? _cfg;
        string suffix = _previewCfg is null ? "" : "|preview";

        StateStyle first, second;
        if (_previewCfg is not null && _previewStatus is { } only)
        {
            first = second = cfg.Effective(only);
        }
        else
        {
            first = cfg.Effective(_agg);
            second = cfg.SplitLeds ? cfg.Effective(_second) : first;
        }

        if (first.Key == second.Key)
        {
            // Same look on both LEDs: drive them together so they stay in sync.
            _led1.Reset();
            _led2.Reset();
            _both.Render(first, suffix, now);
        }
        else
        {
            _both.Reset();
            var (a, b) = cfg.SwapLeds ? (_led2, _led1) : (_led1, _led2);
            a.Render(first, suffix, now);
            b.Render(second, suffix, now);
        }
    }

    // Small snapshot for "ClaudeBlink.exe status", so a dark LED can be diagnosed.
    string _lastTrayState = "";
    void WriteTrayState()
    {
        string state = $"{{\"pid\":{Environment.ProcessId},\"status\":\"{_agg}\",\"secondStatus\":\"{_second}\",\"split\":{(_cfg.SplitLeds ? "true" : "false")},\"blink1Connected\":{(_led.IsConnected ? "true" : "false")},\"sessions\":{_sessions.Count}}}";
        if (state == _lastTrayState) return;
        _lastTrayState = state;
        try { File.WriteAllText(Path.Combine(Config.BaseDir, "tray-state.json"), state); }
        catch (Exception ex) { Log.Error("tray-state", ex); }
    }

    /// <summary>
    /// For a few seconds, show the settings being edited: one status, or (status null) the live statuses.
    /// A null config stops the preview.
    /// </summary>
    public void Preview(Config? cfg, Status? status)
    {
        _previewCfg = cfg;
        _previewStatus = status;
        _previewUntilMs = _clock.ElapsedMilliseconds + PreviewMs;
        Render();
    }

    public void ApplySettings(Config cfg)
    {
        _cfg = cfg;
        _cfg.Save();
        _cfgMtime = SafeMtime(Config.FilePath);
        _previewCfg = null;
        SyncStartup();
        UpdateTrayIcon();
        Poll();
        Render();
    }

    /// <summary>Make the run-at-sign-in registry entry match the setting (done here, outside any Claude session).</summary>
    void SyncStartup()
    {
        try
        {
            if (Installer.StartWithWindows != _cfg.StartWithWindows) Installer.StartWithWindows = _cfg.StartWithWindows;
        }
        catch (Exception ex) { Log.Error("startup", ex); }
    }

    void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }
        Program.ApplyTheme();
        _settings = new SettingsForm(_cfg, Preview, ApplySettings) { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? _tray.Icon };
        _settings.Show();
    }

    // ---- tray UI ----

    string Summary()
    {
        if (_sessions.Count == 0) return "no active sessions" + (_led.IsConnected ? "" : " (blink(1) not found)");
        var parts = _sessions.GroupBy(s => s.Status).OrderByDescending(g => g.Key).Select(g => $"{g.Count()} {Label(g.Key)}");
        return string.Join(", ", parts) + (_led.IsConnected ? "" : " (blink(1) not found)");
    }

    public static string Label(Status s) => s switch
    {
        Status.LongWait => "long wait",
        Status.NeedsInput => "needs input",
        Status.JustFinished => "just finished",
        Status.Compacting => "compacting",
        Status.Subagents => "subagents running",
        Status.Running => "running",
        Status.Done => "done",
        _ => "idle",
    };

    void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem(Summary()) { Enabled = false });
        foreach (var s in _sessions.OrderByDescending(s => s.Status).ThenByDescending(s => s.LastActivityUtc))
        {
            string name = string.IsNullOrEmpty(s.Cwd) ? s.Id[..8] : Path.GetFileName(s.Cwd.TrimEnd('\\', '/'));
            string agents = s.Subagents > 0 && s.Status != Status.Subagents ? $", {s.Subagents} subagent{(s.Subagents == 1 ? "" : "s")}" : "";
            var item = new ToolStripMenuItem($"{name} — {Label(s.Status)}{agents} ({Ago(s.LastActivityUtc)})")
            {
                Image = Dot(ColorFor(s.Status), 12),
            };
            var forget = new ToolStripMenuItem("Forget this session");
            var id = s.Id;
            forget.Click += (_, _) => { SessionFiles.Delete(id); Poll(); };
            item.DropDownItems.Add(forget);
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Clear done sessions", null, (_, _) => { SessionStore.Clear(s => s.Status is Status.Done or Status.JustFinished, _cfg); Poll(); });
        _menu.Items.Add("Clear all sessions", null, (_, _) => { SessionStore.Clear(_ => true, _cfg); Poll(); });
        _menu.Items.Add(new ToolStripSeparator());
        var settings = new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings());
        settings.Font = new Font(settings.Font, FontStyle.Bold);
        _menu.Items.Add(settings);
        _menu.Items.Add("Open data folder", null, (_, _) => OpenFolder(Config.BaseDir));
        _menu.Items.Add(new ToolStripSeparator());
        if (_updater.Ready is { } ready)
            _menu.Items.Add($"Restart to update to {ready.TargetFullRelease.Version}", null, (_, _) => ApplyUpdate());
        else if (_updater.Enabled)
            _menu.Items.Add($"Check for updates (v{Updater.CurrentVersion})", null, async (_, _) =>
            {
                await _updater.CheckAsync();
                string msg = _updater.Ready is { } r ? $"Version {r.TargetFullRelease.Version} is ready. It installs when no session is active."
                    : _updater.LastError is { } err ? $"Update check failed: {err}"
                    : $"You're on the latest version ({Updater.CurrentVersion}).";
                _tray.ShowBalloonTip(5000, "Claude Blink", msg, ToolTipIcon.None);
            });
        else
            _menu.Items.Add(new ToolStripMenuItem($"Version {Updater.CurrentVersion} (dev build, no updates)") { Enabled = false });
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    Color ColorFor(Status s) => _cfg.For(s) is { Effect: not LedEffect.Off } st ? st.BaseColor : Color.Empty;

    void UpdateTrayIcon()
    {
        using var bmp = Dot(ColorFor(_agg), 32);
        IntPtr h = bmp.GetHicon();
        var old = _tray.Icon;
        _tray.Icon = Icon.FromHandle(h);
        old?.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _hIcon = h;
    }

    static Bitmap Dot(Color c, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(2, 2, size - 5, size - 5);
        if (c.IsEmpty)
        {
            using var pen = new Pen(Color.Gray, Math.Max(1, size / 10f));
            g.DrawEllipse(pen, r);
        }
        else
        {
            using var brush = new SolidBrush(c);
            g.FillEllipse(brush, r);
            using var pen = new Pen(Color.FromArgb(120, 0, 0, 0), Math.Max(1, size / 16f));
            g.DrawEllipse(pen, r);
        }
        return bmp;
    }

    void ReloadConfigIfChanged()
    {
        var m = SafeMtime(Config.FilePath);
        if (m == _cfgMtime) return;
        _cfgMtime = m;
        _cfg = Config.Load();
        SyncStartup();
        UpdateTrayIcon();
    }

    void Shutdown()
    {
        _pollTimer.Stop();
        _renderTimer.Stop();
        _led.Off();
        _led.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    static DateTime SafeMtime(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m ago" : $"{(int)d.TotalHours}h ago";
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}
