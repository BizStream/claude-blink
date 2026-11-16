namespace ClaudeBlink;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Setup.exe / Update.exe run the exe with special arguments for these hooks; Run() handles them and exits.
        // For a normal launch (or a dev build that was never installed) it returns straight away.
        Velopack.VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => Installer.Install(startup: true))
            .OnAfterUpdateFastCallback(_ => Installer.Install(startup: false)) // refresh hooks; keep the user's sign-in choice
            .OnBeforeUpdateFastCallback(_ => RequestTrayExit())
            .OnBeforeUninstallFastCallback(_ => { RequestTrayExit(); Installer.Uninstall(); })
            .Run();

        switch (args.FirstOrDefault()?.ToLowerInvariant())
        {
            case "install":
                return Installer.Install(startup: !args.Contains("--no-startup"));
            case "uninstall":
                return Installer.Uninstall();
            case "test":
                return LedTest();
            case "exit": // ask a running tray app to quit cleanly (used when updating)
                Console.WriteLine(RequestTrayExit() ? "exited" : "not running");
                return 0;
            case "status":
                foreach (var s in SessionStore.LoadAll(Config.Load()))
                    Console.WriteLine($"{s.Id}	{s.State}	{s.Status}	subagents={s.Subagents}	{s.Cwd}");
                var trayState = Path.Combine(Config.BaseDir, "tray-state.json");
                bool running = Mutex.TryOpenExisting(@"Local\ClaudeBlinkTray", out var m);
                m?.Dispose();
                Console.WriteLine($"data: {Config.BaseDir}");
                Console.WriteLine(running
                    ? $"tray: running, last state {(File.Exists(trayState) ? File.ReadAllText(trayState) : "(unknown)")}"
                    : "tray: NOT running");
                return 0;
        }

        return RunTray(args);
    }

    // Kept out of Main so the hook path never loads WinForms (hooks run on every tool call).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int RunTray(string[] args)
    {
        // Launching the exe again (e.g. from Explorer) opens Settings in the running instance.
        using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudeBlinkShowSettings");
        using var exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudeBlinkExit");
        using var mutex = new Mutex(true, @"Local\ClaudeBlinkTray", out bool first);
        if (!first)
        {
            showSettings.Set();
            return 0;
        }

        // Log anything unexpected and keep running instead of showing a crash dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("ui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("fatal", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        ThemeOverride = args.SkipWhile(a => a != "--theme").Skip(1).FirstOrDefault() switch
        {
            "dark" => SystemColorMode.Dark,
            "light" => SystemColorMode.Classic,
            _ => null,
        };
        ApplyTheme();
        Application.Run(new TrayApp(showSettings, exitRequest, openSettingsNow: args.Contains("--settings")));
        return 0;
    }

    /// <summary>Asks a running tray app to quit cleanly (LED off) and waits up to 5s. False if none was running.</summary>
    static bool RequestTrayExit()
    {
        if (!EventWaitHandle.TryOpenExisting(@"Local\ClaudeBlinkExit", out var ev)) return false;
        using (ev) ev.Set();
        for (int i = 0; i < 50 && Mutex.TryOpenExisting(@"Local\ClaudeBlinkTray", out var mx); i++) { mx.Dispose(); Thread.Sleep(100); }
        return true;
    }

    /// <summary>Forces light/dark (for testing); null follows the Windows app theme.</summary>
    static SystemColorMode? ThemeOverride;

    /// <summary>Follow the Windows light/dark app setting. Called again before each window opens so theme changes are picked up.</summary>
    public static void ApplyTheme()
    {
#pragma warning disable WFO5001 // WinForms dark mode is marked experimental
        Application.SetColorMode(ThemeOverride ?? SystemColorMode.System);
#pragma warning restore WFO5001
    }

    /// <summary>Show each status color (solid) so the device can be checked by eye. Exit the tray app first.</summary>
    static int LedTest()
    {
        var cfg = Config.Load();
        using var led = new Blink1();
        foreach (var status in Enum.GetValues<Status>().Reverse())
        {
            var style = cfg.Effective(status);
            string name = TrayApp.Label(status);
            if (style.Effect == LedEffect.Off) continue;
            if (!led.Fade(style.LedColor, 200)) { Console.WriteLine("blink(1) not found"); return 1; }
            Console.WriteLine($"{name}: {style.Color} @ {style.Brightness}%");
            Thread.Sleep(2000);
        }
        led.Off();
        Console.WriteLine("off");
        return 0;
    }
}
