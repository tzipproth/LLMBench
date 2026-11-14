using System.Globalization;
using System.Text.Json.Nodes;
using LLMBench.Core;
using LLMBench.Core.Catalog;
using LLMBench.Core.Llama;
using LLMBench.Core.Prompting;
using LLMBench.Core.Runs;

namespace LLMBench.Gui;

/// <summary>Edits all parameters of a run definition. Read-only for runs that already have results.</summary>
internal sealed class RunParametersEditor : UserControl
{
    private const string NoPrompt = "(none)";
    private readonly Workspace _ws;
    private RunDefinition _def = new();
    private bool _readOnly;
    private bool _loading;

    // General
    private readonly Label _name = new() { AutoSize = true, Font = Ui.Big, Anchor = AnchorStyles.Left };
    private readonly Label _state = new() { AutoSize = false, ForeColor = Color.DimGray };
    private readonly TextBox _description = Ui.Text("optional description");
    // Server
    private readonly TextBox _serverUrl = Ui.Text("http://127.0.0.1:8081");
    private readonly TextBox _model = Ui.Text("model name sent in the request (server alias)");
    private readonly TextBox _ctx = Ui.Text("empty = no check");
    private readonly TextBox _serverNotes = Ui.MonoBox(false);
    private readonly Label _serverInfo = new() { ForeColor = Color.DimGray, Anchor = AnchorStyles.Left };
    private JsonObject? _props;
    // Questions
    private readonly ComboBox _catalog = Ui.Combo();
    private readonly CheckBox _allVariants = new() { Text = "all variants", AutoSize = true, Checked = true };
    private readonly CheckedListBox _variants = new() { CheckOnClick = true, IntegralHeight = false, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly CheckedListBox _categories = new() { CheckOnClick = true, IntegralHeight = false, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly CheckedListBox _tiers = new() { CheckOnClick = true, IntegralHeight = false, MultiColumn = true, ColumnWidth = Ui.S(60), Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly TextBox _ids = Ui.Text("comma separated, empty = all");
    private readonly NumericUpDown _limit = new() { Minimum = 0, Maximum = 100000, Width = Ui.S(90) };
    private readonly Label _catalogInfo = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left };
    // Prompt
    private readonly ComboBox _systemPrompt = Ui.Combo();
    private readonly ComboBox _placement = Ui.Combo("system", "user");
    private readonly ComboBox _instruction = Ui.Combo(AnswerInstructions.All);
    private readonly TextBox _promptPreview = Ui.MonoBox();
    // Thinking
    private readonly ComboBox _thinking = Ui.Combo("default", "on", "off");
    private readonly TextBox _thinkingOn = Ui.MonoBox(false);
    private readonly TextBox _thinkingOff = Ui.MonoBox(false);
    private readonly Label _probeInfo = new() { ForeColor = Color.DimGray };
    // Sampling
    private readonly TextBox _temperature = Ui.Text("server default");
    private readonly TextBox _topP = Ui.Text("server default");
    private readonly TextBox _topK = Ui.Text("server default");
    private readonly TextBox _minP = Ui.Text("server default");
    private readonly TextBox _repeatPenalty = Ui.Text("server default");
    private readonly TextBox _presencePenalty = Ui.Text("server default");
    private readonly TextBox _seed = Ui.Text("random");
    private readonly TextBox _maxTokens = Ui.Text("unlimited");
    // Execution
    private readonly NumericUpDown _repetitions = new() { Minimum = 1, Maximum = 1000, Width = Ui.S(90) };
    private readonly CheckBox _shuffle = new() { Text = "shuffle options (reproducible)", AutoSize = true };
    private readonly NumericUpDown _shuffleSeed = new() { Minimum = 0, Maximum = int.MaxValue, Width = Ui.S(120) };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 86400, Width = Ui.S(90) };
    private readonly TextBox _extra = Ui.MonoBox(false);

    private readonly List<Control> _editable = [];

    public event EventHandler? Changed;

    public RunParametersEditor(Workspace ws)
    {
        _ws = ws;
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));

