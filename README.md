# LLMBench

**A general intelligence benchmark for LLMs that run completely in the VRAM of a 16 GB graphics card.**

**→ [Leaderboard](https://tzipproth.github.io/LLMBench/leaderboard.html)** · [Heatmap of every answer](https://tzipproth.github.io/LLMBench/heatmap.html) · [All questions](docs/core-v5-overview.md)

## Why

Most leaderboards rank models through cloud APIs or on hardware nobody has at home. If you own one consumer
graphics card with 16 GB, the questions are different:

- **Which model that fits completely into 16 GB of VRAM thinks best?** Not recalls best, not writes the nicest
  text, but actually reasons.
- **How much good answering do I get per minute?** A model that is slightly better but needs ten times as long is
  often the worse choice in daily use.

LLMBench answers both on real hardware. Every model runs locally in `llama-server` (llama.cpp) on the same
**RTX 5070 Ti (16 GB)**, completely in VRAM, with the same settings, and answers the same 662 questions. Every
single answer is stored and published in this repository, so every number on the leaderboard can be checked.

## Current results

| # | Model | Strength | Quality per time | Accuracy | s / answer |
|---:|---|---:|---:|---:|---:|
| 1 | qwen3.8-27b | **64×** | 2.6 / min | 92.4 % | 19.8 |
| 2 | gemma4-26b-a4b | **48×** | 9.1 / min | 90.6 % | 5.5 |
| 3 | gemma4-12b | **33×** | 12.3 / min | 88.1 % | 3.8 |
| 4 | qwen3.5-9b | **20×** | 3.3 / min | 83.7 % | 13.5 |
| 5 | muse-glimmer-30b | **15×** | 1.2 / min | 81.3 % | 39.8 |
| 6 | gpt-oss-20b | **14×** | 4.2 / min | 79.8 % | 9.9 |
| 7 | gemma4-e4b | **10×** | 5.5 / min | 76.1 % | 6.2 |
| 8 | ministral3-14b | **7.5×** | 1.9 / min | 72.7 % | 17.4 |
| 9 | phi-4-14b | **7.4×** | 6.0 / min | 71.8 % | 5.0 |
| 10 | granite-4.2-8b | **4.9×** | 1.0 / min | 66.6 % | 32.1 |
| 11 | llama3.1-8b | **1.0×** | 0.2 / min | 41.1 % | 10.7 |
| 12 | mistral-7b-v0.3 | **0.38×** | −8.4 / min | 27.5 % | 1.7 |

The [leaderboard](https://tzipproth.github.io/LLMBench/leaderboard.html) has the 95 % ranges, significance ranks, model files and an explanation of
every column. In short:

- **Strength**: how many times higher the odds (right : wrong) of solving a question are than those of
  Llama 3.1 8B (= 1×). On a question that Llama 3.1 8B solves half of the time, a model with 64× is right 98 % of
  the time. Ratios are meaningful: 40× has twice the odds of 20×.
- **Quality per time**: right minus wrong answers per minute on the RTX 5070 Ti. Wrong answers cost, so fast
  guessing does not pay (see Mistral 7B).
- **Accuracy**: share of correct answers. Same order as the strength, but it squeezes the top, where every further
  percent is much harder to get.

## The questions

528 questions, 662 variants (436 free answer, 226 multiple choice). They are written so that a model has to think:
some are well-known puzzles with one detail changed, so the memorized answer is wrong; many need a mental picture
of a room, a stack of objects or a folded sheet of paper.

| Category | Variants | Example of what is tested |
|---|---:|---|
| spatial | 91 | left/right after turning, views from another position, folding |
| physical | 59 | what happens to objects, liquids, balances |
| tracking | 55 | where an object is after several moves |
| room puzzles | 45 | one closed scene, deduce a fact from the description |
| scenes | 44 | everyday situations with an obvious but wrong answer |
| altered classics | 42 | famous riddles with a twist (river crossing, Monty Hall, …) |
| logic | 38 | deduction, truth tellers, constraints |
| math, arithmetic | 68 | word problems, exact calculation |
| temporal | 32 | dates, durations, order of events |
| social | 21 | who knows or believes what |
| long context, planning, characters | 33 | long texts, schedules, letter counting |

Questions are grouped into nine tiers by the round in which they were written, from easy anchors (tier 1) to the
hardest questions (tier 9). Each question has a base and one or more variants, e.g. the original puzzle as a control
and an altered version as the trap. [docs/core-v5-overview.md](docs/core-v5-overview.md) lists all of them with
their solutions; the catalog is [catalogs/core-v5.json](catalogs/core-v5.json).

Most questions were written for LLMBench, by hand or proposed by LLMs and then checked. The `source` field in the
catalog names the origin of every question.

## How a run works

- One request per question variant, nothing else in the context: a system message and the question.
- System prompt ([prompts/neutral.txt](prompts/neutral.txt)):
  `You are a helpful assistant. Answer the question accurately. You may reason first. End your reply with a last
  line "Final Answer:" followed by your answer.`
- Multiple-choice options are added as `A. … B. …`, shuffled with a fixed seed, so every model sees the same order.
- Temperature 0, seed 42, at most 7,000 output tokens. Models with a thinking switch run with thinking off, the
  others with their default. The exact `llama-server` arguments of every model are in
  [auto/models.json](auto/models.json).
- **Scoring** reads only the final answer, never the reasoning: the last line starting with `Final Answer:`
  (fallbacks: `Final Answer:` within a line, a line starting with `Answer:`, a single short line). Numbers are
  compared as numbers, options by letter or text. Answers cut off at the token limit count as wrong; models with
  5 % or more of them are marked on the leaderboard.

### Strength scale

Accuracy depends on how easy the questions are and compresses the differences at the top. LLMBench therefore fits a
Rasch model (item response theory) to the answers of all models once:
P(correct) = c + (1 − c) / (1 + e^(b − θ)), with θ = ability of the model, b = difficulty of the question variant and
c = 1/options for multiple choice (guessing floor), 0 for free answers. The difficulties are then frozen
([calibrations/](calibrations/)), so a model added later lands on the same scale and the values of the earlier
models never change. Strength = e^(θ − θ<sub>Llama 3.1 8B</sub>).

## Tools

All tools share one library and work on the same folder (the *workspace*: `catalogs/`, `prompts/`, `runs/`, …).

| Tool | What it does |
|---|---|
| `llmbench` (CLI, Windows and Linux) | create and run benchmark runs, list, report, compare, leaderboard, rescore, calibrate, merge, exports |
| `LLMBenchGui` (Windows) | the same with a GUI: tabs *Runs*, *Leaderboard*, *Compare*, *Catalogs*, *System prompts* |
| `llmbench-heatmap` | writes [docs/leaderboard.html](https://tzipproth.github.io/LLMBench/leaderboard.html) and [docs/heatmap.html](https://tzipproth.github.io/LLMBench/heatmap.html) (plus a PNG of the heatmap) |
| `tools/build-linux-package.ps1` | self-contained Linux build of the CLI for the machine that runs `llama-server` ([tools/README-LINUX.md](tools/README-LINUX.md)) |

Build (requires the [.NET 10 SDK](https://dotnet.microsoft.com/download)):

```bash
dotnet build LLMBench.slnx
dotnet test
```

On Windows, `llmbench.cmd <command>` runs the CLI from the project folder; the GUI is
`src\LLMBench.Gui\bin\Debug\net10.0-windows\LLMBenchGui.exe`.

### Test your own model

1. Start `llama-server` with the model (it has to fit completely into VRAM) and check it:
   `llmbench server --server http://127.0.0.1:8081`
2. Run all 662 questions: `llmbench run v5-<model> --server http://127.0.0.1:8081`
   (add `--thinking default` if the model has no thinking switch). The run refuses to start if the server runs a
   different model than the run name says.
3. `llmbench leaderboard` shows the model next to the others, already on the frozen strength scale.
4. `llmbench-heatmap` regenerates the heatmap and the leaderboard page.

Several models unattended: list them in `auto/models.json` and run `llmbench auto auto/models.json`. For every model
it starts `llama-server`, waits until the model is loaded, runs the benchmark, stops the server and continues with the
next one; completed runs are skipped.

### CLI quick reference

```text
llmbench server [--server URL]                          check server, show /props
llmbench new|run <name> [options]                       create / start a run (options: llmbench --help)
llmbench preview <name> [--index N]                     composed prompt and request, nothing sent
llmbench list | report <name> | items <name> --status incorrect | item <name> 5
llmbench compare <runA> <runB>                          question by question, McNemar test, parameter differences
llmbench leaderboard [--all] [--model M]                ranking of all runs that ask the same questions
llmbench rescore <run>|--all [--dry-run]                score stored replies again with the current rules
llmbench calibrate --group HASH --reference RUN         freeze question difficulties (only for a new catalog version)
llmbench merge <name> <run> <run> --catalog FILE        combine runs on parts of a catalog into one run
llmbench validate | show <catalog> [--id ID] | overview <catalog>
llmbench export-csv | export-for-llm | question-stats   exports and per-question statistics
llmbench auto auto/models.json [--only a,b]             all models unattended
```

Ctrl+C stops a run cleanly; the answers so far are kept.

## Repository layout

```text
catalogs/core-v5.json        the questions (base + variants)
prompts/neutral.txt          system prompt
runs/v5-<model>/             one folder per model: run.json (parameters) + results.jsonl (every request and reply)
calibrations/                frozen question difficulties of the strength scale
docs/                        leaderboard, heatmap, overview of all questions
auto/models.json             llama-server arguments of every model
run-defaults.json            parameters every new run starts with
src/, tests/, tools/         source code, unit tests, Linux package script
```

Runs are grouped by their *question set* (a hash over every asked question, its options and solution), so only runs
that answered exactly the same questions are ranked together. A run with results is never changed again, except by
`rescore` (which keeps a backup).

### Catalog format

```json
{ "id": "CS-101", "category": "altered_classic", "tier": 2, "tags": [],
  "base": { "question": "Sally has 3 brothers. Each of her brothers has 2 sisters. How many sisters does Sally have?",
            "answerType": "number", "answer": "1" },
  "variants": [
    { "id": "free", "format": "free" },
    { "id": "altered-free", "kind": "altered", "format": "free",
      "question": "Sally has 3 brothers. Each of her brothers has 3 sisters. How many sisters does Sally have?", "answer": "2" } ] }
```

A variant states only what differs from the base. `format`: `choice` (options from the base answer list) or `free`.
`kind`: `original`, `altered` (one detail changed so the memorized answer is wrong) or `distractor` (an irrelevant
sentence added); original + altered form a control pair.
