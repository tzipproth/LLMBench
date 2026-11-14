using LLMBench.Core;
using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

namespace LLMBench.Gui;

/// <summary>Question-by-question comparison of two runs (concept 11.3).</summary>
internal sealed class ComparePage : UserControl
{
    private readonly Workspace _ws;
    private readonly ComboBox _a = Ui.Combo();
    private readonly ComboBox _b = Ui.Combo();
    private readonly CheckBox _onlyDiff = new() { Text = "only items with different outcome", AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
    private readonly TextBox _summary = Ui.MonoBox(wrap: false);
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None,
    };
    private readonly Label _infoA = new() { Dock = DockStyle.Top, Height = Ui.S(44), Font = Ui.Bold, Padding = new Padding(4) };
    private readonly Label _infoB = new() { Dock = DockStyle.Top, Height = Ui.S(44), Font = Ui.Bold, Padding = new Padding(4) };
    private readonly TextBox _respA = Ui.MonoBox();
    private readonly TextBox _respB = Ui.MonoBox();
    private CompareResult? _result;

    public ComparePage(Workspace ws)
    {
        _ws = ws;
        _a.Width = _b.Width = Ui.S(320);
        _onlyDiff.CheckedChanged += (_, _) => FillGrid();
        var bar = Ui.ButtonBar(Ui.Label("Run A:"), _a, Ui.Label("Run B:"), _b,
            Ui.Button("⇄ Swap", (_, _) => { var x = _a.SelectedItem; _a.SelectedItem = _b.SelectedItem; _b.SelectedItem = x; Run(); }),
            Ui.Button("Compare", (_, _) => Run()),
            _onlyDiff);

        Col("q", "Question", 110); Col("v", "Variant", 100); Col("rep", "Rep", 45); Col("exp", "Expected", 220);
        Col("ansA", "Answer A", 150); Col("stA", "Status A", 85); Col("ansB", "Answer B", 150); Col("stB", "Status B", 85); Col("kind", "Outcome", 100);
        _grid.Columns["exp"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.DefaultCellStyle.Font = new Font("Segoe UI", 9.75f);
        _grid.RowTemplate.Height = Ui.S(26);
        _grid.SelectionChanged += (_, _) => ShowDetail();

        var panelA = new Panel { Dock = DockStyle.Fill };
        panelA.Controls.Add(_respA);
        panelA.Controls.Add(_infoA);
        var panelB = new Panel { Dock = DockStyle.Fill };
        panelB.Controls.Add(_respB);
        panelB.Controls.Add(_infoB);
        var detail = Ui.Split(Orientation.Vertical, 900);
        detail.Panel1.Controls.Add(panelA);
        detail.Panel2.Controls.Add(panelB);

        var lower = Ui.Split(Orientation.Horizontal, 330);
        lower.Panel1.Controls.Add(_grid);
        lower.Panel2.Controls.Add(detail);

        var main = Ui.Split(Orientation.Horizontal, 230);
        main.Panel1.Controls.Add(_summary);
        main.Panel2.Controls.Add(lower);

        Controls.Add(main);
        Controls.Add(bar);
    }

    private void Col(string name, string header, int width) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = Ui.S(width), SortMode = DataGridViewColumnSortMode.Automatic });

    public void RefreshRuns()
    {
        var sa = _a.SelectedItem as string;
        var sb = _b.SelectedItem as string;
        var names = _ws.Runs.ListNames().Where(_ws.Runs.HasResults).ToArray();
        _a.Items.Clear(); _a.Items.AddRange(names);
        _b.Items.Clear(); _b.Items.AddRange(names);
        if (sa != null && names.Contains(sa)) _a.SelectedItem = sa;
        if (sb != null && names.Contains(sb)) _b.SelectedItem = sb;
    }

    public void CompareRuns(string a, string b)
    {
        RefreshRuns();
        _a.SelectedItem = a;
        _b.SelectedItem = b;
        Run();
    }

    private void Run()
    {
        if (_a.SelectedItem is not string a || _b.SelectedItem is not string b) { Ui.ShowError(this, "Select two runs."); return; }
        try
        {
            _result = RunComparer.Compare(_ws, a, RunResults.Load(_ws.Runs.ResultsPath(a)), b, RunResults.Load(_ws.Runs.ResultsPath(b)));
            _summary.Text = Ui.Crlf(RunComparer.Format(_result, listDifferingItems: false));
            FillGrid();
        }
        catch (Exception ex) when (ex is BenchException or IOException) { Ui.ShowError(this, ex.Message); }
    }

    private void FillGrid()
    {
        _grid.Rows.Clear();
        if (_result == null) return;
        foreach (var r in _result.Rows.Where(r => !_onlyDiff.Checked || r.Kind is CompareKind.OnlyA or CompareKind.OnlyB))
        {
            int i = _grid.Rows.Add(r.QuestionId, r.VariantId, r.Repetition, r.Expected,
                Answer(r.A) ?? "", r.A?.Status.ToString().ToLowerInvariant() ?? "-",
                Answer(r.B) ?? "", r.B?.Status.ToString().ToLowerInvariant() ?? "-",
                KindText(r.Kind));
            var row = _grid.Rows[i];
            row.Tag = r;
            row.Cells["stA"].Style.BackColor = StatusColor(r.A);
            row.Cells["stB"].Style.BackColor = StatusColor(r.B);
            row.Cells["kind"].Style.BackColor = r.Kind switch
            {
                CompareKind.OnlyA or CompareKind.OnlyB => Ui.Invalid,
                CompareKind.BothCorrect => Ui.Correct,
                CompareKind.BothWrong => Ui.Incorrect,
                _ => Ui.Skipped,
            };
        }
        if (_grid.Rows.Count > 0) { _grid.ClearSelection(); _grid.Rows[0].Selected = true; ShowDetail(); }
        else { _respA.Text = _respB.Text = _infoA.Text = _infoB.Text = ""; }
    }

    private static string? Answer(ItemRecord? r) => r?.Status == ItemStatus.Error ? r.Error : r?.ExtractedAnswer;

    private static string KindText(CompareKind k) => k switch
    {
        CompareKind.BothCorrect => "both correct",
        CompareKind.OnlyA => "only A",
        CompareKind.OnlyB => "only B",
        CompareKind.BothWrong => "both wrong",
        CompareKind.OnlyInA => "only in A",
        CompareKind.OnlyInB => "only in B",
        _ => "excluded",
    };

    private static Color StatusColor(ItemRecord? r) => r?.Status switch
    {
        ItemStatus.Correct => Ui.Correct,
        ItemStatus.Incorrect => Ui.Incorrect,
        ItemStatus.Invalid or ItemStatus.Truncated => Ui.Invalid,
        ItemStatus.Error => Ui.Error,
        _ => Ui.Skipped,
    };

    private void ShowDetail()
    {
        if (_grid.SelectedRows.Count == 0 || _grid.SelectedRows[0].Tag is not CompareRow r) return;
        Show(_infoA, _respA, "A", _result!.NameA, r.A);
        Show(_infoB, _respB, "B", _result.NameB, r.B);
    }

    private static void Show(Label info, TextBox box, string side, string run, ItemRecord? rec)
    {
        if (rec == null) { info.Text = $"{side}: {run} — item not present"; box.Text = ""; return; }
        info.Text = $"{side}: {run} — {rec.Status.ToString().ToUpperInvariant()}{(rec.Reason != null ? $" ({rec.Reason})" : "")}, answer {rec.ExtractedAnswer ?? "-"}\n" +
                    $"{rec.CompletionTokens?.ToString() ?? "-"} tokens, {rec.ElapsedSeconds:0.0} s, finish {rec.FinishReason ?? "-"}";
        box.Text = Ui.Crlf(
            (rec.SystemMessage != null ? $"[system]\n{rec.SystemMessage}\n\n" : "") + $"[user]\n{rec.UserMessage}\n\n" +
            (string.IsNullOrEmpty(rec.ReasoningContent) ? "" : $"[reasoning]\n{rec.ReasoningContent}\n\n") +
            $"[response]\n{rec.Content ?? rec.Error}");
    }
}
