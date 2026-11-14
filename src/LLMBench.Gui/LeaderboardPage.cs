using LLMBench.Core;
using LLMBench.Core.Runs;
using LLMBench.Core.Stats;

namespace LLMBench.Gui;

/// <summary>
/// Ranking of all runs that ask exactly the same questions (same question set), including several runs of
/// one model with different parameters or system prompts.
/// </summary>
internal sealed class LeaderboardPage : UserControl
{
    private const string AllModels = "(all models)";
    private readonly Workspace _ws;
    private readonly Action<string> _openRun;
    private readonly Action<string, string> _compare;

    private readonly ComboBox _group = Ui.Combo();
    private readonly ComboBox _model = Ui.Combo(AllModels);
    private readonly CheckBox _includeIncomplete = new() { Text = "include incomplete/aborted runs", AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
    private readonly TextBox _nameFilter = new() { Width = Ui.S(120), PlaceholderText = "e.g. std-" };
    private readonly CheckBox _onlyUsable = new() { Text = "only usable runs", AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
    private readonly Label _varying = new() { Dock = DockStyle.Top, AutoSize = false, Height = Ui.S(48), Padding = new Padding(4), Font = Ui.Bold };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None,
    };
    private readonly TextBox _detail = Ui.MonoBox(wrap: false);
    private List<LeaderboardGroup> _groups = [];
    private List<LeaderboardEntry> _shown = [];
    private readonly ComboBox _qFilter = Ui.Combo("all questions", "separates", "all solve", "none solves");
    private readonly Label _qInfo = new() { AutoSize = true, Margin = new Padding(12, 9, 3, 3), Font = Ui.Bold };
    private readonly DataGridView _qGrid = new()
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

    // column name -> parameter key in ParameterDiff (to mark varying columns)
    private static readonly Dictionary<string, string> ParamColumns = new()
    {
        ["prompt"] = "systemPromptFile", ["placement"] = "systemPromptPlacement", ["instr"] = "answerInstruction",
        ["thinking"] = "thinking", ["temp"] = "temperature", ["topp"] = "topP", ["topk"] = "topK", ["minp"] = "minP",
        ["rep_pen"] = "repeatPenalty", ["seed"] = "seed", ["maxtok"] = "maxTokens", ["reps"] = "repetitions",
        ["shuffle"] = "shuffleOptions", ["modelfile"] = "server.modelPath", ["model"] = "model", ["extra"] = "extraRequest",
    };

    public LeaderboardPage(Workspace ws, Action<string> openRun, Action<string, string> compare)
    {
        _ws = ws;
        _openRun = openRun;
        _compare = compare;
        _group.Width = Ui.S(760);
        _model.Width = Ui.S(220);
        _group.SelectedIndexChanged += (_, _) => { FillModels(); FillGrid(); };
        _model.SelectedIndexChanged += (_, _) => FillGrid();
        _includeIncomplete.CheckedChanged += (_, _) => FillGrid();
        _onlyUsable.CheckedChanged += (_, _) => FillGrid();
        _nameFilter.TextChanged += (_, _) => FillGrid();

        var bar = Ui.ButtonBar(Ui.Label("Question set:"), _group, Ui.Label("Model:"), _model, Ui.Label("Run name contains:"), _nameFilter, _includeIncomplete, _onlyUsable,
            Ui.Button("Refresh", (_, _) => Reload()),
            Ui.Button("Compare selected (2)", (_, _) => CompareSelected()),
            Ui.Button("Open run", (_, _) => { if (SelectedNames().FirstOrDefault() is { } n) _openRun(n); }));

        AddCol("pos", "#", 40, typeof(int));
        AddCol("rank", "Rank", 55, typeof(int));
        AddCol("run", "Run", 180, typeof(string));
        AddCol("model", "Model", 130, typeof(string));
        AddCol("modelfile", "Model file", 200, typeof(string));
        AddCol("acc", "Accuracy", 80, typeof(double), "0.0 %");
        AddCol("ci", "95 % CI", 85, typeof(string));
        // ability on the calibrated scale (Rasch model, 'llmbench calibrate'); empty without a calibration
        AddCol("score", "Score", 65, typeof(double), "0");
        AddCol("score95", "Score ±", 60, typeof(string));
        AddCol("strength", "Strength ×", 70, typeof(double), "0.##");
        AddCol("strength95", "Strength 95 %", 90, typeof(string));
        AddCol("spa", "Ø s per answer", 70, typeof(double), "0.0");
        AddCol("net", "Net value", 70, typeof(double), "0.0 %");
        AddCol("netmin", "Net per min", 70, typeof(double), "0.0");
        AddCol("worse", "Significantly worse than", 200, typeof(string));
        AddCol("n", "Correct", 70, typeof(string));
        AddCol("chance", "Chance", 65, typeof(double), "0.0 %");
        AddCol("fail", "Inv+trunc", 75, typeof(double), "0.0 %");
        AddCol("tok", "Ø tokens per answer", 75, typeof(double), "0");
        AddCol("tps", "Tokens per s", 70, typeof(double), "0.0");
        AddCol("time", "Model time (min)", 80, typeof(double), "0.0");
        // practical view, computed from the stored answers: accuracy if answers had to be short / quick
        AddCol("medtok", "Median tokens", 70, typeof(double), "0");
        AddCol("meds", "Median s", 65, typeof(double), "0.0");
        AddCol("tpc", "Tokens per correct", 75, typeof(double), "0");
        AddCol("pareto", "Pareto", 55, typeof(string));
        foreach (var l in BudgetStat.TokenLimits) AddCol("tok" + l, $"Acc ≤ {l} tok", 75, typeof(double), "0.0 %");
        foreach (var l in BudgetStat.SecondLimits) AddCol("sec" + l, $"Acc ≤ {l} s", 70, typeof(double), "0.0 %");
        AddCol("thinking", "Thinking", 85, typeof(string));
        AddCol("prompt", "System prompt", 150, typeof(string));
        AddCol("placement", "Placement", 75, typeof(string));
        AddCol("instr", "Instruction", 85, typeof(string));
        AddCol("temp", "Temp", 55, typeof(string));
        AddCol("topp", "top_p", 55, typeof(string));
        AddCol("topk", "top_k", 55, typeof(string));
        AddCol("minp", "min_p", 55, typeof(string));
        AddCol("rep_pen", "rep_pen", 60, typeof(string));
        AddCol("seed", "Seed", 55, typeof(string));
        AddCol("maxtok", "Max tok", 65, typeof(string));
        AddCol("reps", "Reps", 50, typeof(int));
        AddCol("shuffle", "Shuffle", 60, typeof(string));
        AddCol("extra", "Extra request", 140, typeof(string));
        AddCol("stable", "Stable", 70, typeof(string));
        AddCol("ctrl", "Recognizes only", 80, typeof(string));
        AddCol("date", "Started", 125, typeof(string));
        AddCol("note", "Note", 160, typeof(string));
        _grid.DefaultCellStyle.Font = new Font("Segoe UI", 9.75f);
        _grid.RowTemplate.Height = Ui.S(26);
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.SelectionChanged += (_, _) => ShowDetail();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is LeaderboardEntry le) _openRun(le.RunName); };

