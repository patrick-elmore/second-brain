---
name: prompt-eval-cycle
description: Run one full self-improvement cycle on the second-brain MCP. Tunes the system prompt against the synthetic test set, commits the eval results, then analyzes the run logs to identify and fix production code bugs (with tests) before committing the improvements separately.
argument-hint: "[--surface system_prompt|user_wrapper] [--iteration-cap N] [--dry-run]"
---

# /prompt-eval-cycle — Self-Improvement Loop for second-brain MCP

One cycle:

1. **Run** the prompt-tuning loop against the fixed test set
2. **Commit results** (state/runs/, pinned-best.json) — distinct commit
3. **Analyze** the run logs for production code bugs the eval surfaced
4. **Fix** mechanical bugs (with tests); flag ambiguous ones in a findings doc
5. **Verify** by re-scoring the baseline against the fixed pipeline
6. **Promote pinned-best to production** if the pinned prompt differs from `src/SecondBrain.Llm/Prompts/system_prompt.md` — distinct commit
7. **Commit improvements** with before/after F2 in the commit message

Two or three commits per cycle: one for results, optionally one for prompt promotion, one for improvements/recommendations. They are intentionally distinct so the audit trail shows "this finding triggered this fix" and "this tuning win is now in production."

## When to invoke

- After meaningfully changing the corpus (new sources indexed, large mtime drift)
- When you suspect retrieval quality has regressed
- Periodically (weekly?) as a maintenance discipline
- After deliberately changing production code that touches search, the tool loop, or the system prompt

## Defaults

- Surface: `system_prompt`
- Iteration cap: 3 (per current plan; raise via `--iteration-cap N`)
- Test cases file: `state/test-cases-v1.json` (must exist; run `generate-test-cases` once first)

## Pre-flight checks

Before doing anything that costs LLM calls or touches git:

1. **Working tree must be clean.** Run `git status -s` from the repo root. If anything is modified or untracked (other than expected eval state), stop and report. The cycle creates two or three sequential commits; a dirty tree confuses them.

2. **Test cases must exist.** Check for `.tools/second-brain-mcp/src/SecondBrain.PromptEval/state/test-cases-v1.json`. If missing, abort and tell the user to run:
   ```
   dotnet.exe run --project .tools/second-brain-mcp/src/SecondBrain.PromptEval -- generate-test-cases
   ```
   then hand-review the output before retrying.

3. **Build must succeed.** `cd .tools/second-brain-mcp && dotnet.exe build second-brain-mcp.slnx --verbosity minimal`. If it fails, abort.

4. **Baseline tests must pass.** `dotnet.exe test second-brain-mcp.slnx --verbosity minimal`. Capture the count for the cycle's "before" baseline. If any tests fail, abort.

5. **Load prior recommendations.** Read `.tools/second-brain-mcp/src/SecondBrain.PromptEval/state/next-run.md` if it exists. This file is written at the end of every cycle and reflects what the prior cycle suggested for this one. Print its contents verbatim under a heading "PRIOR-CYCLE RECOMMENDATIONS" so the user sees them before any LLM calls happen.

   For each item in the prior recommendations:
   - **Tuning suggestion** (e.g., raise iteration cap, switch surface): if the user invoked the skill with no overriding argument, apply the suggestion to this cycle's parameters. Print "Applying: <suggestion>" so it's auditable. If the user passed a conflicting argument (e.g., `--iteration-cap 3` overriding a "raise to 5" suggestion), the user's argument wins; print "User override: <arg> overrides recommendation <suggestion>".
   - **Carried-forward findings** (unresolved flagged items from prior cycles): print them as a checklist. Do NOT auto-act on them — they were flagged for human judgment originally; they remain so.
   - **Operational tasks** (e.g., promote pinned-best to production, regenerate test cases): print as reminders. Do NOT auto-act unless explicitly told the skill should.

   If `next-run.md` does not exist, this is either the first cycle or the file was deleted. Print "No prior-cycle recommendations file." and proceed.

## Phase 1: Run the eval

Parse arguments:

