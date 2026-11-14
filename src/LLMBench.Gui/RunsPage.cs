using System.Diagnostics;
using LLMBench.Core;
using LLMBench.Core.Runs;
using LLMBench.Core.Stats;
using Microsoft.VisualBasic.FileIO;

namespace LLMBench.Gui;

/// <summary>Run list, parameter editor, start/stop/restart and live results.</summary>
internal sealed class RunsPage : UserControl
{
    private readonly Workspace _ws;
    private readonly Action<string> _status;

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        GridLines = true,
    };
    private readonly RunParametersEditor _editor;
    private readonly RunResultsView _results = new() { Dock = DockStyle.Fill };
    private readonly TextBox _summary = Ui.MonoBox(wrap: false);
    private readonly TextBox _log = Ui.MonoBox();
    private readonly TextBox _header = Ui.MonoBox(wrap: false);
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Label _title = new() { AutoSize = true, Font = Ui.Big, Margin = new Padding(6, 8, 20, 3) };
    private readonly ProgressBar _progress = new() { Width = Ui.S(260), Height = Ui.S(24), Margin = new Padding(10, 6, 3, 3) };
    private readonly Label _progressText = new() { AutoSize = true, Margin = new Padding(6, 9, 3, 3) };
    private readonly Button _btnSave, _btnStart, _btnStop, _btnRestart, _btnRescore, _btnNew, _btnDuplicate, _btnDelete;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    private string? _selected;
    private bool _dirty;
    private RunViewData? _live;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _suppressSelect;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public RunsPage(Workspace ws, Action<string> status)
    {
        _ws = ws;
        _status = status;
        _editor = new RunParametersEditor(ws) { Dock = DockStyle.Fill };
        _editor.Changed += (_, _) => { if (!IsSelectedLocked()) { _dirty = true; UpdateButtons(); } };

        // left: run list
        _list.Columns.Add("Name", Ui.S(190));
        _list.Columns.Add("State", Ui.S(80));
        _list.Columns.Add("Model", Ui.S(120));
        _list.Columns.Add("Accuracy", Ui.S(75), HorizontalAlignment.Right);
        _list.Columns.Add("Items", Ui.S(60), HorizontalAlignment.Right);
        _list.Columns.Add("Started", Ui.S(120));
        _list.SelectedIndexChanged += (_, _) => OnListSelection();

        _btnNew = Ui.Button("New…", (_, _) => NewRun());
        _btnDuplicate = Ui.Button("Duplicate…", (_, _) => DuplicateRun());
        _btnDelete = Ui.Button("Delete…", (_, _) => DeleteRun());
        var leftButtons = Ui.ButtonBar(_btnNew, _btnDuplicate, _btnDelete,
            Ui.Button("Refresh", (_, _) => ReloadList(_selected)),
            Ui.Button("Open folder", (_, _) => OpenFolder()));
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_list);
        left.Controls.Add(leftButtons);
        left.Controls.Add(new Label { Text = "Runs", Dock = DockStyle.Top, Height = Ui.S(28), Font = Ui.Big, Padding = new Padding(4, 4, 0, 0) });

        // right: action bar + tabs
        _btnSave = Ui.Button("Save", (_, _) => Save(), 90);
        _btnStart = Ui.Button("▶ Start", async (_, _) => await StartSelectedAsync(), 100);
        _btnStop = Ui.Button("■ Stop", (_, _) => Stop(), 90);
        _btnRestart = Ui.Button("↻ Restart (new _NN)", async (_, _) => await RestartSelectedAsync());
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(2), WrapContents = false };
        _btnRescore = Ui.Button("Rescore…", (_, _) => RescoreSelected());
        actions.Controls.AddRange([_title, _btnSave, _btnStart, _btnStop, _btnRestart, _btnRescore, _progress, _progressText]);

        _tabs.TabPages.Add(Tab("Parameters", _editor));
        _tabs.TabPages.Add(Tab("Questions && answers", _results));
        _tabs.TabPages.Add(Tab("Summary", _summary));
        _tabs.TabPages.Add(Tab("Log", _log));
        _tabs.TabPages.Add(Tab("Run header / server", _header));

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_tabs);
        right.Controls.Add(actions);

        var split = Ui.Split(Orientation.Vertical, 560);
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);

        _timer.Tick += (_, _) => { if (_live != null && _results.Data == _live) _results.UpdateLiveLine(); UpdateProgressText(); };

        ReloadList(null);
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        else UpdateButtons();
    }

    private static TabPage Tab(string title, Control c)
    {
        var p = new TabPage(title) { Padding = new Padding(3) };
        p.Controls.Add(c);
        return p;
    }

    public void RefreshFileLists() => _editor.RefreshFileLists();

    /// <summary>Selects a run in the list (used by the leaderboard).</summary>
    public void SelectRun(string name)
    {
        if (name == _selected) return;
        if (!ConfirmDiscard()) return;
        ReloadList(name);
        LoadRun(name);
    }

    // ---- list ---------------------------------------------------------------------------

    private void ReloadList(string? select)
    {
        _suppressSelect = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _ws.Runs.ListRuns(_live?.Name))
        {
            var model = string.IsNullOrEmpty(r.Definition.Model) ? r.Header?.ModelAlias ?? "" : r.Definition.Model;
            var item = new ListViewItem([
                r.Name,
                r.State.ToString().ToLowerInvariant(),
                model,
                TextReport.Pct(r.Summary?.Accuracy),
                r.Header == null ? "" : $"{r.ItemsDone}/{r.Header.PlannedItems}",
                r.StartedAt?.ToString("yyyy-MM-dd HH:mm") ?? "",
            ]) { Tag = r.Name };
            item.ForeColor = r.State switch
            {
                RunState.Running => Color.Blue,
                RunState.Draft => Color.DimGray,
                RunState.Aborted or RunState.Incomplete => Color.DarkOrange,
                _ => SystemColors.WindowText,
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _suppressSelect = false;
        if (select != null)
        {
            var it = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => (string)i.Tag! == select);
            if (it != null) { _suppressSelect = true; it.Selected = true; it.EnsureVisible(); _suppressSelect = false; }
        }
    }

    private void OnListSelection()
    {
        if (_suppressSelect) return;
        var name = _list.SelectedItems.Count > 0 ? (string)_list.SelectedItems[0].Tag! : null;
        if (name == null || name == _selected) return;
        if (!ConfirmDiscard())
        {
            ReloadList(_selected);
            return;
        }
        LoadRun(name);
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty || _selected == null) return true;
        var r = MessageBox.Show(this, $"Save changes to '{_selected}'?", "LLMBench", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) return Save();
        _dirty = false;
        return true;
    }

    private void LoadRun(string name)
    {
        _selected = name;
        _dirty = false;
        try
        {
            var info = _ws.Runs.GetInfo(name, _live?.Name == name);
            bool locked = info.State != RunState.Draft;
            _editor.LoadDefinition(info.Definition, locked, StateText(info));
            _title.Text = name;

            if (_live != null && _live.Name == name)
            {
                _results.Show(_live);
                _log.Text = Ui.Crlf(_live.Log.ToString());
                _summary.Text = "Run in progress …";
                _header.Text = Ui.Crlf(_live.Header == null ? "" : Json.Serialize(_live.Header));
            }
            else if (info.State == RunState.Draft)
            {
                ShowPlanPreview(info.Definition);
                _summary.Text = "Not started yet.";
                _log.Text = "";
                _header.Text = "";
            }
            else
            {
                var res = RunResults.Load(_ws.Runs.ResultsPath(name));
                var data = RunViewData.FromResults(info.Definition, res);
                _results.Show(data);
                _log.Text = Ui.Crlf(data.Log.ToString());
                _summary.Text = Ui.Crlf(TextReport.Format(res.Header, info.Definition, data.CurrentSummary()));
                _header.Text = Ui.Crlf(res.Header == null ? "" : Json.Serialize(res.Header));
            }
        }
        catch (Exception ex) when (ex is BenchException or IOException or System.Text.Json.JsonException)
        {
            Ui.ShowError(this, ex.Message);
        }
        UpdateButtons();
    }

    private void ShowPlanPreview(RunDefinition def)
    {
        try
        {
            var plan = RunPlanner.Build(_ws, def);
            _results.Show(RunViewData.FromPlan(def, plan, live: false));
        }
        catch (BenchException ex)
        {
            _results.Show(null);
            _log.Text = Ui.Crlf(ex.Message);
        }
    }

    private static string StateText(RunInfo info) => info.State switch
    {
        RunState.Draft => "draft — parameters can be edited",
        RunState.Running => "running",
        RunState.Completed => $"completed {info.Summary?.FinishedAt:yyyy-MM-dd HH:mm} — read-only (use Restart or Duplicate)",
        RunState.Aborted => $"aborted ({info.Summary?.AbortReason}) — read-only",
        _ => "incomplete (no summary) — read-only",
    };

    private bool IsSelectedLocked() => _selected == null || _ws.Runs.HasResults(_selected) || _live?.Name == _selected;

    private void UpdateButtons()
    {
        bool running = IsRunning;
        bool hasSel = _selected != null;
        bool locked = IsSelectedLocked();
        _btnSave.Enabled = hasSel && !locked && _dirty;
        _btnStart.Enabled = hasSel && !locked && !running;
        _btnStop.Enabled = running;
        _btnRestart.Enabled = hasSel && !running && _selected != null && _ws.Runs.HasResults(_selected);
        _btnRescore.Enabled = _btnRestart.Enabled;
        _btnDelete.Enabled = hasSel && _live?.Name != _selected;
        _btnDuplicate.Enabled = hasSel;
        _btnSave.Text = _dirty ? "Save *" : "Save";
    }

    // ---- commands -----------------------------------------------------------------------

    private bool Save()
    {
        if (_selected == null) return false;
        var problems = _editor.TryRead(out var def);
        if (problems.Count > 0) { Ui.ShowError(this, "Please fix:\n\n" + string.Join("\n", problems)); return false; }
        try
        {
            def.Name = _selected;
            _ws.Runs.Save(def);
            _dirty = false;
            _status($"Saved {_ws.Runs.DefinitionPath(_selected)}");
            ShowPlanPreview(def);
            UpdateButtons();
            return true;
        }
        catch (BenchException ex) { Ui.ShowError(this, ex.Message); return false; }
    }

    private void NewRun()
    {
        if (!ConfirmDiscard()) return;
        var name = AskName("New run", "Name of the new run (letters, digits, . - _):", _ws.Runs.SuggestNewName("run"));
        if (name == null) return;
        RunDefinition def;
        try { def = _ws.LoadRunDefaults(); }
        catch (BenchException ex) { Ui.ShowError(this, ex.Message); def = new RunDefinition(); }
        def.Name = name;
        _ws.Runs.Save(def);
        _selected = null;
        ReloadList(name);
        LoadRun(name);
        _tabs.SelectedIndex = 0;
    }

    private void DuplicateRun()
    {
        if (_selected == null || !ConfirmDiscard()) return;
        var name = AskName("Duplicate run", $"Copy the parameters of '{_selected}' to a new run named:", _ws.Runs.SuggestNewName(_selected + "_copy"));
        if (name == null) return;
        try
        {
            _ws.Runs.Duplicate(_selected, name);
            _selected = null;
            ReloadList(name);
            LoadRun(name);
            _tabs.SelectedIndex = 0;
        }
        catch (BenchException ex) { Ui.ShowError(this, ex.Message); }
    }

    private string? AskName(string title, string prompt, string suggestion)
    {
        while (true)
        {
            var name = InputDialog.Ask(this, title, prompt, suggestion);
            if (name == null) return null;
            if (!RunStore.IsValidName(name)) { Ui.ShowError(this, "Invalid name. Use letters, digits, '.', '-' and '_'."); suggestion = name; continue; }
            if (Directory.Exists(_ws.Runs.RunDir(name))) { Ui.ShowError(this, $"Run '{name}' already exists."); suggestion = name; continue; }
            return name;
        }
    }

    private void DeleteRun()
    {
        if (_selected == null || _live?.Name == _selected) return;
        var dir = _ws.Runs.RunDir(_selected);
        var hasResults = _ws.Runs.HasResults(_selected);
        var r = MessageBox.Show(this,
            $"Move run '{_selected}' to the recycle bin?\n\n{dir}" + (hasResults ? "\n\nThe run has results; they are moved as well." : ""),
            "Delete run", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;
        try
        {
            FileSystem.DeleteDirectory(dir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            _status($"Moved {dir} to the recycle bin.");
        }
        catch (Exception ex) { Ui.ShowError(this, ex.Message); }
        _selected = null;
        _dirty = false;
        ReloadList(null);
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        else { _results.Show(null); _title.Text = ""; UpdateButtons(); }
    }

    private void RescoreSelected()
    {
        if (_selected == null || IsRunning) return;
        try
        {
            var preview = Rescorer.Rescore(_ws, _selected, dryRun: true);
            if (preview.Changes.Count == 0)
            {
                MessageBox.Show(this, $"Scoring the stored replies of '{_selected}' again with the current rules changes nothing.", "Rescore");
                return;
            }
            var lines = preview.Changes.Take(15).Select(c => $"{c.Key} r{c.Repetition}: {c.OldStatus.ToString().ToLowerInvariant()}{(c.OldReason != null ? ":" + c.OldReason : "")} → {c.NewStatus.ToString().ToLowerInvariant()}");
            var text = $"Scoring the stored replies of '{_selected}' again with the current rules changes {preview.Changes.Count} item(s):\n\n" +
                       string.Join("\n", lines) + (preview.Changes.Count > 15 ? $"\n… (+{preview.Changes.Count - 15})" : "") +
                       $"\n\nAccuracy {TextReport.Pct(preview.OldSummary?.Accuracy)} → {TextReport.Pct(preview.NewSummary?.Accuracy)}. " +
                       "No model is needed; the old results file is kept as a backup in the run folder.\n\nApply?";
            if (MessageBox.Show(this, text, "Rescore", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var done = Rescorer.Rescore(_ws, _selected, dryRun: false);
            _status($"Rescored '{_selected}': {done.Changes.Count} change(s), backup {Path.GetFileName(done.BackupFile)}");
            var name = _selected;
            _selected = null;
            ReloadList(name);
            LoadRun(name);
        }
        catch (Exception ex) when (ex is BenchException or IOException) { Ui.ShowError(this, ex.Message); }
    }

    private void OpenFolder()
    {
        var dir = _selected != null ? _ws.Runs.RunDir(_selected) : _ws.RunsDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private async Task RestartSelectedAsync()
    {
        if (_selected == null || IsRunning) return;
        try
        {
            var def = _ws.Runs.CreateRestart(_selected);
            _selected = null;
            ReloadList(def.Name);
            LoadRun(def.Name);
            await StartSelectedAsync();
        }
        catch (BenchException ex) { Ui.ShowError(this, ex.Message); }
    }

    private async Task StartSelectedAsync()
    {
        if (_selected == null || IsRunning) return;
        var problems = _editor.TryRead(out var def);
        if (problems.Count > 0) { Ui.ShowError(this, "Please fix:\n\n" + string.Join("\n", problems)); return; }
        def.Name = _selected;

        var runner = new BenchRunner(_ws);
        RunPlan plan;
        try
        {
            _ws.Runs.Save(def);
            _dirty = false;
            plan = runner.Prepare(def);
            if (plan.Items.Count == 0) throw new BenchException("Nothing to run: no question/variant matches the filters.");
        }
        catch (BenchException ex) { Ui.ShowError(this, ex.Message); return; }

        _live = RunViewData.FromPlan(def, plan, live: true);
        _live.RunStarted = DateTime.Now;
        _results.Show(_live);
        _log.Text = Ui.Crlf(_live.Log.ToString());
        _summary.Text = "Run in progress …";
        _header.Text = "";
        _progress.Maximum = plan.Items.Count;
        _progress.Value = 0;
        _tabs.SelectedIndex = 1;
        _cts = new CancellationTokenSource();
        var live = _live;
        var progress = new Progress<RunEvent>(e => OnRunEvent(live, e));
        _editor.LoadDefinition(def, true, "running");
        _timer.Start();
        ReloadList(def.Name);
        _status($"Running '{def.Name}' …");

        var runTask = runner.RunAsync(def, plan, progress, _cts.Token);
        _runTask = runTask;
        UpdateButtons();
        try
        {
            var summary = await runTask;
            AppendLog(live, summary.Completed ? "Run completed." : $"Run aborted: {summary.AbortReason}");
            _status(summary.Completed
                ? $"Run '{def.Name}' completed: accuracy {TextReport.Pct(summary.Accuracy)}"
                : $"Run '{def.Name}' aborted ({summary.AbortReason}) after {summary.Done} items: accuracy {TextReport.Pct(summary.Accuracy)}");
        }
        catch (OperationCanceledException)
        {
            AppendLog(live, "Stopped during preflight; nothing was written. The run is still a draft.");
            _status("Stopped.");
        }
        catch (BenchException ex)
        {
            AppendLog(live, "ERROR: " + ex.Message);
            Ui.ShowError(this, ex.Message + "\n\nThe run was not started (still a draft).");
            _status("Run not started: " + ex.Message);
        }
        catch (Exception ex)
        {
            AppendLog(live, "UNEXPECTED ERROR: " + ex);
            Ui.ShowError(this, ex.ToString());
        }
        finally
        {
            _timer.Stop();
            _cts.Dispose();
            _cts = null;
            _live = null;
            var current = _selected;
            ReloadList(current);
            if (current == def.Name) { _selected = null; LoadRun(def.Name); }
            UpdateButtons();
            _progressText.Text = "";
        }
    }

    private void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        _status("Stopping …");
    }

    /// <summary>Used when closing the window during a run.</summary>
    public void StopAndWait()
    {
        _cts?.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
    }

    // ---- live events ----------------------------------------------------------------------

    private void OnRunEvent(RunViewData live, RunEvent e)
    {
        bool visible = _results.Data == live;
        switch (e.Kind)
        {
            case RunEventKind.Log:
                if (e.Header != null)
                {
                    live.Header = e.Header;
                    if (visible) _header.Text = Ui.Crlf(Json.Serialize(e.Header));
                }
                AppendLog(live, e.Message ?? "");
                break;
            case RunEventKind.Warning:
                AppendLog(live, "WARNING: " + e.Message);
                break;
            case RunEventKind.ItemStarted:
                var prev = live.CurrentIndex;
                live.CurrentIndex = e.Item!.Index;
                live.CurrentStarted = DateTime.Now;
                if (visible)
                {
                    if (prev.HasValue) _results.Refresh(prev.Value, false);
                    _results.Refresh(e.Item.Index, true);
                }
                break;
            case RunEventKind.ItemCompleted:
                var row = live.Rows.First(r => r.Index == e.Record!.Index);
                row.Record = e.Record;
                _progress.Value = Math.Min(_progress.Maximum, live.Records.Count);
                if (visible) _results.Refresh(row.Index, false);
                break;
            case RunEventKind.Finished:
                live.Summary = e.Summary;
                live.CurrentIndex = null;
                if (visible)
                {
                    _summary.Text = Ui.Crlf(TextReport.Format(e.Header, live.Definition, e.Summary!));
                    _results.UpdateLiveLine();
                }
                break;
        }
        UpdateProgressText();
    }

    private void AppendLog(RunViewData live, string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss}  {line}";
        live.Log.AppendLine(text);
        if (_results.Data == live) _log.AppendText(Ui.Crlf(text) + "\r\n");
    }

    private void UpdateProgressText()
    {
        if (_live == null) { _progressText.Text = ""; return; }
        var done = _live.Records.Count;
        var elapsed = DateTime.Now - (_live.RunStarted ?? DateTime.Now);
        var eta = done > 0 ? TimeSpan.FromSeconds(elapsed.TotalSeconds / done * (_live.Rows.Count - done)) : (TimeSpan?)null;
        _progressText.Text = $"{_live.Name}: {done}/{_live.Rows.Count}   elapsed {elapsed:hh\\:mm\\:ss}" + (eta.HasValue ? $"   remaining ~{eta:hh\\:mm\\:ss}" : "");
    }
}