        var split = Ui.Split(Orientation.Horizontal, 520);
        var top = new Panel { Dock = DockStyle.Fill };
        top.Controls.Add(_grid);
        top.Controls.Add(_varying);
        split.Panel1.Controls.Add(top);
        split.Panel2.Controls.Add(_detail);

        // per-question solve rates (calibration, concept 3.2 rule 6)
        _qFilter.Width = Ui.S(160);
        _qFilter.SelectedIndexChanged += (_, _) => FillQuestions();
        _qGrid.DefaultCellStyle.Font = new Font("Segoe UI", 9.75f);
        _qGrid.RowTemplate.Height = Ui.S(24);
        _qGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _qGrid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        var qPanel = new Panel { Dock = DockStyle.Fill };
        qPanel.Controls.Add(_qGrid);
        qPanel.Controls.Add(Ui.ButtonBar(Ui.Label("Show:"), _qFilter, Ui.Button("Export CSV…", (_, _) => ExportCsv()), Ui.Button("Export for LLM (Markdown)…", (_, _) => ExportForLlm()), _qInfo));
        qPanel.Controls.Add(new Label
        {
            Dock = DockStyle.Top, Height = Ui.S(24), ForeColor = Color.DimGray, Padding = new Padding(4, 4, 0, 0),
            Text = "Solve rate of every question in the runs shown (same filters as the Runs view). Questions that all or none of the runs solve do not separate models: candidates for re-tiering, rewording or removal.",
        });

