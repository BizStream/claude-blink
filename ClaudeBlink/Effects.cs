namespace ClaudeBlink;

enum LedEffect { Solid, BlinkSlow, Blink, BlinkFast, PulseSlow, Pulse, PulseFast, Heartbeat, Burst, Flicker, Rainbow, Off }

/// <summary>Fade to <see cref="Color"/> over <see cref="FadeMs"/>, then hold until <see cref="DurationMs"/> has passed.</summary>
readonly record struct Step(int DurationMs, Color Color, int FadeMs);

/// <summary>Each effect is an endless sequence of fade steps; the device does the smooth fading itself.</summary>
static class Effects
{
    /// <summary>Order shown in the Settings dropdown.</summary>
    public static readonly LedEffect[] All =
    [
        LedEffect.Solid,
        LedEffect.BlinkSlow, LedEffect.Blink, LedEffect.BlinkFast,
        LedEffect.PulseSlow, LedEffect.Pulse, LedEffect.PulseFast,
        LedEffect.Heartbeat, LedEffect.Burst, LedEffect.Flicker, LedEffect.Rainbow,
        LedEffect.Off,
    ];

    public static string Label(LedEffect e) => e switch
    {
        LedEffect.BlinkSlow => "Blink (slow)",
        LedEffect.BlinkFast => "Blink (fast)",
        LedEffect.PulseSlow => "Pulse (slow)",
        LedEffect.PulseFast => "Pulse (fast)",
        _ => e.ToString(),
    };

    /// <summary>Rainbow cycles through every hue and Off is dark, so neither uses the picked color.</summary>
    public static bool UsesColor(LedEffect e) => e is not (LedEffect.Rainbow or LedEffect.Off);

    public static IEnumerable<Step> Steps(StateStyle style)
    {
        Color c = style.LedColor; // brightness applied; black for Off
        Color black = Color.Black;
        switch (style.Effect)
        {
            case LedEffect.BlinkSlow or LedEffect.Blink or LedEffect.BlinkFast:
            {
                int half = style.Effect switch { LedEffect.BlinkSlow => 750, LedEffect.BlinkFast => 100, _ => 500 };
                int fade = Math.Min(40, half / 3);
                while (true)
                {
                    yield return new(half, c, fade);
                    yield return new(half, black, fade);
                }
            }

            case LedEffect.PulseSlow or LedEffect.Pulse or LedEffect.PulseFast:
            {
                int half = style.Effect switch { LedEffect.PulseSlow => 2500, LedEffect.PulseFast => 500, _ => 1500 } / 2;
                while (true)
                {
                    yield return new(half, c, half);
                    yield return new(half, Scale(c, 0.06), half);
                }
            }

            case LedEffect.Heartbeat: // "lub-dub", rest; ~1.2 s per beat
                while (true)
                {
                    yield return new(130, c, 60);
                    yield return new(110, Scale(c, 0.12), 90);
                    yield return new(150, c, 60);
                    yield return new(810, Scale(c, 0.02), 400);
                }

            case LedEffect.Burst: // three quick flashes, then a pause
                while (true)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        yield return new(90, c, 15);
                        yield return new(90, black, 15);
                    }
                    yield return new(1500, black, 0);
                }

            case LedEffect.Flicker: // candle: random brightness changes
            {
                var rnd = new Random();
                while (true)
                {
                    int d = rnd.Next(50, 170);
                    yield return new(d, Scale(c, 0.3 + rnd.NextDouble() * 0.7), d);
                }
            }

            case LedEffect.Rainbow: // full hue circle every 6 s
            {
                double k = Math.Clamp(style.Brightness ?? 100, 0, 100) / 100.0;
                for (int i = 0; ; i = (i + 1) % 24)
                    yield return new(250, Scale(Hue(i * 15), k), 250);
            }

            default: // Solid / Off: re-sent every few seconds so a replugged device picks it back up
                while (true)
                    yield return new(5000, c, 250);
        }
    }

    public static Color Scale(Color c, double k) => Color.FromArgb((int)(c.R * k), (int)(c.G * k), (int)(c.B * k));

    /// <summary>Fully saturated color for a hue in degrees.</summary>
    static Color Hue(double h)
    {
        double x = 1 - Math.Abs(h / 60 % 2 - 1);
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (1d, x, 0d),
            < 120 => (x, 1d, 0d),
            < 180 => (0d, 1d, x),
            < 240 => (0d, x, 1d),
            < 300 => (x, 0d, 1d),
            _ => (1d, 0d, x),
        };
        return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }
}

/// <summary>Plays an effect on one LED target (0 = both, 1, 2).</summary>
sealed class LedChannel(Blink1 led, byte target)
{
    string _key = "";
    IEnumerator<Step>? _steps;
    long _stepEnd;

    public void Render(StateStyle style, string keySuffix, long now)
    {
        string key = style.Key + keySuffix;
        if (key != _key)
        {
            _key = key;
            _steps?.Dispose();
            _steps = Effects.Steps(style).GetEnumerator();
            _stepEnd = now; // start right away
        }
        if (now < _stepEnd) return;

        _steps!.MoveNext();
        var s = _steps.Current;
        bool ok = led.Fade(s.Color, s.FadeMs, target);
        // Keep to the schedule (no drift), unless we've fallen far behind.
        _stepEnd = now - _stepEnd > 500 ? now + s.DurationMs : _stepEnd + s.DurationMs;
        if (!ok) _stepEnd = now + 1000; // device missing: retry in a second
    }

    /// <summary>Forget what's playing so the next Render starts fresh.</summary>
    public void Reset() => _key = "";
}
