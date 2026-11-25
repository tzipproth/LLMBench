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

LLMBench answers both on real hardware. Every model runs locally in `llama-server` (llama.cpp; EXL3 quantizations in TabbyAPI / ExLlamaV3) on the same
**RTX 5070 Ti (16 GB)** and answers the same 662 question variants. The leaderboard marks runs that use the
native thinking setup described below. Dense models have to fit
completely into VRAM. MoE models that are too large may keep part of their experts in system RAM (64 GB DDR5,
Ryzen 9); only a few experts are used per token, so they stay fast. The leaderboard marks them with **MoE · xx % RAM** (share of the weights in RAM). Every
single answer is stored and published in this repository, so every number on the leaderboard can be checked.

## Current results

| # | Model | Strength | Quality per time | Accuracy | s / answer |
|---:|---|---:|---:|---:|---:|
| 1 | daisyrose-27b (Native thinking) | **109×** | 2.6 / min | 95.0 % | 21.3 |
| 2 | qwen3.8-27b-exl3 (EXL3 4.00 bpw, TabbyAPI) | **72×** | 2.6 / min | 93.1 % | 20.3 |
| 3 | inkling2-mini (MoE, six expert layers in RAM) | **64×** | 1.6 / min | 92.6 % | 34.1 |
| 4 | qwen3.8-27b | **64×** | 2.6 / min | 92.4 % | 19.8 |
| 5 | gemma4-26b-a4b (MoE, 17 % RAM) | **48×** | 9.1 / min | 90.6 % | 5.5 |
| 6 | qwen3.6-35b-a3b (MoE, 32 % RAM) | **35×** | 3.8 / min | 88.5 % | 12.9 |
| 7 | gemma4-12b | **33×** | 12.3 / min | 88.1 % | 3.8 |
| 8 | qwen3.5-9b | **20×** | 3.3 / min | 83.7 % | 13.5 |
| 9 | muse-glimmer-30b | **15×** | 1.2 / min | 81.3 % | 39.8 |
| 10 | gpt-oss-20b | **14×** | 4.2 / min | 79.8 % | 9.9 |
| 11 | gemma4-e4b | **10×** | 5.5 / min | 76.1 % | 6.2 |
| 12 | ministral3-14b | **7.5×** | 1.9 / min | 72.7 % | 17.4 |
| 13 | phi-4-14b | **7.4×** | 6.0 / min | 71.8 % | 5.0 |
| 14 | lfm2.5-24b-a2b | **6.5×** | 2.8 / min | 70.7 % | 13.9 |
| 15 | granite-4.2-8b | **5.0×** | 1.0 / min | 67.1 % | 32.1 |
| 16 | lfm3-1.7b | **3.6×** | 2.6 / min | 62.5 % | 12.3 |
| 17 | ernie4.5-21b-a3b | **3.0×** | 1.5 / min | 59.2 % | 19.6 |
| 18 | lfm2-24b-a2b | **2.1×** | 7.5 / min | 53.5 % | 1.3 |
| 19 | lfm2-8b-a1b | **1.3×** | 1.5 / min | 45.2 % | 3.1 |
| 20 | llama3.1-8b | **1.0×** | 0.2 / min | 41.1 % | 10.7 |
| 21 | mistral-7b-v0.3 | **0.38×** | −8.4 / min | 27.5 % | 1.7 |

