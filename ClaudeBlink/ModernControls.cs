using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace ClaudeBlink;

/// <summary>Windows 11-style palette for the current light/dark mode.</summary>
static class Theme
{
#pragma warning disable WFO5001 // WinForms dark mode is marked experimental
    public static bool Dark => Application.IsDarkModeEnabled;
#pragma warning restore WFO5001

    public static Color Window => Dark ? Rgb(0x202020) : Rgb(0xF3F3F3);
    public static Color Card => Dark ? Rgb(0x2B2B2B) : Rgb(0xFBFBFB);
    public static Color CardBorder => Dark ? Rgb(0x1D1D1D) : Rgb(0xE5E5E5);
    public static Color Separator => Dark ? Rgb(0x3A3A3A) : Rgb(0xEAEAEA);
    public static Color Text => Dark ? Rgb(0xFFFFFF) : Rgb(0x1B1B1B);
    public static Color SecondaryText => Dark ? Rgb(0xA8A8A8) : Rgb(0x5F5F5F);
    public static Color DisabledText => Dark ? Rgb(0x6E6E6E) : Rgb(0xA0A0A0);
    public static Color Hover => Dark ? Rgb(0x383838) : Rgb(0xEDEDED);
    public static Color Pressed => Dark ? Rgb(0x323232) : Rgb(0xE3E3E3);
    public static Color ControlFill => Dark ? Rgb(0x373737) : Rgb(0xFEFEFE);
    public static Color ControlBorder => Dark ? Rgb(0x454545) : Rgb(0xD5D5D5);

    /// <summary>The user's Windows accent color.</summary>
    public static Color Accent
    {
        get
        {
            try
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
                {
                    var c = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                    // Lighten for dark mode the way Windows does, so it reads on dark backgrounds.
                    return Dark ? Mix(c, Color.White, 0.35) : c;
                }
            }
            catch { }
            return Dark ? Rgb(0x4CC2FF) : Rgb(0x005FB8);
        }
    }

    public static Color OnAccent => Dark ? Color.Black : Color.White;

    public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
    {
        string family = style.HasFlag(FontStyle.Bold) ? "Segoe UI Variable Text Semibold" : "Segoe UI Variable Text";
        if (!Installed(family)) family = "Segoe UI";
        var f = new Font(family, size, family.EndsWith("Semibold") ? style & ~FontStyle.Bold : style);
        return f;
    }

    public static Font DisplayFont(float size) =>
        Installed("Segoe UI Variable Display Semibold") ? new Font("Segoe UI Variable Display Semibold", size) : new Font("Segoe UI Semibold", size);

    public static Font IconFont(float size) =>
        new(Installed("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", size);

    static readonly HashSet<string> Families = new InstalledFontCollection().Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    static bool Installed(string name) => Families.Contains(name);

    public static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Rounded "card" panel, like the groups in Windows 11 Settings.</summary>
sealed class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Card;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = 8f * DeviceDpi / 96f;
        using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), radius);
        using var fill = new SolidBrush(Theme.Card);
        using var pen = new Pen(Theme.CardBorder);
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
    }
}

/// <summary>Base for small owner-drawn controls: transparent-looking background, hover/press tracking.</summary>
abstract class ModernControl : Control
{
    protected bool Hovered, Pressed;

    protected ModernControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
    }

    protected int Px(float logical) => (int)Math.Round(logical * DeviceDpi / 96f);

    protected override void OnMouseEnter(EventArgs e) { Hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hovered = Pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); }

    protected void PaintParentBackground(Graphics g) => g.Clear(Parent?.BackColor ?? Theme.Window);

    protected void DrawFocus(Graphics g, RectangleF r, float radius)
    {
        if (!Focused || !ShowFocusCues) return;
        using var pen = new Pen(Theme.Text, Px(1.5f));
        using var path = Theme.RoundRect(RectangleF.Inflate(r, -Px(1), -Px(1)), radius);
        g.DrawPath(pen, path);
    }
}

/// <summary>Windows 11-style on/off switch, with optional text to its right.</summary>
sealed class ToggleSwitch : ModernControl
{
    bool _checked;
    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        AutoSize = true;
        TabStop = true;
        Size = GetPreferredSize(Size.Empty);
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    Size SwitchSize => new(Px(40), Px(20));

