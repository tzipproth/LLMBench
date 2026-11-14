namespace LLMBench.Core.Stats;

/// <summary>
/// Texts that describe the benchmark for another LLM (shared by the briefing of the CSV export and the
/// Markdown export). Kept in one place so both stay consistent with the catalog format and the scoring code.
/// </summary>
public static class BenchDocs
{
    public const string Purpose = """
        LLMBench measures general reasoning and common sense of local LLMs (about 7–30 billion parameters): realistic
        everyday situations, spatial and physical reasoning, logic, perspective of others, attention to details. It does
        **not** test expert knowledge, coding, math beyond everyday arithmetic, or agentic tasks. Every question has exactly
        one correct answer that an attentive adult finds within about a minute without tools. Answers are scored
        automatically (no LLM judge), so every question must be scorable without interpretation.
        """;

    public const string CatalogFormat = """
        The catalog is a JSON array of questions. Each question has a **base** (the main question with its answer list and
        correct answer) and the **variants** that are actually asked. A variant only states what differs from the base and
        inherits everything else; every variant is sent to the model as a separate request, without any context.

        ```json
        { "id": "CS-101", "category": "altered_classic", "tier": 2, "tags": ["draft"],
          "base": { "question": "Sally has 3 brothers. Each of her brothers has 2 sisters. How many sisters does Sally have?",
                    "answerType": "number", "answer": "1" },
          "variants": [
            { "id": "free", "format": "free" },
            { "id": "altered-free", "kind": "altered", "format": "free",
              "question": "Sally has 3 brothers. Each of her brothers has 3 sisters. How many sisters does Sally have?",
              "answer": "2", "note": "trap: 1 (original riddle)" } ] }
        ```

        | Field | Meaning |
        |---|---|
        | `id`, `category`, `tier`, `tags` | question id; topic group; difficulty 1 = easy anchor, 2 = medium, 3 = hard, 4 = very hard, 5 = hardest (free answers only), 6 = room puzzles (free answers only), 7 = last hard round (free answers only), 8 = object-frame round (free answers only); tags (`draft` = not yet reviewed) |
        | `base.question` | the main question (for an altered riddle: the well-known original) |
        | `base.options` | answer list for multiple choice: the correct answer plus plausible distractors, in a sensible order |
        | `base.answer` | the correct answer (for multiple choice exactly one of `base.options`) |
        | `base.answerType` | `number` or `text` (default): how free answers are compared |
        | `base.acceptedAnswers` | further accepted free answers (e.g. "Candle A" next to "A") |
        | `base.fixedOrder` | `true` = never shuffle the options (ascending numbers, "cannot be determined" last) |
        | `variants[].id` | variant id, e.g. `mcq`, `free`, `altered-mcq`, `altered-free` |
        | `variants[].format` | `choice` = multiple choice with the base options, `free` = no options, the model must find the answer |
        | `variants[].kind` | `original` (default) = the base question; `altered` = one detail changed so that the memorized answer of a known puzzle becomes wrong (needs its own `question` and `answer`); `distractor` = an irrelevant sentence added |
        | `variants[].optionCount` | show only N options: the correct answer plus the first N−1 distractors of the list |
        | `variants[].question`, `answer`, `options`, `tier`, `note` | overrides of the base for this variant |

        Original + altered of the same question form a **control pair**: altered wrong but original right means the model
        only recognizes the known puzzle instead of reading the question.
        """;

    public const string ScoringRules = """
        Every prompt ends with an instruction to finish the reply with one line `Final Answer: <letter>` (multiple choice)
        or `Final Answer: <answer>` (free). Scoring rules – new questions must fit them:

        - Only the **last** `Final Answer:` line counts. No final line → `invalid` (never guessed from the text).
        - **Multiple choice:** the options are **shuffled** per run and shown as A, B, C …; the answer is the letter (or the
          exact option text). Never refer to option letters or positions in the question text. Use `fixedOrder` when the
          order matters.
        - **Free, number:** the number in the answer is compared numerically ("$0.10", "0.1 dollars" = 0.10). Remarks in
          parentheses are ignored. Several different numbers outside parentheses → `invalid`. State the unit in the question
          ("in minutes", "in km") so that only one number is a valid answer.
        - **Free, text:** exact comparison after lower-casing, trimming and removing a leading article and a final period.
          Therefore free text answers only work for **single words** (a name, a weekday, a direction, a letter); everything
          longer must be multiple choice. Add obvious alternative spellings to `acceptedAnswers`.
        - `truncated` = the reply hit the token limit (usually the model loops); `error` = server problem (not counted).
        """;

    public const string Lessons = """
        These questions had to be reworded after calibration because a model found a defensible second answer – avoid
        such ambiguities:

        - "How many cubic metres of soil are in the hole?" after it was filled again: "a filled hole is no longer a hole" →
          ask "how much soil was put back into the pit".
        - "This morning the street is completely dry. Did it rain this morning?": rain during the night could have dried →
          use the present tense ("right now … is it raining right now?").
        - "He pulls a string tied to the top cup of a stack" (sideways): the whole stack slides along → "pulls it straight
          up until it hangs freely".
        - A free answer "A" (which candle?): models answer "Candle A" → add it to `acceptedAnswers` or use multiple choice.
        - Generated multi-step tasks (rolling a die, swapping cups, walking a grid) are reliable hard questions because the
          solution is computed, not argued.
        """;

    public static string Task(string idPrefix = "NEW") => $$"""
        Please analyse the catalog and the results and propose **new questions** for the next catalog version.

        1. Briefly describe what distinguishes the questions that separate the models from those that all models solve,
           and which weaknesses of the weaker and of the stronger models are visible.
        2. Propose new questions, **mainly tier 3** – hard enough that the strongest models fail sometimes – and some tier 2.
           Aim at the visible weaknesses, but vary topics and surface form. Do not copy well-known puzzles; if you use one,
           change a detail so that the memorized answer becomes wrong and add the unchanged version as the base (`original`)
           with the changed one as an `altered` variant.
        3. Every question must have **exactly one** correct answer that an attentive adult finds in about a minute without
           tools or expert knowledge. State units, time frames and assumptions explicitly. English only.
        4. For each question add a `note`: why the answer is unique and what the tempting wrong answer is.
        5. Where it makes sense, provide both a `choice` and a `free` variant (free only for numbers or single words).
        6. Return only a JSON array in the catalog format above, ids `{{idPrefix}}-001`, `{{idPrefix}}-002`, …, `"source": "llm-proposal"`,
           tag `draft`, with a `category` from the existing ones or a new descriptive one.
        """;
}