The [leaderboard](https://tzipproth.github.io/LLMBench/leaderboard.html) has the 95 % ranges, model files and an explanation of
every column. Position follows the measured accuracy. Use `llmbench compare <runA> <runB>` for a paired statistical comparison.

- **Strength**: how many times higher the odds (right : wrong) of solving a question are than those of
  Llama 3.1 8B (= 1×). On a question that Llama 3.1 8B solves half of the time, a model with 64× is right 98 % of
  the time. Ratios are meaningful: 40× has twice the odds of 20×.
- **Quality per time**: right minus wrong answers per minute on the RTX 5070 Ti. Wrong answers cost, so fast
  guessing does not pay (see Mistral 7B).
- **Accuracy**: share of correct answers. Same order as the strength, but it squeezes the top, where every further
  percent is much harder to get.

DaisyRose-27B, RadixArk's post-trained Qwen3.8-27B, delivered 629 correct answers in bartowski's `IQ4_XS` GGUF.
It answered 17 more variants correctly than the Qwen3.8-27B GGUF run and 13 more than the EXL3 run. Six replies
hit the token limit (0.9 %) and none were invalid. Its quality per minute was close to both Qwen runs.
These results compare complete configurations, including model weights, quantization, context and thinking settings.
[Model card](https://huggingface.co/RadixArk/DaisyRose-27B) ·
[Release post](https://huggingface.co/blog/RadixArk/daisyrose-27b-pushing-the-frontier-of-post-trainin) ·
[Tested GGUF](https://huggingface.co/bartowski/RadixArk_DaisyRose-27B-GGUF) ·
[Run files](runs/v5-daisyrose-27b-c/)

Inkling 2 Mini delivered 613 correct answers, between the two Qwen3.8-27B runs at 612 and 616. It ran at its
default high reasoning effort, with six expert layers in system RAM. All 37 truncated replies used the full
7,000 tokens and took 152–158 seconds each. These replies (5.6 %) trigger the leaderboard's † flag.
Within 60 seconds, Inkling delivered 541 correct answers, compared
with 584 and 585 for the two Qwen runs. [Model card](https://huggingface.co/thinkingmachines/Inkling-2-Mini) ·
[Reasoning effort settings](https://huggingface.co/blog/inkling2mini#reasoning-effort) ·
[Tested GGUF quantization](https://huggingface.co/unsloth/Inkling-2-Mini-GGUF)

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
- The v5 default uses [prompts/neutral.txt](prompts/neutral.txt).
  `You are a helpful assistant. Answer the question accurately. You may reason first. End your reply with a last
  line "Final Answer:" followed by your answer.`
- Multiple-choice options are added as `A. … B. …`, shuffled with a fixed seed, so every model sees the same order.
- Temperature 0, seed 42, at most 7,000 output tokens including reasoning and the final answer. This shared cap
  fits both Qwen3.8-27B runs' 8,192-token context with room for the longest observed prompt (822 tokens).
  Accuracy and strength reflect performance with this generation budget. Quality per time accounts for the
  actual time spent on each reply, including replies that hit the cap.
- The original v5 setup disables thinking where the model supports a switch and leaves it to the server otherwise.
  Native thinking runs use the setup below. [auto/models.json](auto/models.json) records the `llama-server` arguments
  for unattended runs. EXL3 runs use the same benchmark request format through TabbyAPI.
- **Scoring** reads only the final answer, never the reasoning: the last line starting with `Final Answer:`
  (fallbacks: `Final Answer:` within a line, a line starting with `Answer:`, a single short line, a last line that
  is only `\boxed{…}`). Numbers are
  compared as numbers, options by letter or text. Answers cut off at the token limit count as wrong; models with
  5 % or more of them are marked on the leaderboard.

### Native thinking

The **Native thinking** label marks runs that use [prompts/native-thinking.txt](prompts/native-thinking.txt) with
thinking enabled or left to the server. The prompt requests an accurate final answer and omits `You may reason first.`
The model's chat template handles reasoning and any supported effort setting. The benchmark still scores the final
answer and uses the same 7,000-token output limit.

DaisyRose used llama.cpp `b12631`, a 16,384-token context, `--jinja --reasoning on --reasoning-effort medium`,
full GPU offload (`-ngl all`) and a `q8_0` KV cache. LLMBench's probe verified that the thinking switch worked.
DaisyRose's template defaults to `xhigh`. The run explicitly selected `medium`. The server's `--temp 1.0` was
overridden by LLMBench's request temperature of 0. [Model card](https://huggingface.co/RadixArk/DaisyRose-27B#2-evaluations)

```bash
llmbench run v5-daisyrose-27b-c --server http://127.0.0.1:8081 --model daisyrose-27b \
  --system-prompt prompts/native-thinking.txt --thinking on
```

On servers that support it, `--reasoning-effort default` leaves the effort at the chat template's default.
The v5 benchmark default remains `neutral.txt` pending a paired comparison on the same model and quantization.
Changing only the system prompt would measure its effect before changing the thinking mode or effort.
[llama.cpp server options](https://github.com/ggml-org/llama.cpp/blob/e300919cc/tools/server/README.md)

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

1. Start `llama-server` with the model (a dense model has to fit completely into VRAM) and check it:
   `llmbench server --server http://127.0.0.1:8081`. For a MoE model with experts in system RAM add
   `--notes "experts in system RAM"` to the run, so the leaderboard marks it.
2. Run all 662 questions: `llmbench run v5-<model> --server http://127.0.0.1:8081`
   (add `--thinking default` if the model has no thinking switch). The run refuses to start if the server runs a
   different model than the run name says.
3. `llmbench leaderboard` shows the model next to the others, already on the frozen strength scale.
4. `llmbench-heatmap` regenerates the heatmap and the leaderboard page.

Other OpenAI compatible servers work too if they are already running, e.g. TabbyAPI with an EXL3 quant:
`LLMBENCH_API_KEY=<key> llmbench run v5-<model>-exl3 --server http://127.0.0.1:5000 --model <model>-exl3`.
The API key is only read from the environment variable and never stored in the run.

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