        // ---- column 1: general, server, questions
        var general = new FormGroup("Run");
        general.Add("Name", _name);
        general.Add("State", _state, 40);
        general.Add("Description", _description);

        var server = new FormGroup("Server and model");
        server.Add("Server URL", _serverUrl);
        server.Add("Model", _model);
        server.Add("Expected context", _ctx);
        server.AddWide(Ui.ButtonBar(
            Ui.Button("Check server", async (_, _) => await CheckServerAsync()),
            Ui.Button("Use server alias", (_, _) => { if (_props?["model_alias"] is { } a) _model.Text = a.ToString(); }),
            Ui.Button("Use server context", (_, _) => { if (_props?["default_generation_settings"]?["n_ctx"] is { } c) _ctx.Text = c.ToString(); }),
            Ui.Button("Copy server sampler", (_, _) => CopyServerSampler())), 80);
        server.AddWide(_serverInfo, 92);
        server.Add("Server notes", _serverNotes, 90);
        _serverNotes.PlaceholderText = "e.g. the llama-server start command, hardware, quantization ...";

        var questions = new FormGroup("Questions");
        questions.Add("Catalog", _catalog);
        questions.AddWide(_catalogInfo, 24);
        questions.Add("Variants", _allVariants);
        questions.Add("", _variants, 70);
        questions.Add("Categories", _categories, 80);
        questions.Add("Tiers", _tiers, 28);
        questions.Add("Question IDs", _ids);
        questions.Add("Limit (questions)", _limit);
        _tiers.Items.AddRange(["1", "2", "3", "4", "5", "6", "7", "8"]);

        // ---- column 2: prompt, thinking
        var prompt = new FormGroup("Prompt");
        prompt.Add("System prompt", _systemPrompt);
        prompt.Add("Placement", _placement);
        prompt.Add("Answer instruction", _instruction);
        prompt.AddWide(Ui.Label("Preview (first planned item):"));
        prompt.AddWide(_promptPreview, 260);

        var thinking = new FormGroup("Thinking");
        thinking.Add("Thinking", _thinking);
        thinking.Add("Fragment ON", _thinkingOn, 60);
        thinking.Add("Fragment OFF", _thinkingOff, 60);
        thinking.AddWide(Ui.ButtonBar(Ui.Button("Probe thinking (ON vs OFF)", async (_, _) => await ProbeThinkingAsync())), 40);
        thinking.AddWide(_probeInfo, 48);
        thinking.AddWide(new Label
        {
            ForeColor = Color.DimGray,
            Text = "'default' sends nothing (server/template decide). The ON/OFF fragment is merged into the request.",
        }, 44);

        // ---- column 3: sampling, execution, extra
        var sampling = new FormGroup("Sampling (empty = not sent, server default applies)");
        sampling.Add("temperature", _temperature);
        sampling.Add("top_p", _topP);
        sampling.Add("top_k", _topK);
        sampling.Add("min_p", _minP);
        sampling.Add("repeat_penalty", _repeatPenalty);
        sampling.Add("presence_penalty", _presencePenalty);
        sampling.Add("seed (rep i: seed+i)", _seed);
        sampling.Add("max_tokens", _maxTokens);
        sampling.AddWide(Ui.ButtonBar(
            Ui.Button("Deterministic profile", (_, _) => { _temperature.Text = "0"; _topP.Text = "0.95"; _topK.Text = ""; _minP.Text = ""; _repeatPenalty.Text = ""; _presencePenalty.Text = ""; _seed.Text = "42"; _repetitions.Value = 1; })), 40);

