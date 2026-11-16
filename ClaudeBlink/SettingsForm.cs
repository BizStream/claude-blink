namespace ClaudeBlink;

/// <summary>
/// Per-status on/off, color, brightness and effect, plus LED mode and timing. Any change previews on the LED
/// right away; nothing is kept until Save. Laid out like Windows 11 Settings: section titles over rounded cards.
/// </summary>
sealed class SettingsForm : Form
{
    readonly Action<Config?, Status?> _preview;
    readonly Action<Config> _apply;
    Config _work;
    bool _loading;

    readonly ToolTip _tips = new() { InitialDelay = 400, ReshowDelay = 100 };
    readonly List<StatusRow> _rows = [];
    TrackBar _mainBrightness = null!;
    Label _mainBrightnessLabel = null!;
    NumericUpDown _longWaitMin = null!, _justFinishedSec = null!, _staleMin = null!, _expireHours = null!;
    ComboBox _ledMode = null!;
    ToggleSwitch _swapLeds = null!, _startup = null!;

    static readonly (Status Status, string Title, string Description)[] Statuses =
    [
        (Status.LongWait, "Long wait", "Needs input for longer than the time set below"),
        (Status.NeedsInput, "Needs input", "A session is waiting on you"),
        (Status.JustFinished, "Just finished", "A session completed in the last few seconds"),
        (Status.Compacting, "Compacting", "A session is compacting its context"),
        (Status.Subagents, "Subagents", "A session has subagents running"),
        (Status.Running, "Running", "A session is working"),
        (Status.Done, "Done", "Sessions finished; nothing needs you"),
        (Status.Idle, "Idle", "No Claude sessions"),
    ];

    // Each optional status falls back to this one when switched off.
    static readonly Dictionary<Status, string> FallsBackTo = new()
    {
        [Status.LongWait] = "Needs input",
        [Status.JustFinished] = "Done",
        [Status.Compacting] = "Running",
        [Status.Subagents] = "Running",
    };

    public SettingsForm(Config current, Action<Config?, Status?> preview, Action<Config> apply)
    {
        _preview = preview;
        _apply = apply;
        _work = current.Clone();

        Text = "Claude Blink Settings";
        Font = Theme.UiFont(9.75f);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.None; // sizes below are computed from DeviceDpi
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Padding.Empty;

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        root.Controls.Add(new Label { Text = "Claude Blink", Font = Theme.DisplayFont(18f), AutoSize = true, Margin = Pad(0, 0, 0, 0) });
        root.Controls.Add(new Label { Text = "How your blink(1) shows what your Claude sessions are doing", ForeColor = Theme.SecondaryText, AutoSize = true, Margin = Pad(1, 0, 0, 2) });

        AddSection(root, "Status lights", "Listed by priority. When sessions differ, the light shows the highest status.", BuildStatusCard());
        AddSection(root, "LEDs", null, BuildLedsCard());
        AddSection(root, "Timing", null, BuildTimingCard());
        AddSection(root, "General", null, BuildGeneralCard());
        root.Controls.Add(BuildButtons(out var save));

        // If the window would be taller than the screen, cap it and let the content scroll.
        var scroller = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Padding = Pad(24, 12, 24, 16) };
        scroller.Controls.Add(root);
        Controls.Add(scroller);
        Load += (_, _) =>
        {
            ActiveControl = save; // don't start with a focus box on the first slider
            var area = Screen.FromControl(this).WorkingArea;
            if (Height <= area.Height) return;
            var client = ClientSize;
            AutoSize = false;
            scroller.AutoSize = false;
            scroller.Dock = DockStyle.Fill;
            scroller.AutoScroll = true;
            ClientSize = new Size(client.Width + SystemInformation.VerticalScrollBarWidth, client.Height - (Height - area.Height));
            Top = area.Top;
        };

