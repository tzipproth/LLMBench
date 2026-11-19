using System.Globalization;
using System.Text;
using LLMBench.Cli;
using LLMBench.Core;
using LLMBench.Core.Catalog;
using LLMBench.Core.Llama;
using LLMBench.Core.Runs;
using LLMBench.Core.Scoring;
using LLMBench.Core.Stats;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

try
{
    var a = Args.Parse(args);
    if (a.Positional.Count == 0 || a.Has("help")) { Commands.Usage(); return a.Has("help") ? 0 : 1; }
    var cmd = a.Positional[0].ToLowerInvariant();
    a.Positional.RemoveAt(0);

    var ws = Workspace.Find(a.Get("workspace"));
    return cmd switch
    {
        "list" => Commands.List(ws),
        "catalogs" => Commands.Catalogs(ws),
        "validate" => Commands.Validate(ws, a),
        "show" or "show-catalog" => Commands.ShowCatalog(ws, a),
        "server" => await Commands.Server(ws, a),
        "new" => Commands.New(ws, a),
        "params" => Commands.Params(ws, a),
        "preview" => Commands.Preview(ws, a),
        "run" => await Commands.Run(ws, a),
        "restart" => await Commands.Restart(ws, a),
        "report" => Commands.Report(ws, a),
        "items" => Commands.Items(ws, a),
        "item" => Commands.Item(ws, a),
        "compare" => Commands.Compare(ws, a),
        "leaderboard" or "lb" => Commands.LeaderboardCmd(ws, a),
        "question-stats" or "qs" => Commands.QuestionStatsCmd(ws, a),
        "export-for-llm" => Commands.ExportForLlm(ws, a),
        "export-csv" => Commands.ExportCsv(ws, a),
        "rescore" => Commands.Rescore(ws, a),
        "merge" => Commands.Merge(ws, a),
        "calibrate" => Commands.Calibrate(ws, a),
        "overview" => Commands.Overview(ws, a),
        "probe-thinking" => await Commands.ProbeThinking(ws, a),
        "auto" => await Commands.Auto(ws, a),
        _ => throw new UsageException($"Unknown command '{cmd}'."),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    Console.Error.WriteLine("Use 'llmbench --help' for usage.");
    return 2;
}
catch (BenchException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}
catch (FormatException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}

internal static partial class Commands
{
    public static void Usage()
    {
        Console.WriteLine($$"""
            LLMBench {{BenchRunner.Version}} - small local reasoning benchmark for llama-server

            Usage: llmbench [--workspace DIR] <command> [arguments] [options]

            Runs (stored in runs/<name>/run.json + results.jsonl):
              list                         list all runs with state and accuracy
              new <name> [options]         create a run definition (draft) without starting it
              params <name>                print the parameters of a run
              preview <name> [--index N]   show the planned requests (composed prompts) without sending
              run <name> [options]         start a draft run; creates it if it does not exist.
                                           Options are applied to the definition and saved.
              restart <name>               copy parameters to <name>_01, _02, ... and start that run
              report <name>                print the summary of a run
              items <name> [--status S]    list item results (S = correct|incorrect|invalid|truncated|error|skipped)
              item <name> <index>          show one item completely (prompt, answer, reasoning)
              compare <runA> <runB>        question-by-question comparison, McNemar test, parameter differences
              rescore <run>|--all [--dry-run]   score the stored replies again with the current scoring rules
                                           (no model needed; the old results file is kept as a backup)
              merge <name> <run> <run> ... --catalog FILE
                                           combine finished runs of one model on parts of a catalog into one run on
                                           the whole catalog, as if it had been run in one go (checks that every
                                           request is identical; e.g. v4 run + tier-9 run -> run on core-v5.json)
              leaderboard [--all] [--model M] [--group HASH]
                                           ranking of all runs that ask the same questions
                                           (--all: include incomplete/aborted runs)
              leaderboard/question-stats/export-* also take --name TEXT (only runs whose name contains TEXT)
              calibrate [--group HASH] [--name TEXT] [--reference RUN] [--out FILE]
                                           estimate item difficulties (Rasch model) from the completed runs of a
                                           question set and freeze them in calibrations/; the leaderboard then shows
                                           a Score on this fixed scale, also for runs added later; with --reference
                                           also a Strength (times the odds of solving a question vs that run)
              export-csv [--group HASH] [--all] [--name TEXT] [--out FILE] [--no-question]
                                           compact results table (one row per variant, answer + evaluation per run,
                                           question text last) plus a legend file and a briefing for an LLM
                                           (purpose, catalog format, scoring rules, results summary, task)
              export-for-llm [--group HASH] [--all] [--name TEXT] [--out FILE]
                                           questions + per-run results as Markdown for an LLM that proposes new questions
              question-stats [--group HASH] [--all] [--filter all|none|separates]
                                           solve rate of every question across the runs of a leaderboard group
                                           (for calibrating the catalog: find questions all or none solve)
              probe-thinking [<run>] [options]   check whether the thinking ON/OFF fragments work
              auto <config.json> [--prefix P] [--only a,b] [run options]
                                           unattended runs over several models: for every model of the config
                                           start llama-server, wait until it is loaded, run (name P<alias>, default
                                           prefix v5-), stop the server, next model. Completed runs are skipped, so
                                           calling it again continues. Models without enable_thinking in their
                                           template run with thinking 'default'. Example config: auto/models.json

            Catalogs and server:
              catalogs                     list catalog files
              validate [catalog ...]       validate catalogs (default: all)
              show <catalog> [--id ID]     print questions of a catalog (base and variants)
              overview <catalog> [--out FILE]   readable Markdown overview of all questions (default docs/<name>-overview.md)
              server [--server URL]        check /health and show /props summary

            Run options (for new/run):
              --from <run>                 start with the parameters of an existing run
              --server URL  --model NAME  --ctx N (expected context size)  --notes TEXT  --description TEXT
              --catalog FILE  --variants a,b|all  --categories a,b  --tiers 1,2  --ids ID,ID  --limit N
              --system-prompt FILE|none  --placement system|user  --instruction none|reasoned|direct
              --thinking on|off|default  --thinking-on JSON  --thinking-off JSON
              --temperature X  --top-p X  --top-k N  --min-p X  --repeat-penalty X  --presence-penalty X
              --seed N  --max-tokens N    (use 'none' to not send a value; the server default applies)
              --repetitions N  --shuffle | --no-shuffle  --shuffle-seed N  --timeout SECONDS
              --extra JSON                 extra request fields, e.g. "{\"cache_prompt\": false}"

            Other OpenAI compatible servers that are already running (e.g. TabbyAPI / ExLlamaV3): use 'run' with
            --server URL and --model NAME (the name for the leaderboard). A server with an API key gets it from the
            environment variable LLMBENCH_API_KEY (never stored in the run):
              LLMBENCH_API_KEY=... llmbench run v5-mymodel-exl3 --from v5-mymodel --server http://host:5000 --model mymodel-exl3
            """);
    }

    // ---- catalogs ----------------------------------------------------------------------

    public static int Catalogs(Workspace ws)
    {
        foreach (var f in ws.ListCatalogs())
        {
            var c = CatalogLoader.Load(ws, f);
            Console.WriteLine($"{f,-40} {c.Questions.Count,4} questions {c.VariantCount,4} variants  {(c.IsValid ? "ok" : $"{c.Errors.Count} ERRORS")}");
        }
        return 0;
    }

    public static int Validate(Workspace ws, Args a)
    {
        var files = a.Positional.Count > 0 ? a.Positional : ws.ListCatalogs().ToList();
        int bad = 0;
        foreach (var f in files)
        {
            var c = CatalogLoader.Load(ws, f);
            Console.WriteLine($"{c.RelativePath}: {c.Questions.Count} questions, {c.VariantCount} variants, sha256 {c.Sha256[..12]} - {(c.IsValid ? "OK" : "INVALID")}");
            foreach (var e in c.Errors) Console.WriteLine("  error:   " + e);
            foreach (var w in c.Warnings) Console.WriteLine("  warning: " + w);
            if (!c.IsValid) bad++;
        }
        return bad == 0 ? 0 : 1;
    }

    public static int ShowCatalog(Workspace ws, Args a)
    {
        var c = CatalogLoader.Load(ws, a.Pos(0, "catalog file"));
        foreach (var e in c.Errors) Console.WriteLine("error: " + e);
        var id = a.Get("id");
        foreach (var q in c.Questions.Where(q => id == null || q.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"== {q.Id}  [{q.Category ?? "-"}] tier {q.Tier?.ToString() ?? "-"}  {string.Join(" ", q.Tags.Select(t => "#" + t))}");
            Console.WriteLine("  base:");
            Console.WriteLine(Indent(q.Base.Question, "     "));
            if (q.Base.Options.Count > 0) Console.WriteLine("     answer list: " + string.Join(" | ", q.Base.Options.Select(o => o == q.Base.Answer ? "*" + o : o)));
            else Console.WriteLine("     answer: " + q.Base.Answer);
            foreach (var v in q.Variants)
            {
                Console.WriteLine($"  -- variant {v.Id}: {v.Kind}, {v.Type}{(v.IsChoice ? "" : ", " + (v.AnswerType ?? "text"))}, tier {v.Tier?.ToString() ?? "-"}{(v.FixedOrder ? ", fixed order" : "")}{(v.Image != null ? ", image " + v.Image : "")}");
                Console.WriteLine(Indent(v.Question, "     "));
                for (int i = 0; i < v.Options.Count; i++)
                    Console.WriteLine($"     {(v.Options[i].Id == v.CorrectOptionId ? "*" : " ")}{(char)('A' + i)}. {v.Options[i].Text}   [{v.Options[i].Id}]");
                if (!v.IsChoice) Console.WriteLine("     accepted: " + string.Join(" | ", v.AcceptedAnswers));
                if (v.Note != null) Console.WriteLine("     note: " + v.Note);
            }
            Console.WriteLine();
        }
        return 0;
    }

    public static int Overview(Workspace ws, Args a)
    {
        var c = CatalogLoader.Load(ws, a.Pos(0, "catalog file"));
        var outFile = a.Get("out") ?? Path.Combine(ws.Root, "docs", Path.GetFileNameWithoutExtension(c.FullPath) + "-overview.md");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, CatalogOverview.ToMarkdown(c));
        Console.WriteLine($"{c.Questions.Count} questions, {c.VariantCount} variants{(c.IsValid ? "" : $", {c.Errors.Count} ERRORS")} -> {outFile}");
        return c.IsValid ? 0 : 1;
    }

    // ---- server ------------------------------------------------------------------------

    public static async Task<int> Server(Workspace ws, Args a)
    {
        var url = a.Get("server") ?? (a.Positional.Count > 0 && ws.Runs.Exists(a.Positional[0]) ? ws.Runs.Load(a.Positional[0]).ServerUrl : ws.LoadRunDefaults().ServerUrl);
        using var client = new LlamaClient(url);
        var h = await client.GetHealthAsync(CancellationToken.None);
        Console.WriteLine($"Server {url}: {(h.Ok ? "OK" : "NOT READY")} {h.Message}");
        if (!h.Ok) return 1;
        var p = await client.GetPropsAsync(CancellationToken.None);
        if (p == null) { Console.WriteLine("/props not available"); return 0; }
        var gs = p["default_generation_settings"];
        var par = gs?["params"];
        Console.WriteLine($"  alias:       {p["model_alias"]}");
        Console.WriteLine($"  model path:  {p["model_path"]}");
        Console.WriteLine($"  build:       {p["build_info"]}");
        Console.WriteLine($"  context:     {gs?["n_ctx"]}   slots: {p["total_slots"]}");
        Console.WriteLine($"  modalities:  {p["modalities"]?.ToJsonString()}");
        Console.WriteLine($"  sampler:     temp {par?["temperature"]} top_k {par?["top_k"]} top_p {par?["top_p"]} min_p {par?["min_p"]} repeat_penalty {par?["repeat_penalty"]} seed {par?["seed"]}");
        Console.WriteLine($"  reasoning:   format {par?["reasoning_format"]}  chat_format {par?["chat_format"]}");
        Console.WriteLine($"  template:    {ChatTemplateInfo.Analyze(p["chat_template"]?.GetValue<string>()).Summary()}");
        var models = await client.GetModelsAsync(CancellationToken.None);
        Console.WriteLine($"  /v1/models:  {string.Join(", ", models)}");
        return 0;
    }

    // ---- run definitions ---------------------------------------------------------------

    public static int New(Workspace ws, Args a)
    {
        var name = a.Pos(0, "run name");
        if (Directory.Exists(ws.Runs.RunDir(name))) throw new BenchException($"Run '{name}' already exists.");
        var def = BuildDefinition(ws, name, a, null);
        ws.Runs.Save(def);
        Console.WriteLine($"Created run '{name}' ({ws.Runs.DefinitionPath(name)}).");
        return 0;
    }

    public static int Params(Workspace ws, Args a)
    {
        var name = a.Pos(0, "run name");
        Console.WriteLine(Json.Serialize(ws.Runs.Load(name)));
        return 0;
    }

    private static RunDefinition BuildDefinition(Workspace ws, string name, Args a, RunDefinition? existing)
    {
        RunDefinition def;
        if (existing != null) def = existing;
        else if (a.Get("from") is { } from) def = ws.Runs.Load(from).Clone();
        else def = ws.LoadRunDefaults();
        def.Name = name;
        if (existing == null) { def.CreatedAt = null; def.RestartOf = null; }
        RunOptions.Apply(def, a);
        var problems = def.Check();
        if (problems.Count > 0) throw new BenchException("Invalid parameters:\n  " + string.Join("\n  ", problems));
        return def;
    }

    public static int Preview(Workspace ws, Args a)
    {
        var def = ws.Runs.Load(a.Pos(0, "run name"));
        var plan = RunPlanner.Build(ws, def);
        Console.WriteLine($"{plan.Items.Count} planned items, {plan.Skipped.Count} skipped variants.");
        foreach (var s in plan.Skipped) Console.WriteLine($"  skipped {s.QuestionId}/{s.VariantId}: {s.Reason}");
        var idx = a.GetInt("index");
        foreach (var it in plan.Items.Where(i => idx == null || i.Index == idx))
        {
            Console.WriteLine($"---- #{it.Index} {it.Key} rep {it.Repetition} seed {it.Seed?.ToString() ?? "-"} expected: {it.Expected}");
            if (idx != null)
            {
                if (it.SystemMessage != null) Console.WriteLine("[system]\n" + it.SystemMessage + "\n");
                Console.WriteLine("[user]\n" + it.UserMessage);
                var req = RequestBuilder.Build(def, it.SystemMessage, it.UserMessage, it.Seed);
                Console.WriteLine("\n[request]\n" + Json.Pretty(req));
            }
        }
        return 0;
    }

    // ---- running -----------------------------------------------------------------------

    public static async Task<int> Run(Workspace ws, Args a)
    {
        var name = a.Pos(0, "run name");
        RunDefinition def;
        if (ws.Runs.Exists(name))
        {
            if (ws.Runs.HasResults(name))
                throw new BenchException($"Run '{name}' already has results. Use 'llmbench restart {name}' to run the same parameters again.");
            def = BuildDefinition(ws, name, a, ws.Runs.Load(name));
        }
        else def = BuildDefinition(ws, name, a, null);
        return await Execute(ws, def);
    }

    public static async Task<int> Restart(Workspace ws, Args a)
    {
        var name = a.Pos(0, "run name");
        var def = ws.Runs.CreateRestart(name);
        Console.WriteLine($"Restart of '{name}' as '{def.Name}'.");
        return await Execute(ws, def);
    }

    private static async Task<int> Execute(Workspace ws, RunDefinition def)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // second Ctrl+C kills the process
            e.Cancel = true;
            Console.WriteLine("\nStopping (Ctrl+C again to kill) ...");
            cts.Cancel();
        };
        return (await ExecuteCore(ws, def, cts.Token)).Code;
    }

    /// <summary>Runs a prepared definition and prints progress and the report. Code 0 = completed, 3 = stopped.</summary>
    private static async Task<(int Code, RunSummary? Summary)> ExecuteCore(Workspace ws, RunDefinition def, CancellationToken ct)
    {
        var runner = new BenchRunner(ws);
        var plan = runner.Prepare(def);
        foreach (var w in plan.Catalog.Warnings) Console.WriteLine("catalog warning: " + w);
        foreach (var s in plan.Skipped) Console.WriteLine($"skipped {s.QuestionId}/{s.VariantId}: {s.Reason}");
        if (plan.Items.Count == 0) throw new BenchException("Nothing to run: no question/variant matches the filters.");

        Console.WriteLine($"Run '{def.Name}': {plan.Items.Count} items, catalog {plan.Catalog.RelativePath}");
        var progress = new SyncProgress<RunEvent>(e => PrintEvent(e));
        RunSummary summary;
        try
        {
            summary = await runner.RunAsync(def, plan, progress, ct);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stopped during preflight; no results written.");
            return (3, null);
        }

        var res = RunResults.Load(ws.Runs.ResultsPath(def.Name));
        Console.WriteLine();
        Console.WriteLine(TextReport.Format(res.Header, def, summary));
        Console.WriteLine($"Results: {ws.Runs.ResultsPath(def.Name)}");
        return (summary.Completed ? 0 : 3, summary);
    }

    private static void PrintEvent(RunEvent e)
    {
        switch (e.Kind)
        {
            case RunEventKind.Log: Console.WriteLine(e.Message); break;
            case RunEventKind.Warning: WriteColored("WARNING: " + e.Message, ConsoleColor.Yellow); break;
            case RunEventKind.ItemStarted:
                Console.Write($"[{e.Item!.Index + 1,3}/{e.Total}] {e.Item.Key,-28} r{e.Item.Repetition} ");
                break;
            case RunEventKind.ItemCompleted:
                var r = e.Record!;
                var color = r.Status switch
                {
                    ItemStatus.Correct => ConsoleColor.Green,
                    ItemStatus.Incorrect => ConsoleColor.Red,
                    ItemStatus.Skipped => ConsoleColor.DarkGray,
                    _ => ConsoleColor.Yellow,
                };
                var old = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.Write($"{r.Status.ToString().ToLowerInvariant(),-10}");
                Console.ForegroundColor = old;
                var detail = r.Status == ItemStatus.Error ? r.Error : $"answer {Short(r.ExtractedAnswer, 20)} | expected {Short(r.Expected, 30)}";
                Console.WriteLine($" {detail}{(r.Reason != null && r.Status != ItemStatus.Error ? $" ({r.Reason})" : "")}  {r.CompletionTokens?.ToString() ?? "-"} tok  {r.ElapsedSeconds:0.0} s");
                break;
        }
    }

    // ---- reports -----------------------------------------------------------------------

    public static int List(Workspace ws)
    {
        var runs = ws.Runs.ListRuns();
        if (runs.Count == 0) { Console.WriteLine("No runs yet. Create one with 'llmbench new <name>' or 'llmbench run <name>'."); return 0; }
        Console.WriteLine($"{"Name",-30} {"State",-10} {"Model",-22} {"Items",7} {"Accuracy",9} {"95% CI",10}  Started");
        foreach (var r in runs)
        {
            var s = r.Summary;
            var ci = s?.CiLow != null ? $"{s.CiLow * 100:0}-{s.CiHigh * 100:0}%" : "";
            var model = string.IsNullOrEmpty(r.Definition.Model) ? r.Header?.ModelAlias ?? "" : r.Definition.Model;
            Console.WriteLine($"{r.Name,-30} {r.State.ToString().ToLowerInvariant(),-10} {Short(model, 22),-22} {(r.State == RunState.Draft ? "" : $"{r.ItemsDone}"),7} {TextReport.Pct(s?.Accuracy),9} {ci,10}  {r.StartedAt:yyyy-MM-dd HH:mm}");
        }
        return 0;
    }

    public static int Report(Workspace ws, Args a)
    {
        var name = a.Pos(0, "run name");
        var def = ws.Runs.Load(name);
        var res = RunResults.Load(ws.Runs.ResultsPath(name));
        if (res.Header == null) { Console.WriteLine($"Run '{name}' has no results yet."); return 1; }
        var summary = SummaryBuilder.Rebuild(res);
        if (res.Summary == null) Console.WriteLine("(run is incomplete - summary computed from the items written so far)");
        Console.WriteLine(TextReport.Format(res.Header, def, summary));
        foreach (var e in res.ReadErrors) Console.WriteLine("read error: " + e);
        return 0;
    }

    public static int Items(Workspace ws, Args a)
    {
        var name = a.Pos(0, "run name");
        var res = RunResults.Load(ws.Runs.ResultsPath(name));
        var status = a.Get("status");
        foreach (var r in res.Items.Where(i => status == null || i.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"#{r.Index,-4} {r.Key,-28} r{r.Repetition} {r.Status.ToString().ToLowerInvariant(),-10} answer {Short(r.ExtractedAnswer, 25),-25} expected {Short(r.Expected, 35),-35} {r.Reason}");
        return 0;
    }

    public static int Item(Workspace ws, Args a)
    {
        var name = a.Pos(0, "run name");
        var index = int.Parse(a.Pos(1, "item index"), CultureInfo.InvariantCulture);
        var res = RunResults.Load(ws.Runs.ResultsPath(name));
        var r = res.Items.FirstOrDefault(i => i.Index == index) ?? throw new BenchException($"Item #{index} not found in run '{name}'.");
        Console.WriteLine($"#{r.Index} {r.Key} rep {r.Repetition} seed {r.Seed?.ToString() ?? "-"}");
        Console.WriteLine($"status: {r.Status.ToString().ToLowerInvariant()} {r.Reason}   answer: {r.ExtractedAnswer}   expected: {r.Expected}");
        Console.WriteLine($"finish: {r.FinishReason}   tokens: {r.PromptTokens} in / {r.CompletionTokens} out   {r.ElapsedSeconds:0.0} s{(r.Error != null ? "   error: " + r.Error : "")}");
        if (r.SystemMessage != null) Console.WriteLine("\n[system]\n" + r.SystemMessage);
        Console.WriteLine("\n[user]\n" + r.UserMessage);
        if (!string.IsNullOrEmpty(r.ReasoningContent)) Console.WriteLine("\n[reasoning]\n" + r.ReasoningContent);
        Console.WriteLine("\n[content]\n" + r.Content);
        return 0;
    }

    public static int Compare(Workspace ws, Args a)
    {
        var na = a.Pos(0, "run A");
        var nb = a.Pos(1, "run B");
        var ra = RunResults.Load(ws.Runs.ResultsPath(na));
        var rb = RunResults.Load(ws.Runs.ResultsPath(nb));
        var r = RunComparer.Compare(ws, na, ra, nb, rb);
        Console.WriteLine(RunComparer.Format(r));
        return 0;
    }

    public static int LeaderboardCmd(Workspace ws, Args a)
    {
        var groups = Leaderboard.Build(ws);
        if (groups.Count == 0) { Console.WriteLine("No runs with results."); return 0; }
        bool all = a.Has("all");
        var model = a.Get("model");
        var only = a.Get("group");
        foreach (var g in groups.Where(g => only == null || g.QuestionSetHash.StartsWith(only, StringComparison.OrdinalIgnoreCase)))
        {
            var entries = g.Entries
                .Where(e => all || e.State == RunState.Completed)
                .Where(e => model == null || e.Model.Contains(model, StringComparison.OrdinalIgnoreCase))
                .Where(e => NameMatches(e, a.Get("name")))
                .ToList();
            if (entries.Count == 0) continue;
            var sorted = Leaderboard.AssignRanks(entries);
            Console.WriteLine($"== {g.Title}");
            var varying = Leaderboard.VaryingParameters(entries);
            Console.WriteLine("   varying parameters: " + (varying.Count == 0 ? "(none)" : string.Join(", ", varying)));
            if (g.Calibration != null)
                Console.WriteLine($"   Score scale: calibrated {g.Calibration.CreatedAt:yyyy-MM-dd} from {g.Calibration.Runs.Count} runs ({g.Calibration.Items.Count} items, difficulties frozen)" +
                                  (g.Calibration.ReferenceRun != null ? $"; strength 1 = {g.Calibration.ReferenceRun}" : ""));
            Console.WriteLine($"   {"#",3} {"Rank",4} {"Run",-26} {"Model",-18} {"Accuracy",9} {"95% CI",10} {"Score",10} {"Strength",17} {"inv+tr",7} {"tok/ans",7} {"s/ans",6} {"tok/s",6} {"time",10} {"Thinking",-11} {"Prompt",-20} {"Instr",-8} {"Temp",5} {"Reps",4}  Note / significantly worse than");
            foreach (var e in sorted)
            {
                var s = e.Summary;
                var ci = s.CiLow.HasValue ? $"{s.CiLow * 100:0}-{s.CiHigh * 100:0}%" : "";
                var note = string.Join("; ", new[] { e.UsabilityNote, e.SignificantlyWorseThan.Count > 0 ? "worse than " + string.Join(", ", e.SignificantlyWorseThan) : "" }.Where(x => x.Length > 0));
                Console.WriteLine($"   {e.Position,3} {e.Rank?.ToString() ?? "-",4} {Short(e.RunName, 26),-26} {Short(e.Model, 18),-18} {TextReport.Pct(s.Accuracy),9} {ci,10} {ScoreText(e.Ability),10} {StrengthText(e.Ability),17} {TextReport.Pct(e.FormatFailureRate),7} {e.TokensPerAnswer?.ToString("0", CultureInfo.InvariantCulture) ?? "-",7} {e.SecondsPerAnswer?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-",6} {e.TokensPerSecond?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-",6} {RunTiming.Duration(e.ModelSeconds),10} {Short(e.Thinking, 11),-11} {Short(e.SystemPrompt, 20),-20} {e.Definition.AnswerInstruction,-8} {e.Definition.Temperature?.ToString("0.##") ?? "def",5} {e.Definition.Repetitions,4}  {note}");
            }
            Console.WriteLine();
            // practical view: accuracy if the answer had to be short / quick (computed from the stored answers)
            var tokHeads = string.Concat(BudgetStat.TokenLimits.Select(l => $"{"≤" + l + " tok",10}"));
            var secHeads = string.Concat(BudgetStat.SecondLimits.Select(l => $"{"≤" + l + " s",8}"));
            Console.WriteLine($"   budget {"Run",-26} {"Accuracy",9} {"Net",8} {"net/min",7} {"med tok",8} {"med s",7} {"tok/corr",8}{tokHeads}{secHeads}  Pareto");
            foreach (var e in sorted.Where(e => e.Summary.Budget != null))
            {
                var b = e.Summary.Budget!;
                var tok = string.Concat(b.AccuracyAtTokens.Select(x => $"{TextReport.Pct(x),10}"));
                var sec = string.Concat(b.AccuracyWithinSeconds.Select(x => $"{TextReport.Pct(x),8}"));
                Console.WriteLine($"   {e.Position,6} {Short(e.RunName, 26),-26} {TextReport.Pct(e.Summary.Accuracy),9} {TextReport.Pct(b.NetScore),8} {b.NetPerMinute?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-",7} {b.MedianTokens.ToString("0", CultureInfo.InvariantCulture),8} {b.MedianSeconds.ToString("0.0", CultureInfo.InvariantCulture),7} {b.TokensPerCorrect?.ToString("0", CultureInfo.InvariantCulture) ?? "-",8}{tok}{sec}  {(e.Pareto ? "yes" : "")}");
            }
            Console.WriteLine();
        }
        Console.WriteLine("# = position by accuracy. Rank = 1 + number of runs that are significantly better in the paired");
        Console.WriteLine("test over the same questions (McNemar, p < 0.05); equal rank = difference not proven.");
        Console.WriteLine("tok/ans = output tokens per answer (hardware-independent); s/ans, tok/s and time (sum of the request");
        Console.WriteLine("times) depend on the machine - compare them only between runs on the same server with the same settings.");
        Console.WriteLine("budget: ≤N tok = accuracy if every answer had to fit into N output tokens (thinking included; at temperature 0");
        Console.WriteLine("exactly what a run with max_tokens N would score); ≤N s = answered correctly within N seconds on this machine.");
        Console.WriteLine("Net = value per answer: correct +1, wrong -1 (wrong choice -1/(options-1), so guessing is worth 0),");
        Console.WriteLine("invalid/truncated 0; net/min = net value per minute of request time on this machine.");
        Console.WriteLine("tok/corr = output tokens per correct answer (compare only models of similar accuracy). Pareto = no other run");
        Console.WriteLine("shown is at least as accurate with at most as many median tokens (and better in one of them).");
        Console.WriteLine("Score = ability on a fixed scale (Rasch model, see 'llmbench calibrate'): like Elo, equal differences mean the same");
        Console.WriteLine("everywhere; +400 = 10 times the odds of solving a question; 1000 = solves an average question half of the time.");
        Console.WriteLine("Hard questions count more, easy ones and guessing (choice questions) little. ± = 95 % range.");
        Console.WriteLine("Strength = how many times the odds (correct : wrong) of solving a question compared with the reference run");
        Console.WriteLine("(84× = 84 times the odds); a ratio scale: 84× vs 35× = 2.4 times the odds. Range in brackets = 95 %.");
        return 0;
    }

    private static string StrengthText(Ability? a) =>
        a?.Strength == null ? "-" : $"{Rasch.FormatStrength(a.Strength)}× ({Rasch.FormatStrength(a.StrengthLow)}-{Rasch.FormatStrength(a.StrengthHigh)})";

    private static string ScoreText(Ability? a) =>
        a == null ? "-" : $"{a.Score.ToString("0", CultureInfo.InvariantCulture)} ±{a.Score95.ToString("0", CultureInfo.InvariantCulture)}";

    public static int QuestionStatsCmd(Workspace ws, Args a)
    {
        var groups = Leaderboard.Build(ws);
        var only = a.Get("group");
        var g = only == null ? groups.FirstOrDefault() : groups.FirstOrDefault(x => x.QuestionSetHash.StartsWith(only, StringComparison.OrdinalIgnoreCase));
        if (g == null) { Console.WriteLine("No matching leaderboard group (see 'llmbench leaderboard' for the hashes)."); return 1; }
        var entries = g.Entries.Where(e => (a.Has("all") || e.State == RunState.Completed) && NameMatches(e, a.Get("name"))).OrderBy(e => e.RunName).ToList();
        var rows = QuestionStats.Build(entries);
        var filter = a.Get("filter");
        Console.WriteLine($"== {g.Title}");
        Console.WriteLine($"   runs: {string.Join(", ", entries.Select((e, i) => $"[{i + 1}] {e.RunName}"))}");
        Console.WriteLine($"   {"Question",-22} {"T",1} {"Category",-15} {"Solved",7} {"Mean",6}  {string.Join(" ", entries.Select((e, i) => $"{i + 1,3}"))}  Calibration / expected");
        foreach (var r in rows)
        {
            if (filter == "all" && r.Calibration != "all solve") continue;
            if (filter == "none" && r.Calibration != "none solves") continue;
            if (filter == "separates" && r.Calibration != "separates") continue;
            var cells = string.Join(" ", entries.Select(e => r.RateByRun.TryGetValue(e.RunName, out var v) ? (v >= 1 ? "  ✔" : v <= 0 ? "  ✘" : $"{v * 100,3:0}") : "  -"));
            Console.WriteLine($"   {r.Key,-22} {r.Tier?.ToString() ?? "-",1} {r.Category ?? "-",-15} {$"{r.SolvedBy}/{r.Runs}",7} {TextReport.Pct(r.MeanRate),6}  {cells}  {r.Calibration,-12} {Short(r.Expected, 40)}");
        }
        Console.WriteLine();
        Console.WriteLine($"{rows.Count(r => r.Calibration == "all solve")} questions solved by all runs, {rows.Count(r => r.Calibration == "none solves")} by none, {rows.Count(r => r.Calibration == "separates")} separate the runs.");
        return 0;
    }

    public static int Calibrate(Workspace ws, Args a)
    {
        var groups = Leaderboard.Build(ws);
        var only = a.Get("group");
        var g = only == null ? groups.FirstOrDefault() : groups.FirstOrDefault(x => x.QuestionSetHash.StartsWith(only, StringComparison.OrdinalIgnoreCase));
        if (g == null) { Console.WriteLine("No matching leaderboard group (see 'llmbench leaderboard' for the hashes)."); return 1; }
        // only completed runs: an incomplete run answered a non-random part of the questions
        var entries = g.Entries.Where(e => e.State == RunState.Completed && NameMatches(e, a.Get("name"))).OrderBy(e => e.RunName).ToList();
        var cal = Rasch.Calibrate(entries.Select(e => (e.RunName, e.Model, e.Items)).ToList(), g.QuestionSetHash, string.Join(", ", g.CatalogFiles), a.Get("reference"));
        var outFile = Path.GetFullPath(a.Get("out") ?? Calibration.DefaultPath(ws, g.CatalogFiles[0], g.QuestionSetHash));
        if (File.Exists(outFile)) Console.WriteLine($"Replacing {outFile} - scores of all runs of this question set are recomputed with the new difficulties.");
        cal.Save(outFile);
        Console.WriteLine($"== {g.Title}");
        Console.WriteLine($"Calibrated {cal.Items.Count} question/variant pairs from {entries.Count} runs -> {outFile}");
        if (cal.ReferenceRun != null) Console.WriteLine($"Strength reference (= 1): {cal.ReferenceRun}");
        Console.WriteLine($"   {"Run",-26} {"Score",6} {"95%",5} {"Strength",17}");
        foreach (var r in cal.Runs.OrderByDescending(r => r.Theta))
            Console.WriteLine($"   {Short(r.Run, 26),-26} {r.Score,6:0} {"±" + (1.96 * Rasch.PointsPerLogit * r.StandardError).ToString("0", CultureInfo.InvariantCulture),5} {StrengthText(new Ability(r.Theta, r.StandardError, 0, cal.ReferenceTheta)),14}");
        Console.WriteLine("Mean difficulty per tier (logits, higher = harder, 0 = average item):");
        foreach (var t in cal.Items.GroupBy(i => i.Tier).OrderBy(t => t.Key))
            Console.WriteLine($"   tier {t.Key?.ToString() ?? "-"}: {t.Average(i => i.Difficulty).ToString("0.00", CultureInfo.InvariantCulture),6}  ({t.Count()} items)");
        return 0;
    }

    private static bool NameMatches(LeaderboardEntry e, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || e.RunName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public static int ExportForLlm(Workspace ws, Args a)
    {
        var groups = Leaderboard.Build(ws);
        var only = a.Get("group");
        var g = only == null ? groups.FirstOrDefault() : groups.FirstOrDefault(x => x.QuestionSetHash.StartsWith(only, StringComparison.OrdinalIgnoreCase));
        if (g == null) { Console.WriteLine("No matching leaderboard group (see 'llmbench leaderboard' for the hashes)."); return 1; }
        var entries = g.Entries.Where(e => (a.Has("all") || e.State == RunState.Completed) && NameMatches(e, a.Get("name"))).ToList();
        var text = LlmExport.Build(ws, g, entries);
        var outFile = a.Get("out") ?? Path.Combine(ws.Root, "exports", $"llm-export_{g.QuestionSetHash[..8]}_{DateTime.Now:yyyyMMdd-HHmm}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, text);
        Console.WriteLine($"Exported {entries.Count} runs, {g.QuestionCount} question/variant pairs to {outFile} ({text.Length / 1024} KB).");
        return 0;
    }

    public static int Rescore(Workspace ws, Args a)
    {
        bool dry = a.Has("dry-run");
        var names = a.Has("all") ? ws.Runs.ListNames().Where(ws.Runs.HasResults).ToList() : [a.Pos(0, "run name (or --all)")];
        foreach (var name in names)
        {
            RescoreResult r;
            try { r = Rescorer.Rescore(ws, name, dry); }
            catch (BenchException ex) when (a.Has("all"))
            {
                // --all: an unfinished run (e.g. still running on another machine) must not stop the others
                Console.WriteLine($"{name}: skipped - {ex.Message}");
                continue;
            }
            Console.WriteLine($"{name}: {r.Changes.Count} change(s){(dry ? " (dry run, nothing written)" : "")} - accuracy {TextReport.Pct(r.OldSummary?.Accuracy)} -> {TextReport.Pct(r.NewSummary?.Accuracy)}, " +
                              $"invalid {r.OldSummary?.Invalid} -> {r.NewSummary?.Invalid}");
            foreach (var c in r.Changes)
                Console.WriteLine($"   {c.Key,-24} r{c.Repetition}  {Status(c.OldStatus, c.OldReason),-22} -> {Status(c.NewStatus, c.NewReason),-12} answer {Short(c.Answer, 40)}");
            if (r.BackupFile != null) Console.WriteLine($"   backup: {r.BackupFile}");
        }
        return 0;

        static string Status(ItemStatus s, string? reason) => s.ToString().ToLowerInvariant() + (reason != null ? $":{reason}" : "");
    }

    public static int Merge(Workspace ws, Args a)
    {
        var name = a.Pos(0, "name of the new run");
        var catalog = a.Get("catalog") ?? throw new UsageException("Missing option: --catalog FILE");
        var sources = a.Positional.Skip(1).ToList();
        var s = RunMerger.Merge(ws, name, catalog, sources);
        Console.WriteLine($"{name}: {s.Done} items from {string.Join(" + ", sources)} - accuracy {TextReport.Pct(s.Accuracy)}");
        return 0;
    }

    public static int ExportCsv(Workspace ws, Args a)
    {
        var groups = Leaderboard.Build(ws);
        var only = a.Get("group");
        var g = only == null ? groups.FirstOrDefault() : groups.FirstOrDefault(x => x.QuestionSetHash.StartsWith(only, StringComparison.OrdinalIgnoreCase));
        if (g == null) { Console.WriteLine("No matching leaderboard group (see 'llmbench leaderboard' for the hashes)."); return 1; }
        var entries = g.Entries.Where(e => (a.Has("all") || e.State == RunState.Completed) && NameMatches(e, a.Get("name"))).ToList();
        if (entries.Count == 0) { Console.WriteLine("No runs match."); return 1; }
        var runs = Leaderboard.AssignRanks(entries);
        var catalog = ResultsCsv.MatchingCatalog(ws, runs);
        bool withQuestion = !a.Has("no-question");
        var outFile = Path.GetFullPath(a.Get("out") ?? Path.Combine(ws.Root, "exports", $"results_{g.QuestionSetHash[..8]}_{DateTime.Now:yyyyMMdd-HHmm}.csv"));
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllText(outFile, ResultsCsv.Build(runs, catalog, withQuestion), new System.Text.UTF8Encoding(false));
        var legend = Path.ChangeExtension(outFile, null) + "-legend.txt";
        File.WriteAllText(legend, ResultsCsv.BuildLegend(runs, Path.GetFileName(outFile), catalog, withQuestion));
        var briefing = Path.ChangeExtension(outFile, null) + "-briefing.md";
        File.WriteAllText(briefing, LlmBriefing.Build(runs, catalog, Path.GetFileName(outFile), Path.GetFileName(legend)));
        Console.WriteLine($"{runs.Count} runs: {string.Join(", ", runs.Select((r, i) => $"{i + 1}={r.RunName}"))}");
        Console.WriteLine($"-> {outFile}");
        Console.WriteLine($"-> {legend}");
        Console.WriteLine($"-> {briefing}");
        Console.WriteLine($"Hand over: {runs[0].Header.CatalogFile} + these three files.");
        return 0;
    }

    public static async Task<int> ProbeThinking(Workspace ws, Args a)
    {
        var def = a.Positional.Count > 0 ? ws.Runs.Load(a.Positional[0]).Clone() : new RunDefinition();
        RunOptions.Apply(def, a);
        using var client = new LlamaClient(def.ServerUrl);
        Console.WriteLine($"Server {def.ServerUrl}: sending the probe question with ON fragment {def.ThinkingOn.ToJsonString()} and OFF fragment {def.ThinkingOff.ToJsonString()} ...");
        var r = await ThinkingProbe.RunAsync(client, def, CancellationToken.None);
        Console.WriteLine($"ON : reasoning {r.OnReasoningChars} chars, {r.OnCompletionTokens?.ToString() ?? "?"} tokens{(r.OnThinkInContent ? ", <think> in content" : "")}{(r.OnError != null ? ", error " + r.OnError : "")}");
        Console.WriteLine($"OFF: reasoning {r.OffReasoningChars} chars, {r.OffCompletionTokens?.ToString() ?? "?"} tokens{(r.OffThinkInContent ? ", <think> in content" : "")}{(r.OffError != null ? ", error " + r.OffError : "")}");
        Console.WriteLine($"{(r.Verified ? "VERIFIED" : "NOT VERIFIED")}: {r.Note}");
        return r.Verified ? 0 : 1;
    }

    // ---- helpers -----------------------------------------------------------------------

    private static string Short(string? s, int max)
    {
        var t = (s ?? "-").Replace("\r", " ").Replace("\n", " ");
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }

    private static string Indent(string text, string prefix) =>
        string.Join("\n", text.Split('\n').Select(l => prefix + l));

    private static void WriteColored(string text, ConsoleColor color)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }
}

/// <summary>IProgress that invokes the handler synchronously (Progress&lt;T&gt; would post to the thread pool and reorder output).</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly object _lock = new();
    public void Report(T value) { lock (_lock) handler(value); }
}