        var exec = new FormGroup("Execution");
        exec.Add("Repetitions", _repetitions);
        exec.Add("", _shuffle);
        exec.Add("Shuffle seed", _shuffleSeed);
        exec.Add("Timeout (s)", _timeout);
        exec.Add("Extra request JSON", _extra, 90);
        exec.AddWide(Ui.ButtonBar(Ui.Button("Save as default for new runs", (_, _) => SaveAsDefaults())), 40);

        columns.Controls.Add(Column(general, server, questions), 0, 0);
        columns.Controls.Add(Column(prompt, thinking), 1, 0);
        columns.Controls.Add(Column(sampling, exec), 2, 0);
        Controls.Add(columns);

        _editable.AddRange([_description, _serverUrl, _model, _ctx, _serverNotes, _catalog, _allVariants, _variants, _categories, _tiers, _ids, _limit,
            _systemPrompt, _placement, _instruction, _thinking, _thinkingOn, _thinkingOff,
            _temperature, _topP, _topK, _minP, _repeatPenalty, _presencePenalty, _seed, _maxTokens,
            _repetitions, _shuffle, _shuffleSeed, _timeout, _extra]);

        _catalog.SelectedIndexChanged += (_, _) => { if (!_loading) { LoadCatalogLists(null); OnChanged(); } };
        _allVariants.CheckedChanged += (_, _) => { _variants.Enabled = !_allVariants.Checked && !_readOnly; OnChanged(); };
        _systemPrompt.SelectedIndexChanged += (_, _) => OnChanged();
        _placement.SelectedIndexChanged += (_, _) => OnChanged();
        _instruction.SelectedIndexChanged += (_, _) => OnChanged();
        _variants.ItemCheck += (_, _) => BeginInvoke(OnChanged);
        _categories.ItemCheck += (_, _) => BeginInvoke(OnChanged);
        _shuffle.CheckedChanged += (_, _) => OnChanged();

