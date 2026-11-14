using LLMBench.Core;

namespace LLMBench.Gui;

/// <summary>View and edit the system prompt files in prompts/.</summary>
internal sealed class PromptsPage : UserControl
{
    private readonly Workspace _ws;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, Font = new Font("Segoe UI", 10.5f) };
    private readonly TextBox _editor = Ui.MonoBox(readOnly: false);
    private readonly Label _info = new() { Dock = DockStyle.Top, Height = Ui.S(26), Padding = new Padding(4, 4, 0, 0) };
    private string? _file;
    private bool _dirty;
    private bool _loading;

    public PromptsPage(Workspace ws)
    {
        _ws = ws;
        _editor.Font = new Font("Consolas", 11f);
        _list.SelectedIndexChanged += (_, _) => Open();
        _editor.TextChanged += (_, _) => { if (!_loading) { _dirty = true; UpdateInfo(); } };

        var buttons = Ui.ButtonBar(
            Ui.Button("Save", (_, _) => Save()),
            Ui.Button("New…", (_, _) => New()),
            Ui.Button("Reload", (_, _) => Reload()));

        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_list);
        left.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom, Height = Ui.S(60), ForeColor = Color.DimGray, Padding = new Padding(4),
            Text = "Runs store the prompt text and its SHA-256 in the run header, so editing a prompt does not change existing results.",
        });

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_editor);
        right.Controls.Add(_info);
        right.Controls.Add(buttons);

        var split = Ui.Split(Orientation.Vertical, 360);
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);
        Reload();
    }

    private void Reload()
    {
        var sel = _file;
        _list.Items.Clear();
        _list.Items.AddRange([.. _ws.ListPrompts()]);
        _dirty = false;
        _file = null;
        if (sel != null && _list.Items.Contains(sel)) _list.SelectedItem = sel;
        else if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void Open()
    {
        if (_list.SelectedItem is not string f || f == _file) return;
        if (_dirty && _file != null)
        {
            var r = MessageBox.Show(this, $"Save changes to {_file}?", "LLMBench", MessageBoxButtons.YesNoCancel);
            if (r == DialogResult.Cancel) { _list.SelectedItem = _file; return; }
            if (r == DialogResult.Yes) Save();
        }
        _file = f;
        _loading = true;
        _editor.Text = Ui.Crlf(File.ReadAllText(_ws.Resolve(f)));
        _loading = false;
        _dirty = false;
        UpdateInfo();
    }

    private void Save()
    {
        if (_file == null) return;
        var text = _editor.Text.Replace("\r\n", "\n").TrimEnd() + "\n";
        File.WriteAllText(_ws.Resolve(_file), text);
        _dirty = false;
        UpdateInfo();
    }

    private void New()
    {
        var name = InputDialog.Ask(this, "New system prompt", "File name (without .txt):", "my-prompt");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Ui.ShowError(this, "Invalid file name."); return; }
        var path = Path.Combine(_ws.PromptsDir, name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? name : name + ".txt");
        if (File.Exists(path)) { Ui.ShowError(this, "File exists already."); return; }
        File.WriteAllText(path, "\n");
        _file = _ws.MakeRelative(path);
        Reload();
    }

    private void UpdateInfo()
    {
        var text = _editor.Text.Replace("\r\n", "\n").Trim();
        _info.Text = _file == null ? "" : $"{_file}{(_dirty ? "  (modified)" : "")}   —   {text.Length} characters, sha256 {Json.Sha256(text)[..12]}";
    }
}
