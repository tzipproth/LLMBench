using LLMBench.Core;
using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

namespace LLMBench.Gui;

/// <summary>Grid of all items of a run plus a detail pane (prompt, response, reasoning, raw request).</summary>
internal sealed class RunResultsView : UserControl
{
    private readonly Label _headline = new() { Dock = DockStyle.Top, Height = Ui.S(30), Font = Ui.Big, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
    private readonly Label _liveLine = new() { Dock = DockStyle.Top, Height = Ui.S(24), Font = Ui.Bold, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
    private readonly ComboBox _filter = Ui.Combo("all", "not correct", "correct", "incorrect", "invalid", "truncated", "error", "skipped", "pending");
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
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None,
    };
    private readonly Label _detailInfo = new() { Dock = DockStyle.Top, AutoSize = false, Height = Ui.S(48), Font = Ui.Bold, Padding = new Padding(4) };
    private readonly TextBox _prompt = Ui.MonoBox();
    private readonly TextBox _response = Ui.MonoBox();
    private readonly TextBox _reasoning = Ui.MonoBox();
    private readonly TextBox _raw = Ui.MonoBox(wrap: false);
    private readonly TabControl _detailTabs = new() { Dock = DockStyle.Fill };

    private RunViewData? _data;
    private readonly Dictionary<int, DataGridViewRow> _rowsByIndex = [];

    public RunResultsView()
    {
        _grid.Columns.Add(Col("idx", "#", 50));
        _grid.Columns.Add(Col("q", "Question", 110));
        _grid.Columns.Add(Col("v", "Variant", 110));
        _grid.Columns.Add(Col("rep", "Rep", 40));
        _grid.Columns.Add(Col("exp", "Expected", 220));
        _grid.Columns.Add(Col("ans", "Answer", 160));
        _grid.Columns.Add(Col("status", "Status", 90));
        _grid.Columns.Add(Col("reason", "Reason", 100));
        _grid.Columns.Add(Col("tok", "Tokens out", 80));
        _grid.Columns.Add(Col("time", "Time (s)", 70));
        _grid.Columns.Add(Col("finish", "Finish", 70));
        _grid.Columns["exp"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns["ans"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.DefaultCellStyle.Font = new Font("Segoe UI", 9.75f);
        _grid.RowTemplate.Height = Ui.S(26);
        _grid.SelectionChanged += (_, _) => ShowSelected();
        _filter.Width = Ui.S(140);
        _filter.SelectedIndexChanged += (_, _) => ApplyFilter();

        var filterBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(2) };
        filterBar.Controls.Add(Ui.Label("Show:"));
        filterBar.Controls.Add(_filter);

        _detailTabs.TabPages.Add(Tab("Response", _response));
        _detailTabs.TabPages.Add(Tab("Reasoning", _reasoning));
        _detailTabs.TabPages.Add(Tab("Request / raw", _raw));

        var promptPanel = new Panel { Dock = DockStyle.Fill };
        promptPanel.Controls.Add(_prompt);
        promptPanel.Controls.Add(new Label { Text = "Prompt sent", Dock = DockStyle.Top, Height = Ui.S(22), Font = Ui.Bold });

        var detailSplit = Ui.Split(Orientation.Vertical, 700);
        detailSplit.Panel1.Controls.Add(promptPanel);
        detailSplit.Panel2.Controls.Add(_detailTabs);

        var detail = new Panel { Dock = DockStyle.Fill };
        detail.Controls.Add(detailSplit);
        detail.Controls.Add(_detailInfo);

        var gridPanel = new Panel { Dock = DockStyle.Fill };
        gridPanel.Controls.Add(_grid);
        gridPanel.Controls.Add(filterBar);

        var main = Ui.Split(Orientation.Horizontal, 380);
        main.Panel1.Controls.Add(gridPanel);
        main.Panel2.Controls.Add(detail);

        Controls.Add(main);
        Controls.Add(_liveLine);
        Controls.Add(_headline);
    }

    private static DataGridViewTextBoxColumn Col(string name, string header, int width) =>
        new() { Name = name, HeaderText = header, Width = Ui.S(width), SortMode = DataGridViewColumnSortMode.NotSortable };

    private static TabPage Tab(string title, Control c)
    {
        var p = new TabPage(title);
        p.Controls.Add(c);
        return p;
    }

    public RunViewData? Data => _data;

    public void Show(RunViewData? data)
    {
        _data = data;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        _rowsByIndex.Clear();
        ClearDetail();
        if (data == null)
        {
            _headline.Text = "";
            _liveLine.Text = "";
            _grid.ResumeLayout();
            return;
        }
        _headline.Text = data.IsPlanOnly
            ? $"{data.Name} — plan preview ({data.Rows.Count} requests, not started)"
            : $"{data.Name} — {TextReport.HeaderLine(data.Header, data.Definition)}";
        foreach (var r in data.Rows)
        {
            int i = _grid.Rows.Add();
            var row = _grid.Rows[i];
            row.Tag = r;
            _rowsByIndex[r.Index] = row;
            FillRow(row, r);
        }
        _grid.ResumeLayout();
        ApplyFilter();
        UpdateLiveLine();
        if (_grid.Rows.Count > 0 && !data.IsLive) _grid.Rows[0].Selected = true;
    }

    /// <summary>Called by the runs page when a live item changed.</summary>
    public void Refresh(int index, bool follow)
    {
        if (_data == null || !_rowsByIndex.TryGetValue(index, out var row)) return;
        FillRow(row, (RowData)row.Tag!);
        ApplyFilterToRow(row);
        if (follow && row.Visible)
        {
            _grid.ClearSelection();
            row.Selected = true;
            try { _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, row.Index - 5); } catch (InvalidOperationException) { }
        }
        else if (row.Selected) ShowSelected();
        UpdateLiveLine();
    }

    public void UpdateLiveLine()
    {
        if (_data == null) { _liveLine.Text = ""; return; }
        var s = _data.CurrentSummary();
        if (_data.IsPlanOnly) { _liveLine.Text = "Start the run to send the requests."; return; }
        var ci = s.CiLow.HasValue ? $"  CI {s.CiLow * 100:0}–{s.CiHigh * 100:0} %" : "";
        var running = _data.IsLive && _data.CurrentIndex.HasValue ? $"   ▶ running #{_data.CurrentIndex + 1} ({(DateTime.Now - _data.CurrentStarted).TotalSeconds:0} s)" : "";
        _liveLine.Text = $"{s.Done}/{_data.Rows.Count} done — correct {s.Correct}  incorrect {s.Incorrect}  invalid {s.Invalid}  truncated {s.Truncated}  error {s.Error}  skipped {s.Skipped}" +
                         $"   accuracy {TextReport.Pct(s.Accuracy)}{ci}   chance {TextReport.Pct(s.ChanceLevel)}{running}";
    }

    private void FillRow(DataGridViewRow row, RowData r)
    {
        var rec = r.Record;
        bool running = _data?.IsLive == true && _data.CurrentIndex == r.Index && rec == null;
        row.Cells["idx"].Value = r.Index + 1;
        row.Cells["q"].Value = r.QuestionId;
        row.Cells["v"].Value = r.VariantId;
        row.Cells["rep"].Value = r.Repetition;
        row.Cells["exp"].Value = r.Expected;
        row.Cells["ans"].Value = rec?.Status == ItemStatus.Error ? rec.Error : rec?.ExtractedAnswer;
        row.Cells["status"].Value = rec != null ? rec.Status.ToString().ToLowerInvariant() : running ? "running…" : "pending";
        row.Cells["reason"].Value = rec?.Reason;
        row.Cells["tok"].Value = rec?.CompletionTokens;
        row.Cells["time"].Value = rec != null ? rec.ElapsedSeconds.ToString("0.0") : null;
        row.Cells["finish"].Value = rec?.FinishReason;
        var color = rec?.Status switch
        {
            ItemStatus.Correct => Ui.Correct,
            ItemStatus.Incorrect => Ui.Incorrect,
            ItemStatus.Invalid or ItemStatus.Truncated => Ui.Invalid,
            ItemStatus.Error => Ui.Error,
            ItemStatus.Skipped => Ui.Skipped,
            null when running => Ui.Running,
            _ => SystemColors.Window,
        };
        row.Cells["status"].Style.BackColor = color;
        row.Cells["ans"].Style.BackColor = rec == null ? SystemColors.Window : color;
    }

    private void ApplyFilter()
    {
        _grid.CurrentCell = null;
        foreach (DataGridViewRow row in _grid.Rows) ApplyFilterToRow(row);
    }

    private void ApplyFilterToRow(DataGridViewRow row)
    {
        var r = (RowData)row.Tag!;
        var f = _filter.SelectedItem as string ?? "all";
        var st = r.Record?.Status.ToString().ToLowerInvariant();
        bool visible = f switch
        {
            "all" => true,
            "pending" => r.Record == null,
            "not correct" => r.Record != null && r.Record.Status != ItemStatus.Correct,
            _ => st == f,
        };
        if (row.Visible != visible)
        {
            if (!visible && row.Selected) row.Selected = false;
            if (_grid.CurrentRow == row && !visible) _grid.CurrentCell = null;
            row.Visible = visible;
        }
    }

    private void ClearDetail()
    {
        _detailInfo.Text = "";
        _prompt.Text = _response.Text = _reasoning.Text = _raw.Text = "";
    }

    private void ShowSelected()
    {
        if (_grid.SelectedRows.Count == 0 || _grid.SelectedRows[0].Tag is not RowData r) { ClearDetail(); return; }
        var rec = r.Record;
        _prompt.Text = Ui.Crlf((r.SystemMessage != null ? $"[system]\n{r.SystemMessage}\n\n" : "") + $"[user]\n{r.UserMessage}");
        if (rec == null)
        {
            _detailInfo.Text = $"#{r.Index + 1} {r.QuestionId}/{r.VariantId} rep {r.Repetition} — expected: {r.Expected} — not answered yet";
            _response.Text = _reasoning.Text = _raw.Text = "";
            return;
        }
        _detailInfo.Text =
            $"#{r.Index + 1} {r.QuestionId}/{r.VariantId} rep {r.Repetition} seed {rec.Seed?.ToString() ?? "-"} — {rec.Status.ToString().ToUpperInvariant()}{(rec.Reason != null ? $" ({rec.Reason})" : "")}" +
            $" — answer: {rec.ExtractedAnswer ?? "-"} — expected: {rec.Expected}\n" +
            $"finish: {rec.FinishReason ?? "-"} — tokens {rec.PromptTokens?.ToString() ?? "-"} in / {rec.CompletionTokens?.ToString() ?? "-"} out — {rec.ElapsedSeconds:0.0} s" +
            (rec.ThinkStripped ? " — <think> block removed before extraction" : "") +
            (rec.Error != null ? $" — error: {rec.Error}" : "");
        _response.Text = Ui.Crlf(rec.Content);
        _reasoning.Text = Ui.Crlf(string.IsNullOrEmpty(rec.ReasoningContent) ? "(no reasoning_content)" : rec.ReasoningContent);
        _raw.Text = Ui.Crlf(
            "Request:\n" + Json.Pretty(rec.Request) +
            "\n\nUsage:\n" + Json.Pretty(rec.Usage) +
            "\n\nTimings:\n" + Json.Pretty(rec.Timings));
        _detailTabs.TabPages[1].Text = string.IsNullOrEmpty(rec.ReasoningContent) ? "Reasoning" : "Reasoning ●";
    }
}
