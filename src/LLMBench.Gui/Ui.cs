using System.Globalization;

namespace LLMBench.Gui;

/// <summary>Small factory helpers so the forms can be built in code without the designer.</summary>
internal static class Ui
{
    /// <summary>System DPI factor (1.0 at 96 dpi, 1.5 at 144 dpi).</summary>
    public static readonly float Scale = GetScale();

    private static float GetScale()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96f;
    }

    /// <summary>Logical pixels → device pixels.</summary>
    public static int S(int logical) => (int)Math.Round(logical * Scale);

    public static readonly Font Mono = new("Consolas", 10f);
    public static readonly Font Bold = new("Segoe UI", 9.75f, FontStyle.Bold);
    public static readonly Font Big = new("Segoe UI", 11f, FontStyle.Bold);

    public static readonly Color Correct = Color.FromArgb(198, 239, 206);
    public static readonly Color Incorrect = Color.FromArgb(255, 199, 206);
    public static readonly Color Invalid = Color.FromArgb(255, 235, 156);
    public static readonly Color Error = Color.FromArgb(244, 176, 132);
    public static readonly Color Skipped = Color.FromArgb(230, 230, 230);
    public static readonly Color Running = Color.FromArgb(189, 215, 238);

    public static TextBox MonoBox(bool readOnly = true, bool wrap = true)
    {
        var t = NewMono(readOnly, wrap);
        // TextBoxes select all text when they get focus via tab switching; keep the caret at the start instead.
        t.Enter += (_, _) => t.BeginInvoke(() => { if (t.SelectionLength == t.TextLength) t.Select(0, 0); });
        return t;
    }

    private static TextBox NewMono(bool readOnly, bool wrap) => new()
    {
        Multiline = true,
        ReadOnly = readOnly,
        ScrollBars = ScrollBars.Both,
        WordWrap = wrap,
        Font = Mono,
        Dock = DockStyle.Fill,
        BackColor = readOnly ? SystemColors.Window : SystemColors.Window,
        HideSelection = false,
    };

    public static Label Label(string text, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 7, 3, 3),
        Font = bold ? Bold : null,
    };

    public static Button Button(string text, EventHandler onClick, int width = 0)
    {
        var b = new Button { Text = text, AutoSize = width == 0, Height = S(30), Margin = new Padding(3) };
        if (width > 0) b.Width = S(width);
        b.AutoSizeMode = AutoSizeMode.GrowOnly;
        b.Click += onClick;
        return b;
    }

    public static TextBox Text(string? placeholder = null) => new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        PlaceholderText = placeholder ?? "",
    };

    public static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        c.Items.AddRange(items);
        if (items.Length > 0) c.SelectedIndex = 0;
        return c;
    }

    public static SplitContainer Split(Orientation o, int distance = 0)
    {
        var s = new SplitContainer { Dock = DockStyle.Fill, Orientation = o, SplitterWidth = 6 };
        if (distance > 0) s.HandleCreated += (_, _) => TrySetSplitter(s, S(distance));
        return s;
    }

    public static void TrySetSplitter(SplitContainer s, int distance)
    {
        try
        {
            var max = (s.Orientation == Orientation.Vertical ? s.Width : s.Height) - s.Panel2MinSize - s.SplitterWidth;
            if (max > s.Panel1MinSize) s.SplitterDistance = Math.Clamp(distance, s.Panel1MinSize, max);
        }
        catch (InvalidOperationException) { }
    }

    public static FlowLayoutPanel ButtonBar(params Control[] controls)
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(2) };
        p.Controls.AddRange(controls);
        return p;
    }

    public static string Num(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    public static string Num(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
    public static string Num(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    public static void ShowError(IWin32Window owner, string message) =>
        MessageBox.Show(owner, message, "LLMBench", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>Text with \n line ends converted for Windows text boxes.</summary>
    public static string Crlf(string? s) => (s ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
}

/// <summary>
/// A two-column grid (label | control) inside a group box. Uses fixed, DPI-scaled row heights
/// (AutoSize chains let the content grow wider than the window).
/// </summary>
internal sealed class FormGroup : GroupBox
{
    public const int Row = 34;
    private readonly TableLayoutPanel _table;
    private int _content;

    public FormGroup(string title)
    {
        Text = title;
        Dock = DockStyle.Top;
        Padding = new Padding(Ui.S(6), Ui.S(4), Ui.S(6), Ui.S(6));
        Margin = new Padding(Ui.S(4));
        Font = Ui.Bold;
        _table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Font = new Font("Segoe UI", 9.75f) };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_table);
        UpdateHeight();
    }

    private int AddRow(int height)
    {
        int h = Ui.S(height > 0 ? height + 8 : Row);
        int row = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        _content += h;
        UpdateHeight();
        return row;
    }

    private void UpdateHeight() => Height = _content + Ui.S(36);

    public T Add<T>(string label, T control, int height = 0) where T : Control
    {
        int row = AddRow(height);
        var l = Ui.Label(label);
        if (height > 0) l.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        _table.Controls.Add(l, 0, row);
        if (height > 0) control.Dock = DockStyle.Fill;
        else if (control is CheckBox or NumericUpDown) control.Anchor = AnchorStyles.Left;
        else control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _table.Controls.Add(control, 1, row);
        return control;
    }

    /// <summary>A control spanning both columns; height in logical pixels (0 = one row).</summary>
    public T AddWide<T>(T control, int height = 0) where T : Control
    {
        int row = AddRow(height);
        control.Dock = DockStyle.Fill;
        if (control is Label lab) lab.AutoSize = false;
        if (control is FlowLayoutPanel f) { f.AutoSize = false; f.WrapContents = true; }
        _table.Controls.Add(control, 0, row);
        _table.SetColumnSpan(control, 2);
        return control;
    }
}

/// <summary>Simple modal text input.</summary>
internal sealed class InputDialog : Form
{
    private readonly TextBox _box;

    private InputDialog(string title, string prompt, string value)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(520, 130);
        var label = new Label { Text = prompt, Left = 12, Top = 12, Width = 496, AutoSize = false, Height = 22 };
        _box = new TextBox { Text = value, Left = 12, Top = 40, Width = 496 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 332, Top = 82, Width = 84, Height = 30 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 424, Top = 82, Width = 84, Height = 30 };
        Controls.AddRange([label, _box, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public static string? Ask(IWin32Window owner, string title, string prompt, string value = "")
    {
        using var d = new InputDialog(title, prompt, value);
        return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text.Trim() : null;
    }
}