        RefreshFileLists();
    }

    private static Panel Column(params Control[] groups)
    {
        var p = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(Ui.S(2)) };
        foreach (var g in groups.Reverse()) p.Controls.Add(g);
        return p;
    }

    private void OnChanged()
    {
        if (_loading) return;
        UpdatePreview();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshFileLists()
    {
        var cat = _catalog.SelectedItem as string;
        var sp = _systemPrompt.SelectedItem as string;
        _loading = true;
        _catalog.Items.Clear();
        _catalog.Items.AddRange([.. _ws.ListCatalogs()]);
        _systemPrompt.Items.Clear();
        _systemPrompt.Items.Add(NoPrompt);
        _systemPrompt.Items.AddRange([.. _ws.ListPrompts()]);
        Select(_catalog, cat);
        Select(_systemPrompt, sp ?? NoPrompt);
        _loading = false;
    }

    private static void Select(ComboBox c, string? value)
    {
        if (value == null) return;
        int i = c.Items.IndexOf(value);
        if (i < 0) { c.Items.Add(value); i = c.Items.Count - 1; }
        c.SelectedIndex = i;
    }

    // ---- load / read -------------------------------------------------------------------

    public void LoadDefinition(RunDefinition def, bool readOnly, string stateText)
    {
        _loading = true;
        _def = def.Clone();
        _readOnly = readOnly;
        _name.Text = def.Name;
        _state.Text = stateText + (def.RestartOf != null ? $"  (restart of {def.RestartOf})" : "");
        _description.Text = def.Description ?? "";
        _serverUrl.Text = def.ServerUrl;
        _model.Text = def.Model;
        _ctx.Text = Ui.Num(def.ExpectedContextSize);
        _serverNotes.Text = Ui.Crlf(def.ServerNotes);
        Select(_catalog, def.CatalogFile);
        LoadCatalogLists(def);
        _allVariants.Checked = def.AllVariants;
        _ids.Text = string.Join(", ", def.QuestionIds);
        _limit.Value = Math.Clamp(def.Limit, 0, (int)_limit.Maximum);
        Select(_systemPrompt, string.IsNullOrWhiteSpace(def.SystemPromptFile) ? NoPrompt : def.SystemPromptFile);
        Select(_placement, def.SystemPromptPlacement);
        Select(_instruction, def.AnswerInstruction);
        Select(_thinking, def.Thinking);
        _thinkingOn.Text = def.ThinkingOn.ToJsonString();
        _thinkingOff.Text = def.ThinkingOff.ToJsonString();
        _temperature.Text = Ui.Num(def.Temperature);
        _topP.Text = Ui.Num(def.TopP);
        _topK.Text = Ui.Num(def.TopK);
        _minP.Text = Ui.Num(def.MinP);
        _repeatPenalty.Text = Ui.Num(def.RepeatPenalty);
        _presencePenalty.Text = Ui.Num(def.PresencePenalty);
        _seed.Text = Ui.Num(def.Seed);
        _maxTokens.Text = Ui.Num(def.MaxTokens);
        _repetitions.Value = Math.Clamp(def.Repetitions, 1, (int)_repetitions.Maximum);
        _shuffle.Checked = def.ShuffleOptions;
        _shuffleSeed.Value = Math.Clamp(def.ShuffleSeed, 0, int.MaxValue);
        _timeout.Value = Math.Clamp(def.TimeoutSeconds, 1, (int)_timeout.Maximum);
        _extra.Text = Ui.Crlf(def.ExtraRequest.ToJsonString(Json.Indented));
        foreach (var c in _editable)
        {
            if (c is TextBoxBase tb) tb.ReadOnly = readOnly;
            else c.Enabled = !readOnly;
        }
        _variants.Enabled = !readOnly && !_allVariants.Checked;
        _loading = false;
        UpdatePreview();
    }

    private void LoadCatalogLists(RunDefinition? def)
    {
        var selectedVariants = def?.Variants ?? _variants.CheckedItems.Cast<string>().ToList();
        var selectedCategories = def?.Categories ?? _categories.CheckedItems.Cast<string>().ToList();
        var selectedTiers = def?.Tiers.Select(t => t.ToString(CultureInfo.InvariantCulture)).ToList() ?? _tiers.CheckedItems.Cast<string>().ToList();
        _variants.Items.Clear();
        _categories.Items.Clear();
        _catalogInfo.Text = "";
        if (_catalog.SelectedItem is string file)
        {
            try
            {
                var c = CatalogLoader.Load(_ws, file);
                foreach (var v in c.VariantIds) _variants.Items.Add(v, selectedVariants.Contains(v));
                foreach (var cat in c.Categories) _categories.Items.Add(cat, selectedCategories.Contains(cat));
                _catalogInfo.Text = $"{c.Questions.Count} questions, {c.VariantCount} variants" + (c.IsValid ? "" : $" — {c.Errors.Count} ERRORS (see Catalogs tab)");
                _catalogInfo.ForeColor = c.IsValid ? Color.DimGray : Color.Red;
            }
            catch (BenchException ex) { _catalogInfo.Text = ex.Message; _catalogInfo.ForeColor = Color.Red; }
        }
        for (int i = 0; i < _tiers.Items.Count; i++) _tiers.SetItemChecked(i, selectedTiers.Contains((string)_tiers.Items[i]));
    }

    /// <summary>Reads the form into a definition. Returns problems (empty = ok).</summary>
    public List<string> TryRead(out RunDefinition def)
    {
        var p = new List<string>();
        def = _def.Clone();
        def.Description = string.IsNullOrWhiteSpace(_description.Text) ? null : _description.Text.Trim();
        def.ServerUrl = _serverUrl.Text.Trim();
        def.Model = _model.Text.Trim();
        def.ExpectedContextSize = ParseInt(_ctx, "Expected context", p);
        def.ServerNotes = _serverNotes.Text.Replace("\r\n", "\n").Trim();
        def.CatalogFile = _catalog.SelectedItem as string ?? "";
        def.Variants = _allVariants.Checked ? ["all"] : _variants.CheckedItems.Cast<string>().ToList();
        if (!_allVariants.Checked && def.Variants.Count == 0) p.Add("Select at least one variant or 'all variants'.");
        def.Categories = _categories.CheckedItems.Cast<string>().ToList();
        def.Tiers = _tiers.CheckedItems.Cast<string>().Select(t => int.Parse(t, CultureInfo.InvariantCulture)).ToList();
        def.QuestionIds = _ids.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        def.Limit = (int)_limit.Value;
        def.SystemPromptFile = _systemPrompt.SelectedItem is string sp && sp != NoPrompt ? sp : null;
        def.SystemPromptPlacement = (string)_placement.SelectedItem!;
        def.AnswerInstruction = (string)_instruction.SelectedItem!;
        def.Thinking = (string)_thinking.SelectedItem!;
        def.ThinkingOn = ParseJson(_thinkingOn, "Fragment ON", p);
        def.ThinkingOff = ParseJson(_thinkingOff, "Fragment OFF", p);
        def.Temperature = ParseDouble(_temperature, "temperature", p);
        def.TopP = ParseDouble(_topP, "top_p", p);
        def.TopK = ParseInt(_topK, "top_k", p);
        def.MinP = ParseDouble(_minP, "min_p", p);
        def.RepeatPenalty = ParseDouble(_repeatPenalty, "repeat_penalty", p);
        def.PresencePenalty = ParseDouble(_presencePenalty, "presence_penalty", p);
        def.Seed = string.IsNullOrWhiteSpace(_seed.Text) ? null
            : long.TryParse(_seed.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : Fail<long?>(p, "seed must be an integer.");
        def.MaxTokens = ParseInt(_maxTokens, "max_tokens", p);
        def.Repetitions = (int)_repetitions.Value;
        def.ShuffleOptions = _shuffle.Checked;
        def.ShuffleSeed = (int)_shuffleSeed.Value;
        def.TimeoutSeconds = (int)_timeout.Value;
        def.ExtraRequest = ParseJson(_extra, "Extra request JSON", p);
        p.AddRange(def.Check());
        return p;
    }

    private static T Fail<T>(List<string> p, string msg) { p.Add(msg); return default!; }

    private static double? ParseDouble(TextBox t, string name, List<string> p)
    {
        var s = t.Text.Trim().Replace(',', '.');
        if (s.Length == 0) return null;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        p.Add($"{name}: '{t.Text}' is not a number.");
        return null;
    }

    private static int? ParseInt(TextBox t, string name, List<string> p)
    {
        var s = t.Text.Trim();
        if (s.Length == 0) return null;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        p.Add($"{name}: '{t.Text}' is not an integer.");
        return null;
    }

    private static JsonObject ParseJson(TextBox t, string name, List<string> p)
    {
        try { return Json.ParseObject(t.Text); }
        catch (Exception ex) { p.Add($"{name}: invalid JSON ({ex.Message})"); return new JsonObject(); }
    }

    // ---- server ------------------------------------------------------------------------

    public async Task CheckServerAsync()
    {
        _serverInfo.ForeColor = Color.DimGray;
        _serverInfo.Text = "Checking ...";
        try
        {
            using var client = new LlamaClient(_serverUrl.Text.Trim());
            var h = await client.GetHealthAsync(CancellationToken.None);
            if (!h.Ok)
            {
                _serverInfo.ForeColor = Color.Red;
                _serverInfo.Text = "Not ready: " + h.Message;
                return;
            }
            _props = await client.GetPropsAsync(CancellationToken.None);
            var gs = _props?["default_generation_settings"];
            var par = gs?["params"];
            _serverInfo.ForeColor = Color.DarkGreen;
            _serverInfo.Text =
                $"OK — alias {_props?["model_alias"]}, ctx {gs?["n_ctx"]}, slots {_props?["total_slots"]}, build {_props?["build_info"]}\n" +
                $"model {_props?["model_path"]}\n" +
                $"vision {_props?["modalities"]?["vision"]}; server sampler: temp {par?["temperature"]}, top_k {par?["top_k"]}, top_p {Ui.Num(par?["top_p"]?.GetValue<double>())}, min_p {par?["min_p"]}, repeat_penalty {par?["repeat_penalty"]}, reasoning_format {par?["reasoning_format"]}\n" +
                $"template thinking switches: {ChatTemplateInfo.Analyze(_props?["chat_template"]?.GetValue<string>()).Summary()}";
        }
        catch (Exception ex)
        {
            _serverInfo.ForeColor = Color.Red;
            _serverInfo.Text = "Error: " + ex.Message;
        }
    }

    private async Task ProbeThinkingAsync()
    {
        TryRead(out var def);
        _probeInfo.ForeColor = Color.DimGray;
        _probeInfo.Text = "Sending the probe question with the ON and the OFF fragment ...";
        try
        {
            using var client = new LlamaClient(def.ServerUrl);
            var r = await ThinkingProbe.RunAsync(client, def, CancellationToken.None);
            _probeInfo.ForeColor = r.Verified ? Color.DarkGreen : Color.Red;
            _probeInfo.Text = (r.Verified ? "VERIFIED: " : "NOT VERIFIED: ") + r.Note;
        }
        catch (Exception ex)
        {
            _probeInfo.ForeColor = Color.Red;
            _probeInfo.Text = "Error: " + ex.Message;
        }
    }

    private void SaveAsDefaults()
    {
        var problems = TryRead(out var def);
        problems.RemoveAll(p => p.StartsWith("Name ", StringComparison.Ordinal));
        if (problems.Count > 0) { Ui.ShowError(this, "Please fix:\n\n" + string.Join("\n", problems)); return; }
        var r = MessageBox.Show(this,
            $"Use these parameters for every new run?\n\n(saved to {_ws.RunDefaultsPath}; name, description and dates are not stored)",
            "LLMBench", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        _ws.SaveRunDefaults(def);
    }

    private void CopyServerSampler()
    {
        var par = _props?["default_generation_settings"]?["params"];
        if (par == null) { Ui.ShowError(this, "Check the server first."); return; }
        if (_readOnly) return;
        _temperature.Text = Ui.Num(par["temperature"]?.GetValue<double>());
        _topP.Text = Ui.Num(Math.Round(par["top_p"]?.GetValue<double>() ?? 0, 4));
        _topK.Text = Ui.Num(par["top_k"]?.GetValue<int>());
        _minP.Text = Ui.Num(Math.Round(par["min_p"]?.GetValue<double>() ?? 0, 4));
        _repeatPenalty.Text = Ui.Num(par["repeat_penalty"]?.GetValue<double>());
        _presencePenalty.Text = Ui.Num(par["presence_penalty"]?.GetValue<double>());
    }

    private void UpdatePreview()
    {
        var problems = TryRead(out var def);
        if (string.IsNullOrEmpty(def.Name)) def.Name = "preview";
        try
        {
            var plan = RunPlanner.Build(_ws, def);
            var it = plan.Items.FirstOrDefault();
            var head = $"{plan.Items.Count} requests planned" + (plan.Skipped.Count > 0 ? $", {plan.Skipped.Count} variants skipped (missing)" : "");
            _promptPreview.Text = it == null ? head : Ui.Crlf(
                $"{head}\n\n#0 {it.Key} — expected: {it.Expected}\n\n" +
                (it.SystemMessage != null ? $"[system]\n{it.SystemMessage}\n\n" : "") +
                $"[user]\n{it.UserMessage}");
        }
        catch (Exception ex) when (ex is BenchException or IOException)
        {
            _promptPreview.Text = Ui.Crlf((problems.Count > 0 ? string.Join("\n", problems) + "\n\n" : "") + ex.Message);
        }
    }
}