```
SURFACE=system_prompt
ITERATION_CAP=3
DRY_RUN=false
ARGS="<the user's arguments>"
# parse --surface, --iteration-cap, --dry-run
```

Execute the tune command, capturing stdout+stderr to a tee'd log:

```bash
cd .tools/second-brain-mcp
dotnet.exe run --project src/SecondBrain.PromptEval -- tune \
  --surface "$SURFACE" --iteration-cap "$ITERATION_CAP" \
  2>&1 | tee /tmp/prompt-eval-cycle-run.log
```

Record:
- Phase id (line: `Phase id:        2026-...`)
- Stopped reason (line: `Stopped:         <plateau|iteration_cap|proposer_failures>`)
- Best iteration F2 (line: `Best iteration:  N (F2=...)`)
- Per-iteration table at the end

Stop here if `--dry-run` was set. Print the summary and exit.

## Phase 2: Commit eval results

Stage exactly:
- `.tools/second-brain-mcp/src/SecondBrain.PromptEval/state/runs/<phase-id>.json`
- `.tools/second-brain-mcp/src/SecondBrain.PromptEval/state/pinned-best.json`

Do NOT stage `score-cache.json` (it's gitignored).

Verify only those files are staged:
```bash
git.exe diff --stat --cached
```

Commit:
```
prompt-eval cycle <date>: <surface> tuning produced F2 <baseline>→<best>

Phase: <phase-id>
Stopped: <reason>
Best iteration: <N>

Iterations:
 # 0  F2=...  (baseline)
 # 1  F2=...  <one-line rationale>
 ...

Tunable surface: <surface>
Test set: <id> (<count> cases)
```

This is **commit 1** for the cycle (of 2 or 3, depending on whether Phase 6 promotes). Do not push.

## Phase 3: Analyze run logs

Read the captured log (`/tmp/prompt-eval-cycle-run.log`). Categorize every error line:

| Pattern in log | Category | Auto-fix? |
|---|---|---|
| `SqliteException ... no such column: \w+` | FTS5 syntax error in search query | Already handled; flag if it recurs frequently |
| `FileNotFoundException ... read_file` | Hallucinated path | Already returns guidance; flag if recurring |
| `UnauthorizedAccessException ... outside allowed roots` | Same as above | Already handled |
| `prompt is too long: .* tokens > 200000` | Tool loop context overflow | **Auto-fix (hard blocker)**: see recipe in Phase 4. Combined fix: read_file truncation + token-budget guard |
| `messages\.\d+: user messages must have non-empty content` | Empty/malformed user message | **Auto-fix (hard blocker)**: see recipe in Phase 4. Defensive guard for empty tool-results + omit-Tools forced synthesis |
| `TaskCanceledException ... HttpClient.Timeout` | Network timeout | Bump timeout; flag if from non-eval client |
| Any unhandled exception in `RunCaseAsync` | New unknown failure mode | **Auto-fix if hard blocker** (see "Hard-blocker policy" below); otherwise flag |
| `proposer failed` | Proposer issue (parse error, timeout, etc.) | Inspect; may need to raise MaxTokens or revise instructions |

Count occurrences per category across the run.

### Hard-blocker policy

A "hard-blocking" issue is one that:
- Causes one or more test cases to score F2=0 every cycle (zero-score floor, not a low-score), OR
- Crashes the eval phase (any unhandled exception escaping `EvalRunner.EvaluateAsync`), OR
- Is rejected by the upstream API in a way the harness cannot work around (e.g., the request is malformed or exceeds a hard limit)

**Hard blockers MUST be fixed in the cycle that surfaces them, even if the fix involves design judgment.** Do not flag-and-wait. The cycles cost LLM calls; running a "dead cycle" that re-discovers the same blocker every time is waste. If multiple plausible fixes exist:

1. Pick the one with the smallest blast radius (a defensive guard before a refactor, a per-call cap before a global limit).
2. Document the choice and the rejected alternatives in the findings doc.
3. Apply it. Add a unit test. Verify the build + tests pass.
4. If the chosen fix breaks the build or tests, revert and flag — at that point the cycle has done its job and human design judgment is needed before retrying.

**Soft issues** (warnings, edge cases, style preferences, contested design decisions on non-blockers) still flag-for-review. The flag-and-wait pattern is the right behavior for "we should think about this" — wrong for "this is breaking 4 cases every cycle."

Write findings to `.tools/second-brain-mcp/src/SecondBrain.PromptEval/state/findings/<phase-id>.md`. The structure mirrors the final summary so the doc and the printed report tell the same story.

```markdown
# Cycle findings: <phase-id>

Generated: <iso-timestamp>
Surface tuned: <surface>
Test set: <id> (<count> cases)

## Tuning result

| Metric      | Baseline | Best (iter K) | Delta |
|-------------|----------|---------------|-------|
| F2          | <pre>    | <best>        | <+/-> |
| Recall      | <pre>    | <best>        | <+/-> |
| Precision   | <pre>    | <best>        | <+/-> |
| Acceptable% | <pre>    | <best>        | <+/-> |

Per-iteration progression:
- Iter 0 (baseline): F2=<x>
- Iter 1: F2=<x> — <rationale>
- Iter 2: F2=<x> — <rationale>
- Iter K (★ best): F2=<x> — <rationale>

Stopped: <reason>

## Issues observed

### Issue 1: <Category> (<N> occurrences)

**Pattern**: <description>

**Sample log lines**:
```
<one or two examples, kept short>
```

**Impact**: <e.g. "wasted ~2 turns per failure", "crashed entire phase">

**Status**: <new | recurring | already-fixed-prior-cycle>

**Action**: <auto-fix-applied | flagged-for-review | none-needed>

**If auto-fixed**: file `<path>` change `<one-sentence>`, test `<test name>`.

**If flagged**: <why it needs human judgment — what makes it ambiguous, what design decisions are involved>

(repeat per issue)

## Verification

| Metric      | Pre-fix | Post-fix | Delta |
|-------------|---------|----------|-------|
| F2          | <x>     | <x>      | <+/-> |
| Recall      | <x>     | <x>      | <+/-> |
| Precision   | <x>     | <x>      | <+/-> |
| Acceptable% | <x>     | <x>      | <+/-> |

Note: per-fix attribution not measured; the verify scores all fixes together.
```

## Phase 4: Apply fixes

Two paths:

**Hard-blocking issues** (see Hard-blocker policy in Phase 3): apply a fix in this cycle, even if there's no pre-canned recipe. Pick the smallest-blast-radius option. Document the choice and rejected alternatives in the findings doc. Add a unit test. Build + tests must pass; if not, revert and flag.

**Recipe-driven issues** (categories with entries in "Auto-fix recipes" below): apply the documented change.

**Soft / contested issues**: flag-for-review — do nothing to code. The findings doc captures them for human judgment.

For every applied fix:

1. Apply the code change.
2. Add a unit test that exercises the fixed path.
3. Run the build + test suite. Both must succeed; if not, revert and flag instead.

If no fixes are applicable this cycle, skip to Phase 5 with a "no improvements this cycle" note.

### Auto-fix recipes (extend as new categories emerge)

Recipes are concrete change instructions for known patterns. For hard blockers without a recipe, see "Hard-blocker policy" in Phase 3 — pick the smallest-blast-radius fix, document the choice, apply it.

**Known categories with recipes:**

- **`prompt is too long: .* tokens > 200000`** (tool loop context overflow)
  - Already-applied guards in `src/SecondBrain.Llm/ToolLoop.cs`:
    - `MaxToolTurns = 25` cap with omit-Tools forced synthesis on overflow
    - `MaxReadFileBytes = 32_768` truncation in `RunReadFile` with marker
    - `ContextSoftLimitTokens = 150_000` soft limit; when exceeded the next API call omits Tools
  - If this pattern still recurs frequently after the existing guards: lower `MaxReadFileBytes` first (most targeted), then `ContextSoftLimitTokens`, then `MaxToolTurns` last (broadest reduction).
  - Tests: `RunAsync_ReadFileLargerThanCap_*`, `RunAsync_ContextSoftLimitReached_*`, `RunAsync_ToolLoopHitsCap_*`.
  - Fixed in commits 7f7f807 and a7c7fdc.

- **`messages\.\d+: user messages must have non-empty content`** (empty/malformed user message)
  - Already-applied guards in `src/SecondBrain.Llm/ToolLoop.cs`:
    - Forced synthesis omits Tools instead of injecting a second user message (avoids consecutive same-role messages that the API may flag as empty).
    - Defensive guard: if `StopReason == ToolUse` but no `tool_use` blocks were dispatched, treat as completion and extract any text — never add an empty user message.
  - If this pattern recurs after the guards: dump the message list at the moment of failure (see `_logger.LogError` in `DispatchToolAsync` for the existing pattern) to identify which message is empty and why.
  - Tests: `RunAsync_StopReasonToolUseButNoToolUseBlocks_*`.
  - Fixed in commit a7c7fdc.

- **`SqliteException`** (FTS5 syntax error in search query)
  - `SearchEngine.Search` catches `SqliteException` and returns empty hits.
  - Tests in `SecondBrain.Index.Tests`.
  - Fixed in commit 7f7f807.

- **`FileNotFoundException` / `UnauthorizedAccessException` in read_file**
  - `ToolLoop.RunReadFile` catches both and returns actionable guidance ("use only absolute_path values returned by `search`; do not invent paths"). Failed reads do not record into `FilesReferenced`.
  - Tests: `RunAsync_ReadFileNotFound_*`, `RunAsync_ReadFileOutsideAllowedRoots_*`.
  - Fixed in commit 7f7f807.

**Adding a new recipe**: when a hard blocker is auto-fixed in a cycle, append the recipe here so the pattern + fix + test are documented for future maintenance. Document: pattern, file to change, change to make, test to add, commit.

## Phase 5: Verify

Re-score the baseline against the (possibly fixed) pipeline:

```bash
rm -f .tools/second-brain-mcp/src/SecondBrain.PromptEval/state/score-cache.json
cd .tools/second-brain-mcp
dotnet.exe run --project src/SecondBrain.PromptEval -- score 2>&1 | tee /tmp/prompt-eval-cycle-verify.log
```

Capture the new baseline F2 from the output (`Mean F2:           0.XXX`).

Compare against the original baseline from Phase 2:
- **Improved (delta > 0)**: good — record in commit message
- **Unchanged (|delta| ≤ 0.01)**: fixes were neutral on retrieval quality (still worth keeping if they prevent crashes)
- **Regressed (delta < -0.01)**: stop and flag. Either revert the auto-fixes or consult before committing.

## Phase 6: Promote pinned-best to production prompt

The pinned-best prompt is the best system prompt the harness has ever seen across all cycles. The production prompt that ships with the binary is `src/SecondBrain.Llm/Prompts/system_prompt.md`. This phase keeps them in sync automatically.

Read both:
- Pinned: `jq -r '.system_prompt.value' src/SecondBrain.PromptEval/state/pinned-best.json`
- Production: `cat src/SecondBrain.Llm/Prompts/system_prompt.md`

**If pinned-best.value is null or missing**: nothing to promote. Skip to Phase 7.

**If pinned-best.value matches production verbatim**: nothing to promote (already in sync). Skip to Phase 7.

**Otherwise** (pinned differs from production):

1. Overwrite production with the pinned value:
   ```bash
   jq -r '.system_prompt.value' \
     .tools/second-brain-mcp/src/SecondBrain.PromptEval/state/pinned-best.json \
     > .tools/second-brain-mcp/src/SecondBrain.Llm/Prompts/system_prompt.md
   ```

2. Rebuild the solution. The prompt is an embedded resource; if the new file is malformed (encoding issue, truncation), the build will catch it. The pre-flight tests must still pass.
   ```bash
   cd .tools/second-brain-mcp
   dotnet.exe build second-brain-mcp.slnx --verbosity minimal
   dotnet.exe test second-brain-mcp.slnx --verbosity minimal --no-build
   ```

   If build or tests fail, **revert** (`git checkout -- src/SecondBrain.Llm/Prompts/system_prompt.md`) and flag in findings as a "promotion failure" issue. Continue to Phase 7 without the promotion commit.

3. Stage and commit the prompt change as a distinct commit:
   ```bash
   git.exe add .tools/second-brain-mcp/src/SecondBrain.Llm/Prompts/system_prompt.md
   git.exe commit -m "$(cat <<'EOF'
   prompt-eval cycle <date>: promote pinned-best system prompt to production (F2 <pinned-score>)

   Source: state/pinned-best.json (cycle <pinned phase-id>, iter <pinned iteration_id>)

   The pinned-best prompt has not been in production until this commit. Run
   update.ps1 to redeploy the MCP service so the live binary picks up the change.
   EOF
   )"
   ```

This is **commit 2 of 3** when promotion happens (commit 1 was results, commit 3 will be improvements/recommendations).

**Important**: The cycle does NOT run `update.ps1`. That requires admin elevation and stops the Windows service for 30-60 seconds — too disruptive to do unattended. The redeploy is surfaced as a NEXT STEPS reminder for the user to run after reviewing.

## Phase 7: Commit improvements

First, generate `state/next-run.md` per the format defined below. The file always exists at the end of every cycle, even when no fixes are applied — its purpose is to seed the next run.

Stage:
- `state/next-run.md` (always — this is the new requirement)
- All code/test changes from Phase 4 (if any)
- The findings doc from Phase 3 (if any)

Even when no fixes are applied AND no findings were written, still commit so `next-run.md` is captured. There is always a final commit (whether 2 or 3 depending on whether Phase 6 ran).

Commit message format depends on what's in the commit:

If fixes or findings present:
```
prompt-eval cycle <date>: <N> bug(s) fixed, baseline F2 <before>→<after>

From cycle <phase-id>:

Fixes applied:
- <one-line per fix referencing what error pattern triggered it>

Flagged for review (no auto-fix):
- <one-line per flagged item, see state/findings/<phase-id>.md>

Re-baseline: F2=<after> (was <before>, delta <signed>)

Recommendations for next run captured in state/next-run.md.
```

If no fixes and no findings (only `next-run.md` is staged):
```
prompt-eval cycle <date>: no improvements; recommendations recorded for next run

From cycle <phase-id>:

No new issues observed; no fixes applied.
Re-baseline: F2=<after> (was <before>, delta <signed>).
Recommendations for next run captured in state/next-run.md.
```

This is the **final commit** for the cycle (commit 2 if Phase 6 was skipped, commit 3 if Phase 6 promoted a new prompt).

## Reporting

After both commits, print a structured summary to stdout. The summary must include a per-issue breakdown (what was found, what was done, what changed because of it), not just an aggregate score.

Capture metrics from three sources during the cycle so the summary can be assembled at the end:
- **Pre-cycle baseline** — from Phase 1's iter-0 score
- **Tuning best** — from Phase 1's best-iteration score
- **Post-fix baseline** — from Phase 5's verify run

For each: mean F2, mean precision, mean recall, count of cases with F2 ≥ 0.5, and per-source-type F2 breakdown. The score command's stdout already prints all of these.

Format:

```
═══════════════════════════════════════════════════════════════════
prompt-eval cycle <phase-id> complete
═══════════════════════════════════════════════════════════════════

TUNING RESULT
  Iteration cap reached at <N>; best iter <K> (stopped: <reason>)

  Metric        Baseline    Best (iter K)   Delta
  ──────────────────────────────────────────────
  F2            <pre>       <best>          <+/->
  Recall        <pre>       <best>          <+/->
  Precision     <pre>       <best>          <+/->
  Acceptable%   <pre>       <best>          <+/->

  Per source type (best iteration):
    transcript    F2=<x>  (delta <+/->)
    note          F2=<x>  (delta <+/->)
    ...

ISSUES FOUND IN RUN LOGS

  1. <Category> (<N> occurrences)
     Pattern: <one-line description of what triggers it>
     Sample:  <one log line excerpt, truncated to ~120 chars>
     Impact:  <e.g. "wasted ~2 turns per failure", "crashed entire phase">
     Status:  <auto-fixed | flagged-for-review | recurring (already-fixed)>

  2. ...

  (If zero categories: "No new issues observed; all error patterns already
   handled by prior fixes.")

FIXES APPLIED

  1. <File>: <one-sentence description of change>
     Triggered by: issue #<N> above
     Test added:   <test name>

  2. ...

  (If none: "No auto-fixes applied this cycle.")

FLAGGED FOR REVIEW

  1. <Category>: <one-sentence description, why it needs human judgment>
     See state/findings/<phase-id>.md for full context.

  2. ...

  (If none: "No items flagged.")

VERIFICATION (re-baseline against the fixed pipeline)

  Metric        Pre-fix     Post-fix        Delta
  ──────────────────────────────────────────────
  F2            <pre>       <post>          <+/->
  Recall        <pre>       <post>          <+/->
  Precision     <pre>       <post>          <+/->
  Acceptable%   <pre>       <post>          <+/->

  Per source type:
    transcript    F2=<pre>→<post>  (delta <+/->)
    ...

  Note: per-fix attribution is not available — the verify scores all fixes
  together. If you need per-fix impact, re-run with --score-each-fix
  (~50-100 extra LLM calls per fix).

COMMITS
  <sha1>  prompt-eval cycle <date>: <surface> tuning F2 <pre>→<best>
  <sha2>  prompt-eval cycle <date>: promote pinned-best system prompt to production (F2 <pinned-score>)
          (only present when Phase 6 promoted a new prompt)
  <sha3>  prompt-eval cycle <date>: <N> bug(s) fixed, baseline F2 <pre>→<post>

ARTIFACTS
  Findings:  state/findings/<phase-id>.md
  Run log:   state/runs/<phase-id>.json
  Pinned:    state/pinned-best.json (now reflects iter <K> winner)

NEXT STEPS
  <bulleted recommendations from the rules below>
═══════════════════════════════════════════════════════════════════
```

### Next-step rule set

Generate the "NEXT STEPS" bullets by applying these rules in order. Output the bullet for every rule that fires; suppress rules that don't apply.

- **Tuning regressed**: best F2 ≤ baseline F2. → "Tuning produced no improvement. Consider regenerating test cases (corpus may have shifted) or reviewing the proposer's rationale for whether it's stuck on cosmetic edits."
- **Verify regressed**: post-fix F2 < pre-fix F2 by > 0.01. → "Verification dropped F2 from <pre> to <post>. Investigate which fix caused the regression before next cycle. Improvements were committed but should be reviewed."
- **Auto-fixes had no measurable effect**: post-fix F2 within ±0.005 of pre-fix. → "Fixes prevented crashes but did not move retrieval scores. They are still worth keeping (system is more robust)."
- **Flagged items present**: → "<N> finding(s) flagged for human review in state/findings/<phase-id>.md. Resolve before the next cycle."
- **Tuning hit iteration cap with positive delta**: stopped reason was `iteration_cap` and best F2 > baseline + 0.02. → "Tuning was still improving at the cap. Consider raising --iteration-cap (current: <N>) on the next cycle."
- **Stopped on plateau**: stopped reason was `plateau`. → "Tuning plateaued at iter <K>. The current surface may be near its limit; consider tuning a different surface next cycle (e.g. --surface tool_descriptions or user_wrapper)."
- **Promoted prompt needs redeploy**: Phase 6 promoted the pinned-best prompt to production this cycle. → "Production prompt was updated to pinned-best (F2 <pinned-score>). Run `update.ps1` (admin) to redeploy the MCP service so the live binary picks up the change."
- **Promotion failed**: Phase 6 attempted promotion but build/tests failed and the change was reverted. → "Pinned-best promotion was attempted but reverted because the build/tests failed against the new prompt. Investigate state/findings/<phase-id>.md before next cycle — the pinned prompt may have an encoding or format issue."
- **No issues, no fixes, score moved**: no error patterns in logs and tuning improved scores anyway. → "Clean cycle. Re-run with a different surface or extend the iteration cap to push further."

## state/next-run.md — recommendations file format

This is a reference section (not a runtime phase). The file is generated and committed in Phase 7 above; the file's contents are read in Pre-flight Step 5 of the next cycle.

The recommendations file is the single source of truth for "what should the next run start from" — the prior cycle's NEXT STEPS bullets, expressed in a format the pre-flight loader can act on.

Overwrite the file each cycle (do not append). The next-run file always reflects the most recent cycle's output. Carry-forward of unresolved items happens explicitly: if a flagged finding from this cycle is still unresolved, write it as a "carried-forward findings" entry. If a flagged finding was resolved in this cycle, drop it.

Format:

```markdown
# Next-run recommendations

Generated: <iso-timestamp>
From cycle: <phase-id>

## Tuning suggestions

These are parameter overrides the next cycle should apply if invoked with no conflicting argument.

- **iteration-cap**: <N> (current default 3; raise/lower based on this cycle's stop reason)
- **surface**: <id> (current default system_prompt; switch if this cycle plateaued)
- (omit any line that has no recommendation; absent = no change from default)

## Carried-forward findings

Issues flagged for review by THIS cycle (or earlier cycles, if they remain unresolved). The pre-flight loader prints these as a checklist; it does NOT auto-act on them.

- [<this-cycle phase-id>] <one-line description>. See state/findings/<phase-id>.md.
- (omit the section if empty)

## Operational tasks

Reminders that don't change the next cycle's parameters but should be addressed before or alongside it.

- <e.g., "Promote state/pinned-best.json's system_prompt.value to src/SecondBrain.Llm/Prompts/system_prompt.md and redeploy the MCP service.">
- <e.g., "Regenerate test cases — index fingerprint has shifted from <old> to <new>.">
- (omit the section if empty)

## Notes

Optional free-form context: anything that doesn't fit the structured sections above but the next cycle's operator should know about.
```

### Generating each section from the cycle's data

| Source signal | Goes into |
|---|---|
| Stopped on `iteration_cap` with positive delta > 0.02 | Tuning suggestions: `iteration-cap: <N+2>` |
| Stopped on `plateau` | Tuning suggestions: `surface: <next surface to try>` |
| Any items in FLAGGED FOR REVIEW | Carried-forward findings (one entry each) |
| Phase 6 promoted a new prompt this cycle | Operational tasks: redeploy MCP service via `update.ps1` |
| Phase 6 attempted promotion but reverted | Operational tasks: investigate why the pinned prompt failed to build |
| Index fingerprint at run-time differs from test-cases-v1.json's | Operational tasks: regenerate test cases |
| Verify regressed | Notes: warn that the previous cycle's fixes need review |

The "promote pinned-best to production" reminder no longer appears in Operational tasks — that promotion now happens automatically in Phase 6 of every cycle. If the promotion was skipped (no diff), there's nothing to remind. If it was attempted, the redeploy reminder fires instead.

### When to write and commit it

**Write the file in Phase 7**, before staging the improvements commit. Stage `state/next-run.md` alongside the findings doc and any code/test changes, so the file lands in the final commit — same commit that surfaces what was found. Single commit; no amend.

If Phase 7 would otherwise be skipped (no fixes AND no findings), still write `state/next-run.md` and commit it as the final commit. The cycle now always produces a final commit. Commit message:

```
prompt-eval cycle <date>: no improvements; recommendations recorded for next run
```

The next cycle's pre-flight Step 5 reads this file.

## Failure modes

- **Phase 1 crashes**: tee log captured; nothing committed yet; report and stop.
- **Phase 2 commit fails (e.g., signing)**: revert any state changes (`git checkout -- state/`), report and stop.
- **Phase 4 build/test breaks after auto-fix**: revert the fix file-by-file, mark the category as flagged-for-review, continue.
- **Phase 5 verify regresses**: do not commit improvements; print the regression and ask the user.
- **Phase 6 build/test breaks after promotion**: revert system_prompt.md (`git checkout -- src/SecondBrain.Llm/Prompts/system_prompt.md`), record a "promotion failure" entry in the findings doc, skip the promotion commit, continue to Phase 7. The pinned-best.json still reflects the winner — the next cycle will retry promotion.
- **No working tree initially**: abort in pre-flight.

The cycle is idempotent: if it stops mid-way, the user can resolve the issue and re-invoke. The phases that already committed remain committed; subsequent phases pick up from a clean tree.
