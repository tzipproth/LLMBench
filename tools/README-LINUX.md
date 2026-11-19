# LLMBench – Linux command line package

Self-contained: no .NET installation needed. Runs on the machine that also runs `llama-server`.

## Install (once, after copying the folder)

```bash
cd ~/llmbench-linux
chmod +x llmbench          # WinSCP may drop the executable bit
./llmbench --help
```

The folder is the workspace: `catalogs/`, `prompts/`, `run-defaults.json` (server `http://127.0.0.1:8081`, thinking off,
temperature 0, model name taken from the server) and `runs/` (results).

## A run

```bash
./llmbench server                                   # which model is loaded? (alias, context, thinking switch)
./llmbench run v5-gemma4-12b                       # full catalog core-v5 (the default, 662 requests)
./llmbench run test-gemma4-12b --limit 20          # quick test with the first 20 questions
```

Run name convention: `v5-<model alias>`, e.g. `v5-gemma4-12b`.
Models without a thinking switch (`./llmbench server` shows "no enable_thinking", e.g. Mistral 7B): add
`--thinking default`, otherwise the thinking check marks the run as not usable.
The run refuses to start if a different model is loaded than the name in the run (empty = taken from the server).

The run shows its progress in the console; Ctrl+C stops it cleanly (the results written so far are kept).
To keep it running after the ssh session ends, start it inside `tmux` (or `screen`). Without tmux:

```bash
nohup ./llmbench run v5-gemma4-12b > v5-gemma4-12b.log 2>&1 &
tail -f v5-gemma4-12b.log                           # watch (Ctrl+C only stops watching)
```

Note the `./` in front of `llmbench` (program in the current folder); `/llmbench` gives "command not found" (exit 127).

A server that is already running and needs an API key (e.g. TabbyAPI / ExLlamaV3): pass the key in the
environment variable `LLMBENCH_API_KEY` (it is not stored in the run) and give the model its own name:

```bash
LLMBENCH_API_KEY=<key> ./llmbench run v5-qwen3.8-27b-exl3 --model qwen3.8-27b-exl3
```

Other useful commands: `./llmbench list`, `./llmbench report <run>`, `./llmbench items <run> --status incorrect`.

## All models unattended (auto)

`auto/models.json` lists every model: the llama-server path, the server arguments of each model (exactly as on the
command line, without the `\` line endings) and optionally extra run options (`"bench": "--max-tokens 9000"`).
`"enabled": false` leaves a model out. `"serverExe"` inside a model uses a different llama-server for this model only
(phi-4 runs with the older build in `/home/thomas/llama/`, because `llama-new` crashes while loading it).

```bash
tmux new -s bench
./llmbench auto auto/models.json                                # full core-v5, runs v5-<alias>
./llmbench auto auto/models.json --only gemma4-12b,phi-4-14b    # only these models
```

For every model: start llama-server, wait until `/health` says ok (model loaded), run the benchmark, stop the
server, next model. Before starting, nothing may run on the port (stop your own llama-server first).
- Models without `enable_thinking` in their chat template run with thinking `default` automatically.
- A model whose server does not start, or crashes during the run, is reported and skipped; the others continue.
- Completed runs are skipped: after an interruption simply start the same command again. An unfinished run is
  repeated as `<name>_01`.
- Ctrl+C stops the current run cleanly, stops the server and prints the summary.
- Server output: `runs/_auto/<date-time>/<alias>.server.log`; summary table: `.../summary.txt`.

Copying a new package over the folder also replaces `auto/models.json`; keep a copy if you changed it on Linux.

## Copy the result back

Copy the whole run folder `runs/<run name>/` (contains `run.json` and `results.jsonl`) to `runs/` of the Windows
workspace. The GUI and the Windows CLI then show it like any local run.