        LoadValues(_work);
        FormClosed += (_, _) => _preview(null, null);
    }

    // ---- layout helpers ----

    int S(float logical) => (int)Math.Round(logical * DeviceDpi / 96f);
    Padding Pad(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    void AddSection(TableLayoutPanel root, string title, string? caption, Card card)
    {
        root.Controls.Add(new Label { Text = title, Font = Theme.UiFont(10.5f, FontStyle.Bold), AutoSize = true, Margin = Pad(1, 14, 0, caption is null ? 6 : 0) });
        if (caption is not null)
            root.Controls.Add(new Label { Text = caption, ForeColor = Theme.SecondaryText, AutoSize = true, Margin = Pad(1, 0, 0, 6) });
        card.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        card.Margin = Padding.Empty;
        root.Controls.Add(card);
    }

    Card NewCard(Control content)
    {
        var card = new Card { Padding = Pad(16, 4, 12, 4) };
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        return card;
    }

    TableLayoutPanel Grid(int columns)
    {
        var grid = new TableLayoutPanel { ColumnCount = columns, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, BackColor = Theme.Card };
        for (int i = 0; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return grid;
    }

    /// <summary>Thin separators above each row from row <paramref name="firstRow"/> on.</summary>
    static void RowSeparators(TableLayoutPanel grid, int firstRow)
    {
        grid.CellPaint += (_, e) =>
        {
            if (e.Row < firstRow) return;
            using var pen = new Pen(Theme.Separator);
            e.Graphics.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Top, e.CellBounds.Right, e.CellBounds.Top);
        };
    }

    Label Secondary(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.SecondaryText, Anchor = AnchorStyles.Left, Margin = Pad(3, 6, 3, 6) };

    static Label Blank() => new() { AutoSize = true, Text = "", Margin = Padding.Empty };

    FlowLayoutPanel NameStack(string title, string description)
    {
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Pad(4, 6, 20, 6) };
        stack.Controls.Add(new Label { Text = title, AutoSize = true, Margin = Padding.Empty });
        stack.Controls.Add(new Label { Text = description, AutoSize = true, ForeColor = Theme.SecondaryText, Font = Theme.UiFont(9f), Margin = Padding.Empty });
        return stack;
    }

    TrackBar Slider() => new()
    {
        Minimum = 5, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10,
        AutoSize = false, Size = new Size(S(150), S(30)), Anchor = AnchorStyles.Left, TickStyle = TickStyle.None, Margin = Pad(0, 3, 0, 3),
    };

    Label PercentLabel() => new() { AutoSize = true, MinimumSize = new Size(S(44), 0), Anchor = AnchorStyles.Left, Margin = Pad(4, 3, 16, 3) };

    NumericUpDown Num(int min, int max, int step) =>
        new() { Minimum = min, Maximum = max, Increment = step, Width = S(76), Anchor = AnchorStyles.Left, TextAlign = HorizontalAlignment.Right, Margin = Pad(3, 6, 8, 6) };

    // ---- cards ----

    Card BuildStatusCard()
    {
        // On | Status | Color | Custom brightness | slider | % | Effect | Preview
        var grid = Grid(8);

        foreach (var (text, span) in new[] { ("", 1), ("Status", 1), ("Color", 1), ("Brightness", 3), ("Effect", 1), ("", 1) })
        {
            var h = new Label { Text = text, AutoSize = true, ForeColor = Theme.SecondaryText, Font = Theme.UiFont(9f), Margin = Pad(3, 6, 3, 0) };
            grid.Controls.Add(h);
            if (span > 1) grid.SetColumnSpan(h, span);
        }

        AddMainBrightnessRow(grid);
        foreach (var (status, title, description) in Statuses)
            AddRow(grid, status, title, description);

        RowSeparators(grid, firstRow: 2);
        return NewCard(grid);
    }

    void AddMainBrightnessRow(TableLayoutPanel grid)
    {
        _mainBrightness = Slider();
        _mainBrightnessLabel = PercentLabel();

        grid.Controls.Add(Blank());
        grid.Controls.Add(NameStack("Main brightness", "Used by every status without its own"));
        grid.Controls.Add(Blank());
        grid.Controls.Add(Blank());
        grid.Controls.Add(_mainBrightness);
        grid.Controls.Add(_mainBrightnessLabel);
        grid.Controls.Add(Blank());
        grid.Controls.Add(Blank());

        _mainBrightness.ValueChanged += (_, _) =>
        {
            _mainBrightnessLabel.Text = $"{_mainBrightness.Value}%";
            if (_loading) return;
            _work.Brightness = _mainBrightness.Value;
            foreach (var row in _rows) ShowInheritedBrightness(row);
            _preview(_work, null); // show the live statuses at the new brightness
        };
    }

    void AddRow(TableLayoutPanel grid, Status status, string title, string description)
    {
        var row = new StatusRow(status);

        Control onCell = Blank();
        if (FallsBackTo.TryGetValue(status, out var fallback))
        {
            row.Enabled = new ToggleSwitch { Anchor = AnchorStyles.Left, Margin = Pad(0, 3, 8, 3) };
            _tips.SetToolTip(row.Enabled, $"Show {title}. When off, these sessions show as {fallback}.");
            row.Enabled.CheckedChanged += (_, _) =>
            {
                UpdateRowEnabled(row);
                if (_loading) return;
                _work.For(status).Enabled = row.Enabled.Checked;
                if (row.Enabled.Checked) PreviewRow(row);
            };
            onCell = row.Enabled;
        }

        row.NameStack = NameStack(title, description);

        row.Swatch = new SwatchButton { Anchor = AnchorStyles.Left, Margin = Pad(3, 3, 12, 3) };
        _tips.SetToolTip(row.Swatch, "Pick a color");
        row.Swatch.Click += (_, _) => PickColor(row);

        row.Custom = new CheckBox { AutoSize = true, Anchor = AnchorStyles.Left, Margin = Pad(3, 3, 6, 3) };
        _tips.SetToolTip(row.Custom, "Use a custom brightness for this status");
        row.Brightness = Slider();
        row.BrightnessLabel = PercentLabel();
        row.Custom.CheckedChanged += (_, _) =>
        {
            UpdateRowEnabled(row);
            if (_loading) return;
            // Ticking starts from the main value; unticking goes back to following it.
            _work.For(status).Brightness = row.Custom.Checked ? row.Brightness.Value : null;
            ShowInheritedBrightness(row);
            PreviewRow(row);
        };
        row.Brightness.ValueChanged += (_, _) =>
        {
            row.BrightnessLabel.Text = $"{row.Brightness.Value}%";
            if (_loading || !row.Custom.Checked) return;
            _work.For(status).Brightness = row.Brightness.Value;
            PreviewRow(row);
        };

        row.Effect = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(132), Anchor = AnchorStyles.Left, MaxDropDownItems = 12, Margin = Pad(3, 3, 8, 3) };
        row.Effect.Items.AddRange(Effects.All.Select(e => new EffectItem(e)).ToArray<object>());
        row.Effect.SelectedIndexChanged += (_, _) =>
        {
            UpdateRowEnabled(row);
            if (_loading) return;
            _work.For(status).Effect = SelectedEffect(row);
            PreviewRow(row);
        };

        row.PreviewButton = new IconButton("") { Anchor = AnchorStyles.Left, Margin = Pad(0, 3, 0, 3) }; // "View" (eye)
        _tips.SetToolTip(row.PreviewButton, "Preview");
        row.PreviewButton.Click += (_, _) => PreviewRow(row);

        grid.Controls.Add(onCell);
        grid.Controls.Add(row.NameStack);
        grid.Controls.Add(row.Swatch);
        grid.Controls.Add(row.Custom);
        grid.Controls.Add(row.Brightness);
        grid.Controls.Add(row.BrightnessLabel);
        grid.Controls.Add(row.Effect);
        grid.Controls.Add(row.PreviewButton);
        _rows.Add(row);
    }

    Card BuildLedsCard()
    {
        var grid = Grid(2);
        _swapLeds = new ToggleSwitch { Text = "Swap which LED shows the most urgent status", Anchor = AnchorStyles.Left, Margin = Pad(0, 6, 3, 6) };
        _ledMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(380), Anchor = AnchorStyles.Left, Margin = Pad(3, 8, 3, 8) };
        _ledMode.Items.AddRange(["Both LEDs show the most urgent status", "Split: most urgent on one LED, the next on the other"]);
        _ledMode.SelectedIndexChanged += (_, _) =>
        {
            _swapLeds.Enabled = _ledMode.SelectedIndex == 1;
            if (_loading) return;
            _work.SplitLeds = _ledMode.SelectedIndex == 1;
            _preview(_work, null);
        };
        _swapLeds.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _work.SwapLeds = _swapLeds.Checked;
            _preview(_work, null);
        };

        grid.Controls.Add(new Label { Text = "Mode", AutoSize = true, Anchor = AnchorStyles.Left, Margin = Pad(4, 6, 24, 6) });
        grid.Controls.Add(_ledMode);
        grid.Controls.Add(Blank());
        grid.Controls.Add(_swapLeds);
        RowSeparators(grid, firstRow: 1);
        return NewCard(grid);
    }

    Card BuildTimingCard()
    {
        _longWaitMin = Num(1, 240, 1);
        _justFinishedSec = Num(2, 600, 1);
        _staleMin = Num(1, 240, 1);
        _expireHours = Num(1, 168, 1);

        var grid = Grid(3);
        void Add(string label, NumericUpDown n, string unit)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Pad(4, 6, 24, 6) });
            grid.Controls.Add(n);
            grid.Controls.Add(Secondary(unit));
        }
        Add("Needs input becomes Long wait after", _longWaitMin, "minutes");
        Add("Show Just finished for", _justFinishedSec, "seconds");
        Add("Treat a silent running session as done after", _staleMin, "minutes (catches killed terminals)");
        Add("Forget done sessions after", _expireHours, "hours");
        RowSeparators(grid, firstRow: 1);
        return NewCard(grid);
    }

    Card BuildGeneralCard()
    {
        _startup = new ToggleSwitch { Text = "Start Claude Blink when I sign in to Windows", Margin = Pad(0, 6, 3, 6) };
        var grid = Grid(1);
        grid.Controls.Add(_startup);
        return NewCard(grid);
    }

    Control BuildButtons(out PillButton save)
    {
        var bar = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Pad(0, 16, 0, 0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var defaults = new PillButton("Restore defaults") { Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        defaults.Click += (_, _) =>
        {
            _work = new Config();
            LoadValues(_work);
            _preview(_work, null);
        };

        var right = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        var saveButton = new PillButton("Save") { Accent = true, Margin = Pad(0, 0, 8, 0) };
        save = saveButton;
        var cancel = new PillButton("Cancel") { Margin = Padding.Empty };
        saveButton.Click += (_, _) =>
        {
            ReadTiming();
            _work.StartWithWindows = _startup.Checked;
            _apply(_work.Clone());
            Close();
        };
        cancel.Click += (_, _) => Close();
        right.Controls.Add(saveButton);
        right.Controls.Add(cancel);
        AcceptButton = saveButton;
        CancelButton = cancel;

        bar.Controls.Add(defaults, 0, 0);
        bar.Controls.Add(right, 1, 0);
        return bar;
    }

    // ---- values ----

    void LoadValues(Config c)
    {
        _loading = true;
        _mainBrightness.Value = Math.Clamp(c.Brightness, _mainBrightness.Minimum, _mainBrightness.Maximum);
        _mainBrightnessLabel.Text = $"{_mainBrightness.Value}%";
        foreach (var row in _rows)
        {
            var s = c.For(row.Status);
            if (row.Enabled is not null) row.Enabled.Checked = s.Enabled;
            row.Swatch.Color = s.BaseColor;
            row.Custom.Checked = s.Brightness is not null;
            row.Brightness.Value = Math.Clamp(s.Brightness ?? c.Brightness, row.Brightness.Minimum, row.Brightness.Maximum);
            row.BrightnessLabel.Text = $"{row.Brightness.Value}%";
            row.Effect.SelectedIndex = Math.Max(0, Array.IndexOf(Effects.All, s.Effect));
            UpdateRowEnabled(row);
        }
        _ledMode.SelectedIndex = c.SplitLeds ? 1 : 0;
        _swapLeds.Checked = c.SwapLeds;
        _swapLeds.Enabled = c.SplitLeds;
        _startup.Checked = c.StartWithWindows;
        Set(_longWaitMin, c.LongWaitMinutes);
        Set(_justFinishedSec, c.JustFinishedSeconds);
        Set(_staleMin, c.StaleWorkingMinutes);
        Set(_expireHours, c.DoneExpireHours);
        _loading = false;

        static void Set(NumericUpDown n, double v) => n.Value = (decimal)Math.Clamp(v, (double)n.Minimum, (double)n.Maximum);
    }

    /// <summary>A row without its custom-brightness box ticked mirrors the main brightness.</summary>
    void ShowInheritedBrightness(StatusRow row)
    {
        if (row.Custom.Checked) return;
        bool was = _loading;
        _loading = true;
        row.Brightness.Value = _mainBrightness.Value;
        row.BrightnessLabel.Text = $"{_mainBrightness.Value}%";
        _loading = was;
    }

    void UpdateRowEnabled(StatusRow row)
    {
        bool on = row.Enabled?.Checked ?? true;
        var effect = SelectedEffect(row);
        bool lit = effect != LedEffect.Off;
        row.NameStack.Controls[0].ForeColor = on ? Theme.Text : Theme.DisabledText;
        row.NameStack.Controls[1].ForeColor = on ? Theme.SecondaryText : Theme.DisabledText;
        row.Effect.Enabled = on;
        row.PreviewButton.Enabled = on;
        row.Swatch.Enabled = on && Effects.UsesColor(effect);
        row.Custom.Enabled = on && lit;
        row.Brightness.Enabled = on && lit && row.Custom.Checked;
        row.BrightnessLabel.ForeColor = on && lit && row.Custom.Checked ? Theme.Text : Theme.DisabledText;
    }

    void ReadTiming()
    {
        _work.LongWaitMinutes = (int)_longWaitMin.Value;
        _work.JustFinishedSeconds = (int)_justFinishedSec.Value;
        _work.StaleWorkingMinutes = (int)_staleMin.Value;
        _work.DoneExpireHours = (double)_expireHours.Value;
    }

    void PickColor(StatusRow row)
    {
        var style = _work.For(row.Status);
        using var dlg = new ColorDialog { Color = style.BaseColor, FullOpen = true, AnyColor = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        style.Color = StateStyle.ToHex(dlg.Color);
        row.Swatch.Color = dlg.Color;
        PreviewRow(row);
    }

    void PreviewRow(StatusRow row)
    {
        ReadTiming();
        _preview(_work, row.Status);
    }

    static LedEffect SelectedEffect(StatusRow row) => (row.Effect.SelectedItem as EffectItem)?.Effect ?? LedEffect.Solid;

    sealed record EffectItem(LedEffect Effect)
    {
        public override string ToString() => Effects.Label(Effect);
    }

    sealed class StatusRow(Status status)
    {
        public Status Status { get; } = status;
        public ToggleSwitch? Enabled;
        public FlowLayoutPanel NameStack = null!;
        public SwatchButton Swatch = null!;
        public CheckBox Custom = null!;
        public TrackBar Brightness = null!;
        public Label BrightnessLabel = null!;
        public ComboBox Effect = null!;
        public IconButton PreviewButton = null!;
    }
}