    public override Size GetPreferredSize(Size proposedSize)
    {
        var sw = SwitchSize;
        if (string.IsNullOrEmpty(Text)) return new Size(sw.Width + Px(4), sw.Height + Px(8));
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(sw.Width + Px(12) + t.Width, Math.Max(sw.Height + Px(8), t.Height + Px(4)));
    }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintParentBackground(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var sw = SwitchSize;
        var track = new RectangleF(Px(2), (Height - sw.Height) / 2f, sw.Width - 1, sw.Height - 1);
        float r = track.Height / 2;
        using var path = Theme.RoundRect(track, r);

        Color accent = Enabled ? Theme.Accent : Theme.DisabledText;
        if (_checked)
        {
            using var fill = new SolidBrush(Hovered && Enabled ? Theme.Mix(accent, Theme.Card, 0.12) : accent);
            g.FillPath(fill, path);
        }
        else
        {
            using var fill = new SolidBrush(Hovered && Enabled ? Theme.Hover : Theme.ControlFill);
            using var pen = new Pen(Enabled ? Theme.SecondaryText : Theme.DisabledText, Px(1));
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        float knob = (Pressed ? 0.75f : Hovered ? 0.68f : 0.6f) * track.Height;
        float cx = _checked ? track.Right - r : track.Left + r;
        float cy = track.Top + r;
        Color knobColor = _checked ? Theme.OnAccent : Enabled ? Theme.SecondaryText : Theme.DisabledText;
        using (var kb = new SolidBrush(knobColor))
            g.FillEllipse(kb, cx - knob / 2, cy - knob / 2, knob, knob);

        DrawFocus(g, track, r);

        if (!string.IsNullOrEmpty(Text))
        {
            var textRect = new Rectangle((int)track.Right + Px(10), 0, Width - (int)track.Right - Px(10), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? Theme.Text : Theme.DisabledText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }
}

/// <summary>Rounded color chip showing its hex value; click to pick a color.</summary>
sealed class SwatchButton : ModernControl
{
    Color _color = Color.White;

    public SwatchButton()
    {
        AutoSize = true;
        TabStop = true;
        Size = GetPreferredSize(Size.Empty);
    }

    public Color Color
    {
        get => _color;
        set { _color = value; Text = StateStyle.ToHex(value); Invalidate(); }
    }

    bool _rainbow;

    /// <summary>Shows a rainbow gradient instead of the color (for the Rainbow effect, which ignores color).</summary>
    public bool Rainbow
    {
        get => _rainbow;
        set { if (_rainbow == value) return; _rainbow = value; Invalidate(); }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText("#WWWWWW", Font);
        return new Size(t.Width + Px(16), t.Height + Px(12));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintParentBackground(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(Px(1), Px(2), Width - Px(2) - 1, Height - Px(4) - 1);
        float radius = Px(4);
        using var path = Theme.RoundRect(r, radius);

        if (_rainbow)
        {
            Color[] stops = [Color.Red, Color.Orange, Color.Yellow, Color.LimeGreen, Color.DeepSkyBlue, Color.MediumPurple];
            using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(r, stops[0], stops[^1], 0f))
            {
                fill.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend(stops.Length)
                {
                    Colors = stops,
                    Positions = [0f, 0.2f, 0.4f, 0.6f, 0.8f, 1f],
                };
                g.FillPath(fill, path);
            }
            using (var pen = new Pen(Theme.ControlBorder, Px(1))) g.DrawPath(pen, path);
            TextRenderer.DrawText(g, "Rainbow", Font, Rectangle.Round(r), Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            DrawFocus(g, r, radius);
            return;
        }

        Color c = Enabled ? _color : Theme.Mix(_color, Theme.Card, 0.7);
        using (var fill = new SolidBrush(c)) g.FillPath(fill, path);
        using (var pen = new Pen(Hovered && Enabled ? Theme.Text : Theme.ControlBorder, Hovered && Enabled ? Px(1.5f) : Px(1)))
            g.DrawPath(pen, path);

        double luminance = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), luminance > 140 ? Color.Black : Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        DrawFocus(g, r, radius);
    }
}

/// <summary>Borderless icon button (Segoe Fluent Icons glyph) with a hover background.</summary>
sealed class IconButton : ModernControl
{
    public IconButton(string glyph)
    {
        Text = glyph;
        TabStop = true;
        Font = Theme.IconFont(12f);
        AutoSize = true;
        Size = GetPreferredSize(Size.Empty);
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Px(36), Px(32));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintParentBackground(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        if (Enabled && (Hovered || Pressed))
        {
            using var path = Theme.RoundRect(r, Px(4));
            using var fill = new SolidBrush(Pressed ? Theme.Pressed : Theme.Hover);
            g.FillPath(fill, path);
        }
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), Enabled ? Theme.Text : Theme.DisabledText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        DrawFocus(g, r, Px(4));
    }
}

/// <summary>Rounded push button; Accent = filled with the Windows accent color.</summary>
sealed class PillButton : ModernControl, IButtonControl
{
    public bool Accent { get; init; }
    public DialogResult DialogResult { get; set; }
    bool _isDefault;

    public PillButton(string text)
    {
        Text = text;
        TabStop = true;
        AutoSize = true;
        Size = GetPreferredSize(Size.Empty);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(Math.Max(Px(96), t.Width + Px(28)), t.Height + Px(14));
    }

    public void NotifyDefault(bool value) { _isDefault = value; Invalidate(); }
    public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (DialogResult != DialogResult.None && FindForm() is { } f) f.DialogResult = DialogResult;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { PerformClick(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintParentBackground(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = Px(4);
        using var path = Theme.RoundRect(r, radius);

        Color fill, text;
        if (Accent)
        {
            var a = Theme.Accent;
            fill = !Enabled ? Theme.DisabledText : Pressed ? Theme.Mix(a, Theme.Window, 0.2) : Hovered ? Theme.Mix(a, Theme.Window, 0.1) : a;
            text = Theme.OnAccent;
        }
        else
        {
            fill = Pressed ? Theme.Pressed : Hovered ? Theme.Hover : Theme.ControlFill;
            text = Enabled ? Theme.Text : Theme.DisabledText;
        }
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (!Accent)
            using (var pen = new Pen(Theme.ControlBorder)) g.DrawPath(pen, path);

        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        DrawFocus(g, r, radius);
    }
}