        var subTabs = new TabControl { Dock = DockStyle.Fill };
        var runsTab = new TabPage("Runs");
        runsTab.Controls.Add(split);
        var qTab = new TabPage("Questions");
        qTab.Controls.Add(qPanel);
        subTabs.TabPages.Add(runsTab);
        subTabs.TabPages.Add(qTab);

        Controls.Add(subTabs);
        Controls.Add(new Label
        {
            Dock = DockStyle.Top, Height = Ui.S(24), ForeColor = Color.DimGray, Padding = new Padding(4, 4, 0, 0),
            Text = "Only runs that asked exactly the same questions are ranked together. # = position by accuracy. Rank = 1 + number of runs that are significantly better in the paired test over the same questions (McNemar, p < 0.05); equal rank = difference not proven. Score = ability on a fixed scale (Rasch model, 'llmbench calibrate'; like Elo: +400 = 10 times the odds, hard questions count more). Strength = how many times the odds of solving a question compared with the reference run of the calibration (84 = 84 times the odds; 84 vs 35 = 2.4 times). Columns marked * differ between the shown runs. Double-click opens the run.",
        });
        Controls.Add(bar);
    }

    private void AddCol(string name, string header, int width, Type type, string? format = null)
    {
        var c = new DataGridViewTextBoxColumn
        {
            Name = name, HeaderText = header, Width = Ui.S(width), ValueType = type,
            SortMode = DataGridViewColumnSortMode.Automatic, Tag = header,
        };
        if (format != null) c.DefaultCellStyle.Format = format;
        if (type != typeof(string)) c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns.Add(c);
    }

    public void Reload()
    {
        var sel = (_group.SelectedItem as GroupItem)?.Group.QuestionSetHash;
        try { _groups = Leaderboard.Build(_ws); }
        catch (Exception ex) when (ex is BenchException or IOException) { Ui.ShowError(this, ex.Message); _groups = []; }
        _group.Items.Clear();
        foreach (var g in _groups) _group.Items.Add(new GroupItem(g));
        if (_group.Items.Count == 0) { _grid.Rows.Clear(); _varying.Text = "No runs with results yet."; return; }
        var idx = _groups.FindIndex(g => g.QuestionSetHash == sel);
        _group.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private sealed record GroupItem(LeaderboardGroup Group)
    {
        public override string ToString() => Group.Title;
    }

    private void FillModels()
    {
        var sel = _model.SelectedItem as string;
        _model.Items.Clear();
        _model.Items.Add(AllModels);
        if (_group.SelectedItem is GroupItem gi)
            foreach (var m in gi.Group.Entries.Select(e => e.Model).Distinct().OrderBy(m => m)) _model.Items.Add(m);
        _model.SelectedItem = sel != null && _model.Items.Contains(sel) ? sel : AllModels;
    }

    private void FillGrid()
    {
        _grid.Rows.Clear();
        if (_group.SelectedItem is not GroupItem gi) return;
        var model = _model.SelectedItem as string;
        var entries = gi.Group.Entries
            .Where(e => _includeIncomplete.Checked || e.State == RunState.Completed)
            .Where(e => !_onlyUsable.Checked || e.Usable)
            .Where(e => _nameFilter.Text.Trim().Length == 0 || e.RunName.Contains(_nameFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(e => model == null || model == AllModels || e.Model == model)
            .ToList();
        var sorted = Leaderboard.AssignRanks(entries);
        var varying = Leaderboard.VaryingParameters(entries);
        _varying.Text = entries.Count == 0
            ? "No runs match the filter."
            : $"{entries.Count} run(s), {gi.Group.QuestionCount} question/variant pairs.   Parameters that differ: " + (varying.Count == 0 ? "(none)" : string.Join(", ", varying));

        foreach (DataGridViewColumn c in _grid.Columns)
        {
            var header = (string)c.Tag!;
            c.HeaderText = ParamColumns.TryGetValue(c.Name, out var key) && varying.Contains(key) ? header + " *" : header;
            c.HeaderCell.Style.Font = c.HeaderText.EndsWith('*') ? Ui.Bold : null;
        }

        foreach (var e in sorted)
        {
            var s = e.Summary;
            var d = e.Definition;
            var b = s.Budget;
            var budget = new List<object>
            {
                b != null ? b.MedianTokens : DBNull.Value,
                b != null ? b.MedianSeconds : DBNull.Value,
                b?.TokensPerCorrect is { } tpc ? tpc : DBNull.Value,
                e.Pareto ? "yes" : "",
            };
            budget.AddRange(BudgetStat.TokenLimits.Select((_, k) => b != null ? (object)b.AccuracyAtTokens[k] : DBNull.Value));
            budget.AddRange(BudgetStat.SecondLimits.Select((_, k) => b != null ? (object)b.AccuracyWithinSeconds[k] : DBNull.Value));
            int i = _grid.Rows.Add([
                e.Position,
                e.Rank ?? (object)DBNull.Value,
                e.RunName,
                e.Model,
                e.ModelFile,
                s.Accuracy.HasValue ? s.Accuracy.Value : DBNull.Value,
                s.CiLow.HasValue ? $"{s.CiLow * 100:0}–{s.CiHigh * 100:0} %" : "",
                e.Ability != null ? e.Ability.Score : DBNull.Value,
                e.Ability != null ? $"±{e.Ability.Score95:0}" : "",
                e.Ability?.Strength is { } st ? Math.Round(st, st >= 9.95 ? 0 : st >= 0.995 ? 1 : 2) : DBNull.Value,
                e.Ability?.Strength != null ? $"{Rasch.FormatStrength(e.Ability.StrengthLow)}–{Rasch.FormatStrength(e.Ability.StrengthHigh)}" : "",
                e.SecondsPerAnswer.HasValue ? e.SecondsPerAnswer.Value : DBNull.Value,
                b != null ? b.NetScore : DBNull.Value,
                b?.NetPerMinute is { } npm ? npm : DBNull.Value,
                string.Join(", ", e.SignificantlyWorseThan),
                $"{s.Correct}/{s.Scored}",
                s.ChanceLevel.HasValue ? s.ChanceLevel.Value : DBNull.Value,
                e.FormatFailureRate.HasValue ? e.FormatFailureRate.Value : DBNull.Value,
                e.TokensPerAnswer.HasValue ? e.TokensPerAnswer.Value : DBNull.Value,
                e.TokensPerSecond.HasValue ? e.TokensPerSecond.Value : DBNull.Value,
                e.ModelSeconds / 60,
                .. budget,
                e.Thinking,
                e.SystemPrompt,
                d.SystemPromptPlacement,
                d.AnswerInstruction,
                Ui.Num(d.Temperature), Ui.Num(d.TopP), Ui.Num(d.TopK), Ui.Num(d.MinP), Ui.Num(d.RepeatPenalty), Ui.Num(d.Seed), Ui.Num(d.MaxTokens),
                d.Repetitions,
                d.ShuffleOptions ? "yes" : "no",
                d.ExtraRequest.ToJsonString(),
                s.Stability != null ? $"{s.Stability.SolvedInAll}/{s.Stability.QuestionVariants}" : "",
                s.ControlPairs != null ? $"{s.ControlPairs.RecognizesOnly.Count}/{s.ControlPairs.Pairs}" : "",
                e.Header.StartedAt.ToString("yyyy-MM-dd HH:mm"),
                e.UsabilityNote]);
            var row = _grid.Rows[i];
            row.Tag = e;
            if (!e.Usable) row.DefaultCellStyle.ForeColor = Color.Gray;
            row.Cells["acc"].Style.BackColor = e.Rank == 1 ? Ui.Correct : SystemColors.Window;
        }
        if (_grid.Rows.Count > 0) { _grid.ClearSelection(); _grid.Rows[0].Selected = true; }
        ShowDetail();
        _shown = sorted;
        FillQuestions();
    }

    private void FillQuestions()
    {
        _qGrid.Rows.Clear();
        _qGrid.Columns.Clear();
        var runs = _shown;
        Col(_qGrid, "q", "Question", 160);
        Col(_qGrid, "tier", "Tier", 45);
        Col(_qGrid, "cat", "Category", 120);
        Col(_qGrid, "solved", "Solved by", 75);
        Col(_qGrid, "mean", "Mean", 65, "0 %");
        Col(_qGrid, "cal", "Calibration", 95);
        for (int i = 0; i < runs.Count; i++) Col(_qGrid, "r" + i, runs[i].RunName, 95);
        Col(_qGrid, "exp", "Expected", 260);
        if (runs.Count == 0) { _qInfo.Text = ""; return; }

        var rows = QuestionStats.Build(runs);
        var f = _qFilter.SelectedItem as string ?? "all questions";
        foreach (var r in rows)
        {
            if (f != "all questions" && r.Calibration != f) continue;
            var values = new List<object?> { r.Key, r.Tier, r.Category, $"{r.SolvedBy}/{r.Runs}", r.MeanRate, r.Calibration };
            foreach (var e in runs)
                values.Add(r.RateByRun.TryGetValue(e.RunName, out var v) ? (v >= 1 ? "✔" : v <= 0 ? "✘" : $"{v * 100:0} %") : "-");
            values.Add(r.Expected);
            int idx = _qGrid.Rows.Add(values.ToArray()!);
            var row = _qGrid.Rows[idx];
            for (int i = 0; i < runs.Count; i++)
            {
                var cell = row.Cells["r" + i];
                if (r.RateByRun.TryGetValue(runs[i].RunName, out var v))
                    cell.Style.BackColor = v >= 1 ? Ui.Correct : v <= 0 ? Ui.Incorrect : Ui.Invalid;
                cell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }
            row.Cells["cal"].Style.BackColor = r.Calibration switch
            {
                "all solve" => Ui.Skipped,
                "none solves" => Ui.Incorrect,
                _ => SystemColors.Window,
            };
        }
        _qInfo.Text = $"{rows.Count} questions: {rows.Count(r => r.Calibration == "separates")} separate, " +
                      $"{rows.Count(r => r.Calibration == "all solve")} solved by all, {rows.Count(r => r.Calibration == "none solves")} solved by none";
    }

    private void ExportCsv()
    {
        if (_group.SelectedItem is not GroupItem gi || _shown.Count == 0) { Ui.ShowError(this, "No runs shown."); return; }
        var dir = Path.Combine(_ws.Root, "exports");
        Directory.CreateDirectory(dir);
        using var dlg = new SaveFileDialog
        {
            InitialDirectory = dir,
            FileName = $"results_{gi.Group.QuestionSetHash[..8]}_{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
            Title = $"Export {_shown.Count} runs (as filtered and sorted) as CSV + legend + briefing",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var catalog = ResultsCsv.MatchingCatalog(_ws, _shown);
            File.WriteAllText(dlg.FileName, ResultsCsv.Build(_shown, catalog), new System.Text.UTF8Encoding(false));
            var legend = Path.ChangeExtension(dlg.FileName, null) + "-legend.txt";
            File.WriteAllText(legend, ResultsCsv.BuildLegend(_shown, Path.GetFileName(dlg.FileName), catalog, true));
            var briefing = Path.ChangeExtension(dlg.FileName, null) + "-briefing.md";
            File.WriteAllText(briefing, LlmBriefing.Build(_shown, catalog, Path.GetFileName(dlg.FileName), Path.GetFileName(legend)));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{dlg.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is BenchException or IOException) { Ui.ShowError(this, ex.Message); }
    }

    private void ExportForLlm()
    {
        if (_group.SelectedItem is not GroupItem gi || _shown.Count == 0) { Ui.ShowError(this, "No runs shown."); return; }
        var dir = Path.Combine(_ws.Root, "exports");
        Directory.CreateDirectory(dir);
        using var dlg = new SaveFileDialog
        {
            InitialDirectory = dir,
            FileName = $"llm-export_{gi.Group.QuestionSetHash[..8]}_{DateTime.Now:yyyyMMdd-HHmm}.md",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
            Title = $"Export {_shown.Count} runs (as filtered) for an LLM",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, LlmExport.Build(_ws, gi.Group, _shown));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{dlg.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is BenchException or IOException) { Ui.ShowError(this, ex.Message); }
    }

    private static void Col(DataGridView g, string name, string header, int width, string? format = null)
    {
        var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = Ui.S(width), SortMode = DataGridViewColumnSortMode.Automatic };
        if (format != null) c.DefaultCellStyle.Format = format;
        g.Columns.Add(c);
    }

    private List<string> SelectedNames() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => ((LeaderboardEntry)r.Tag!).RunName).ToList();

    private void CompareSelected()
    {
        var names = SelectedNames();
        if (names.Count != 2) { Ui.ShowError(this, "Select exactly two runs (Ctrl+click)."); return; }
        _compare(names[0], names[1]);
    }

    private void ShowDetail()
    {
        if (_grid.SelectedRows.Count == 0 || _grid.SelectedRows[0].Tag is not LeaderboardEntry e) { _detail.Text = ""; return; }
        var text = TextReport.Format(e.Header, e.Definition, e.Summary);
        if (!string.IsNullOrWhiteSpace(e.Definition.ServerNotes)) text += "\nServer notes: " + e.Definition.ServerNotes;
        _detail.Text = Ui.Crlf(text);
    }
}
