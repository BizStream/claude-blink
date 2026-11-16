using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeBlink;

/// <summary>What the LED shows. Higher value = higher priority when sessions differ.</summary>
enum Status { Idle = 0, Done, Running, Subagents, Compacting, JustFinished, NeedsInput, LongWait }

/// <summary>How the LED looks for one status.</summary>
sealed class StateStyle
{
    public string Color { get; set; } = "#FFFFFF";
    /// <summary>5-100 (%), or null to use the main brightness.</summary>
    public int? Brightness { get; set; }
    public LedEffect Effect { get; set; } = LedEffect.Solid;
    /// <summary>Only used by optional statuses; when off, sessions fall back to the underlying status.</summary>
    public bool Enabled { get; set; } = true;

    public StateStyle() { }
    public StateStyle(string color, LedEffect effect) => (Color, Effect) = (color, effect);

    /// <summary>A copy with the main brightness filled in when this status doesn't override it.</summary>
    public StateStyle WithBrightness(int main) =>
        new() { Color = Color, Brightness = Brightness ?? main, Effect = Effect, Enabled = Enabled };

    /// <summary>The picked color at full brightness.</summary>
    [JsonIgnore]
    public Color BaseColor
    {
        get
        {
            try { return ColorTranslator.FromHtml(Color); } catch { return System.Drawing.Color.White; }
        }
    }

    /// <summary>The color actually sent to the LED (brightness applied; black when Off).</summary>
    [JsonIgnore]
    public Color LedColor
    {
        get
        {
            if (Effect == LedEffect.Off) return System.Drawing.Color.Black;
            var c = BaseColor;
            double k = Math.Clamp(Brightness ?? 100, 0, 100) / 100.0;
            return System.Drawing.Color.FromArgb((int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
        }
    }

    [JsonIgnore]
    public string Key => $"{Color}|{Brightness}|{Effect}";

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>Settings, edited in the Settings window and stored in %USERPROFILE%\.claude-blink\config.json.</summary>
sealed class Config
{
    /// <summary>Main brightness (5-100 %), used by every status that doesn't set its own.</summary>
    public int Brightness { get; set; } = 20;

    public StateStyle LongWait { get; set; } = new("#FF0000", LedEffect.PulseSlow);
    public StateStyle NeedsInput { get; set; } = new("#FF8000", LedEffect.Pulse) { Brightness = 40 };
    public StateStyle JustFinished { get; set; } = new("#0000FF", LedEffect.Burst) { Brightness = 50 };
    public StateStyle Compacting { get; set; } = new("#B000FF", LedEffect.Flicker);
    public StateStyle Subagents { get; set; } = new("#00C8FF", LedEffect.Pulse) { Enabled = false };
    public StateStyle Running { get; set; } = new("#C0C0C0", LedEffect.Pulse);
    public StateStyle Done { get; set; } = new("#0000FF", LedEffect.Solid);
    public StateStyle Idle { get; set; } = new("#00FF00", LedEffect.Solid) { Brightness = 5 };

    /// <summary>Applied by the tray app itself (a registry write from inside a Claude session can be redirected).</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Split mode: one LED shows the most urgent status, the other the next one.</summary>
    public bool SplitLeds { get; set; } = true;
    /// <summary>Swap which physical LED gets the most urgent status in split mode.</summary>
    public bool SwapLeds { get; set; }

    /// <summary>Statuses that can be switched off (the rest always apply).</summary>
    public static readonly Status[] Optional = [Status.LongWait, Status.JustFinished, Status.Compacting, Status.Subagents];

    /// <summary>Needs input for this long becomes Long wait.</summary>
    public int LongWaitMinutes { get; set; } = 5;
    /// <summary>How long Just finished shows after a session completes.</summary>
    public int JustFinishedSeconds { get; set; } = 5;
    /// <summary>A running session (or subagent) with no activity for this long is treated as done (crashed/killed).</summary>
    public int StaleWorkingMinutes { get; set; } = 15;
    /// <summary>"Done" sessions are forgotten after this long with no activity.</summary>
    public double DoneExpireHours { get; set; } = 4;

    public StateStyle For(Status s) => s switch
    {
        Status.LongWait => LongWait,
        Status.NeedsInput => NeedsInput,
        Status.JustFinished => JustFinished,
        Status.Compacting => Compacting,
        Status.Subagents => Subagents,
        Status.Running => Running,
        Status.Done => Done,
        _ => Idle,
    };

    /// <summary>The style to actually show for a status (main brightness applied unless overridden).</summary>
    public StateStyle Effective(Status s) => For(s).WithBrightness(Brightness);

    public static IEnumerable<Status> AllStatuses => Enum.GetValues<Status>();

    public bool IsOn(Status s) => !Optional.Contains(s) || For(s).Enabled;

    public static string BaseDir => SessionFiles.BaseDir;
    public static string FilePath => Path.Combine(BaseDir, "config.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                // Files from the first version (flat WorkingColor/WaitingColor/...) aren't compatible: start from defaults.
                if (!text.Contains("\"NeedsInput\"")) return new Config();
                var cfg = JsonSerializer.Deserialize<Config>(text, Json) ?? new Config();
                // Before there was a main brightness every status stored its own. Make the most common value the
                // main brightness; statuses at that value (or Off, where it doesn't matter) follow it.
                if (System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject root && !root.ContainsKey("Brightness"))
                {
                    var lit = AllStatuses.Select(cfg.For).Where(st => st.Effect != LedEffect.Off && st.Brightness is not null).ToList();
                    if (lit.Count > 0)
                        cfg.Brightness = lit.GroupBy(st => st.Brightness!.Value).OrderByDescending(g => g.Count()).First().Key;
                    foreach (var st in AllStatuses.Select(cfg.For))
                        if (st.Brightness == cfg.Brightness || st.Effect == LedEffect.Off) st.Brightness = null;
                }
                return cfg;
            }
        }
        catch { /* fall back to defaults */ }
        return new Config();
    }

    public void Save()
    {
        Directory.CreateDirectory(BaseDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public Config Clone() => JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(this, Json), Json)!;
}
