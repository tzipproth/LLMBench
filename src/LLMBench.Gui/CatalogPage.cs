using System.Diagnostics;
using System.Text;
using LLMBench.Core;
using LLMBench.Core.Catalog;
using LLMBench.Core.Prompting;

namespace LLMBench.Gui;

/// <summary>Browse catalogs: questions, variants, options, solutions, images and the composed prompt.</summary>
internal sealed class CatalogPage : UserControl
{
    private const string NoPrompt = "(none)";
    private readonly Workspace _ws;
    private readonly ComboBox _catalog = Ui.Combo();
    private readonly TextBox _search = new() { Width = Ui.S(260), PlaceholderText = "search id / text / category" };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, Font = new Font("Segoe UI", 10f), FullRowSelect = true };
    private readonly Label _info = new() { Dock = DockStyle.Top, Height = Ui.S(26), Padding = new Padding(4, 4, 0, 0) };
    private readonly TextBox _validation = Ui.MonoBox();
    private readonly TextBox _details = Ui.MonoBox();
    private readonly PictureBox _image = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly ComboBox _instruction = Ui.Combo(AnswerInstructions.All);
    private readonly ComboBox _systemPrompt = Ui.Combo();
    private readonly CheckBox _shuffle = new() { Text = "shuffle (seed 1, rep 0)", AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
    private readonly TextBox _preview = Ui.MonoBox();
    private readonly SplitContainer _detailSplit;
    private QuestionCatalog? _current;

    public CatalogPage(Workspace ws)
    {
        _ws = ws;
        _catalog.Width = Ui.S(320);
        _catalog.SelectedIndexChanged += (_, _) => LoadCatalog();
        _search.TextChanged += (_, _) => FillTree();
        _tree.AfterSelect += (_, _) => ShowSelection();

        var top = Ui.ButtonBar(Ui.Label("Catalog:"), _catalog, _search,
            Ui.Button("Reload", (_, _) => { RefreshList(); LoadCatalog(); }),
            Ui.Button("Open file", (_, _) => OpenFile()),
            Ui.Button("Write overview…", (_, _) => WriteOverview()));

        var leftSplit = Ui.Split(Orientation.Horizontal, 760);
        leftSplit.Panel1.Controls.Add(_tree);
        leftSplit.Panel1.Controls.Add(_info);
        var valPanel = new Panel { Dock = DockStyle.Fill };
        valPanel.Controls.Add(_validation);
        valPanel.Controls.Add(new Label { Text = "Validation", Dock = DockStyle.Top, Height = Ui.S(22), Font = Ui.Bold });
        leftSplit.Panel2.Controls.Add(valPanel);

        // right: details (+ image) above, prompt preview below
        _detailSplit = Ui.Split(Orientation.Vertical, 900);
        _detailSplit.Panel1.Controls.Add(_details);
        _detailSplit.Panel2.Controls.Add(_image);
        _detailSplit.Panel2Collapsed = true;

        var previewBar = Ui.ButtonBar(Ui.Label("Prompt preview — system prompt:"), _systemPrompt, Ui.Label("answer instruction:"), _instruction, _shuffle);
        _systemPrompt.Width = Ui.S(260);
        _instruction.Width = Ui.S(120);
        _systemPrompt.SelectedIndexChanged += (_, _) => ShowSelection();
        _instruction.SelectedIndexChanged += (_, _) => ShowSelection();
        _shuffle.CheckedChanged += (_, _) => ShowSelection();
        var previewPanel = new Panel { Dock = DockStyle.Fill };
        previewPanel.Controls.Add(_preview);
        previewPanel.Controls.Add(previewBar);

        var rightSplit = Ui.Split(Orientation.Horizontal, 420);
        rightSplit.Panel1.Controls.Add(_detailSplit);
        rightSplit.Panel2.Controls.Add(previewPanel);

        var main = Ui.Split(Orientation.Vertical, 520);
        main.Panel1.Controls.Add(leftSplit);
        main.Panel2.Controls.Add(rightSplit);

        Controls.Add(main);
        Controls.Add(top);

        RefreshList();
        if (_catalog.Items.Count > 0) _catalog.SelectedIndex = 0;
    }

    private void RefreshList()
    {
        var sel = _catalog.SelectedItem as string;
        _catalog.Items.Clear();
        _catalog.Items.AddRange([.. _ws.ListCatalogs()]);
        if (sel != null && _catalog.Items.Contains(sel)) _catalog.SelectedItem = sel;
        var sp = _systemPrompt.SelectedItem as string;
        _systemPrompt.Items.Clear();
        _systemPrompt.Items.Add(NoPrompt);
        _systemPrompt.Items.AddRange([.. _ws.ListPrompts()]);
        _systemPrompt.SelectedItem = sp != null && _systemPrompt.Items.Contains(sp) ? sp : "prompts/neutral.txt";
        if (_systemPrompt.SelectedIndex < 0) _systemPrompt.SelectedIndex = 0;
    }

    private void LoadCatalog()
    {
        if (_catalog.SelectedItem is not string file) return;
        try
        {
            _current = CatalogLoader.Load(_ws, file);
            _info.Text = $"{_current.Questions.Count} questions, {_current.VariantCount} variants, categories: {string.Join(", ", _current.Categories)}";
            var sb = new StringBuilder();
            sb.AppendLine(_current.IsValid ? $"OK — sha256 {_current.Sha256}" : $"{_current.Errors.Count} ERROR(S):");
            foreach (var e in _current.Errors) sb.AppendLine("error:   " + e);
            foreach (var w in _current.Warnings) sb.AppendLine("warning: " + w);
            _validation.Text = Ui.Crlf(sb.ToString());
            _validation.ForeColor = _current.IsValid ? Color.DarkGreen : Color.Red;
        }
        catch (BenchException ex)
        {
            _current = null;
            _validation.Text = ex.Message;
        }
        FillTree();
    }

    private void FillTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        if (_current != null)
        {
            var term = _search.Text.Trim();
            foreach (var q in _current.Questions)
            {
                if (term.Length > 0 && !Matches(q, term)) continue;
                var node = new TreeNode($"{q.Id}   [{q.Category ?? "-"}]{(q.Tier.HasValue ? $"  tier {q.Tier}" : "")}") { Tag = q };
                foreach (var v in q.Variants)
                {
                    var format = v.IsChoice ? $"{v.Options.Count} options" : $"free {v.AnswerType ?? "text"}";
                    var tier = v.Tier != q.Tier ? $", tier {v.Tier}" : "";
                    node.Nodes.Add(new TreeNode($"{v.Id}  ({v.Kind}, {format}{tier}){(v.Image != null ? "  🖼" : "")}") { Tag = v });
                }
                _tree.Nodes.Add(node);
            }
            _tree.ExpandAll();
        }
        _tree.EndUpdate();
        if (_tree.Nodes.Count > 0) _tree.SelectedNode = _tree.Nodes[0].Nodes.Count > 0 ? _tree.Nodes[0].Nodes[0] : _tree.Nodes[0];
        else { _details.Text = ""; _preview.Text = ""; }
    }

    private static bool Matches(CatalogQuestion q, string term) =>
        q.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (q.Category?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || q.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase))
        || q.Variants.Any(v => v.Question.Contains(term, StringComparison.OrdinalIgnoreCase) || v.Options.Any(o => o.Text.Contains(term, StringComparison.OrdinalIgnoreCase)));

    private void ShowSelection()
    {
        var node = _tree.SelectedNode;
        if (node == null) return;
        var q = node.Tag as CatalogQuestion ?? node.Parent?.Tag as CatalogQuestion;
        if (q == null) return;
        var variant = node.Tag as QuestionVariant ?? q.Variants.FirstOrDefault();

        var sb = new StringBuilder();
        sb.AppendLine($"{q.Id}    category: {q.Category ?? "-"}    tier: {q.Tier?.ToString() ?? "-"}    source: {q.Source ?? "-"}{(q.SourceId != null ? " #" + q.SourceId : "")}");
        if (q.Tags.Count > 0) sb.AppendLine("tags: " + string.Join(", ", q.Tags));
        if (q.Note != null) sb.AppendLine("note: " + q.Note);
        sb.AppendLine();
        sb.AppendLine("══ BASE");
        sb.AppendLine();
        sb.AppendLine(q.Base.Question);
        sb.AppendLine();
        if (q.Base.Options.Count > 0)
            foreach (var o in q.Base.Options) sb.AppendLine($"  {(string.Equals(o, q.Base.Answer, StringComparison.OrdinalIgnoreCase) ? "✔" : " ")} {o}");
        else sb.AppendLine($"  answer: {q.Base.Answer}{(q.Base.AnswerType != null ? $" ({q.Base.AnswerType})" : "")}");
        if (q.Base.AcceptedAnswers.Count > 0) sb.AppendLine("  also accepted: " + string.Join(" | ", q.Base.AcceptedAnswers));
        if (q.Base.FixedOrder) sb.AppendLine("  (fixed option order)");
        if (q.Base.Note != null) sb.AppendLine("  note: " + q.Base.Note);
        sb.AppendLine();
        sb.AppendLine($"══ VARIANTS ASKED ({q.Variants.Count})");
        foreach (var v in q.Variants)
        {
            sb.AppendLine();
            sb.AppendLine($"{(v == variant ? "▶ " : "  ")}── '{v.Id}' — {v.Kind}, {(v.IsChoice ? $"multiple choice, {v.Options.Count} options" : $"free answer ({v.AnswerType ?? "text"})")}, tier {v.Tier?.ToString() ?? "-"}{(v.FixedOrder && v.IsChoice ? ", fixed order" : "")}{(v.Image != null ? $", image {v.Image}" : "")}");
            sb.AppendLine();
            sb.AppendLine(v.Question);
            if (v.IsChoice)
            {
                sb.AppendLine();
                for (int i = 0; i < v.Options.Count; i++)
                {
                    var o = v.Options[i];
                    var mark = o.Id == v.CorrectOptionId ? "✔" : " ";
                    sb.AppendLine($"  {mark} {PromptBuilder.Letter(i)}. {o.Text}    [{o.Id}]");
                }
            }
            else sb.AppendLine("\n  accepted: " + string.Join("  |  ", v.AcceptedAnswers));
            if (v.Note != null) sb.AppendLine("  note: " + v.Note);
        }
        _details.Text = Ui.Crlf(sb.ToString());

        ShowImage(variant?.Image);
        ShowPreview(q, variant);
    }

    private void ShowImage(string? path)
    {
        var old = _image.Image;
        _image.Image = null;
        old?.Dispose();
        if (path == null) { _detailSplit.Panel2Collapsed = true; return; }
        var full = _ws.Resolve(path);
        _detailSplit.Panel2Collapsed = false;
        if (!File.Exists(full)) return;
        using var fs = File.OpenRead(full);
        using var img = Image.FromStream(fs);
        _image.Image = new Bitmap(img);
    }

    private void ShowPreview(CatalogQuestion q, QuestionVariant? v)
    {
        if (v == null) { _preview.Text = ""; return; }
        try
        {
            string? system = null;
            if (_systemPrompt.SelectedItem is string sp && sp != NoPrompt && File.Exists(_ws.Resolve(sp)))
                system = File.ReadAllText(_ws.Resolve(sp)).Trim();
            var displayed = v.IsChoice ? PromptBuilder.ArrangeOptions(q.Id, v, _shuffle.Checked, 1, 0) : [];
            var user = PromptBuilder.BuildUserMessage(v, displayed, (string)_instruction.SelectedItem!);
            var (sys, usr) = PromptBuilder.ApplyPlacement(system, "system", user);
            var correct = displayed.FirstOrDefault(d => d.Id == v.CorrectOptionId);
            var expected = v.IsChoice ? $"{correct?.Letter}. {correct?.Text}" : string.Join(" | ", v.AcceptedAnswers);
            _preview.Text = Ui.Crlf((sys != null ? $"[system]\n{sys}\n\n" : "") + $"[user]\n{usr}\n\n— expected: {expected}");
        }
        catch (Exception ex) when (ex is BenchException or IOException) { _preview.Text = ex.Message; }
    }

    private void WriteOverview()
    {
        if (_current == null) return;
        var dir = Path.Combine(_ws.Root, "docs");
        Directory.CreateDirectory(dir);
        using var dlg = new SaveFileDialog
        {
            InitialDirectory = dir,
            FileName = Path.GetFileNameWithoutExtension(_current.FullPath) + "-overview.md",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, CatalogOverview.ToMarkdown(_current));
        try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); }
        catch (Exception ex) { Ui.ShowError(this, ex.Message); }
    }

    private void OpenFile()
    {
        if (_current == null) return;
        try { Process.Start(new ProcessStartInfo(_current.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { Ui.ShowError(this, ex.Message); }
    }
}
