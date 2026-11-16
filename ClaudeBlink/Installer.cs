using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ClaudeBlink;

/// <summary>Adds/removes the Claude Code hooks in ~/.claude/settings.json and the run-at-login entry.</summary>
static class Installer
{
    const string Marker = "ClaudeBlinkHook.exe";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    // event, matcher (null = all), async (per-tool events run in the background so they never slow Claude down)
    static readonly (string Event, string? Matcher, bool Async)[] Hooks =
    [
        ("UserPromptSubmit", null, false),
        ("PreToolUse", "AskUserQuestion|ExitPlanMode", false),
        ("PostToolUse", "*", true),
        ("PostToolUseFailure", "*", true),
        ("PermissionRequest", "*", false),
        ("PermissionDenied", "*", true),
        ("Notification", null, false),
        ("Stop", null, false),
        ("StopFailure", null, false),
        ("SubagentStart", null, false),
        ("SubagentStop", null, false),
        ("PreCompact", null, false),
        ("PostCompact", null, false),
        ("SessionStart", "compact", false),
        ("SessionEnd", null, false),
    ];

    public static int Install(bool startup)
    {
        string exe = Environment.ProcessPath!;
        string hookExe = Path.Combine(Path.GetDirectoryName(exe)!, "ClaudeBlinkHook.exe");
        if (!File.Exists(hookExe)) { Console.WriteLine($"Missing {hookExe}"); return 1; }
        string command = $"\"{hookExe.Replace('\\', '/')}\"";

        var root = LoadSettings();
        RemoveOurHooks(root);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var (ev, matcher, isAsync) in Hooks)
        {
            var arr = hooks[ev] as JsonArray ?? new JsonArray();
            hooks[ev] = arr;
            var entry = new JsonObject();
            if (matcher is not null) entry["matcher"] = matcher;
            var hook = new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = 5 };
            if (isAsync) hook["async"] = true;
            entry["hooks"] = new JsonArray(hook);
            arr.Add(entry);
        }
        SaveSettings(root);
        Console.WriteLine($"Hooks added to {SettingsPath} -> {command}");

        if (startup)
        {
            // The tray app applies this itself the next time it runs (registry writes from a Claude session may be redirected).
            var cfg = Config.Load();
            cfg.StartWithWindows = true;
            cfg.Save();
            try { StartWithWindows = true; } catch { }
            Console.WriteLine("Set to start at sign-in (applied by the tray app).");
        }
        return 0;
    }

    public static int Uninstall()
    {
        var root = LoadSettings();
        RemoveOurHooks(root);
        SaveSettings(root);
        Console.WriteLine($"Hooks removed from {SettingsPath}");
        var cfg = Config.Load();
        cfg.StartWithWindows = false;
        cfg.Save(); // a running tray app picks this up and removes its own run-at-sign-in entry
        try { StartWithWindows = false; } catch { }
        Console.WriteLine("Run-at-sign-in turned off.");
        return 0;
    }

    /// <summary>The HKCU Run entry that starts the tray app at login.</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("ClaudeBlink") is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("ClaudeBlink", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("ClaudeBlink", throwOnMissingValue: false);
        }
    }

    static void RemoveOurHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var name in hooks.Select(p => p.Key).ToList())
        {
            if (hooks[name] is not JsonArray arr) continue;
            foreach (var entry in arr.OfType<JsonObject>().ToList())
            {
                if (entry["hooks"] is not JsonArray inner) continue;
                foreach (var h in inner.OfType<JsonObject>().Where(h => ((string?)h["command"])?.Contains(Marker, StringComparison.OrdinalIgnoreCase) == true).ToList())
                    inner.Remove(h);
                if (inner.Count == 0) arr.Remove(entry);
            }
            if (arr.Count == 0) hooks.Remove(name);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    static JsonObject LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return new JsonObject();
        var text = File.ReadAllText(SettingsPath);
        var bak = SettingsPath + ".claudeblink.bak";
        File.WriteAllText(bak, text);
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
               ?? throw new InvalidOperationException("settings.json is not a JSON object");
    }

    static void SaveSettings(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(SettingsPath, root.ToJsonString(opts) + Environment.NewLine);
    }
}
