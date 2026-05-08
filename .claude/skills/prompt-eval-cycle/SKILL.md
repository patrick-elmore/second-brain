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
6. **Commit improvements** with before/after F2 in the commit message

Two commits per cycle: one for results, one for improvements. They are intentionally distinct so the audit trail shows "this finding triggered this fix."

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

1. **Working tree must be clean.** Run `git status -s` from the repo root. If anything is modified or untracked (other than expected eval state), stop and report. The cycle commits twice; a dirty tree confuses both commits.

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

This is **commit 1 of 2** for the cycle. Do not push.

## Phase 3: Analyze run logs

Read the captured log (`/tmp/prompt-eval-cycle-run.log`). Categorize every error line:

| Pattern in log | Category | Auto-fix? |
|---|---|---|
| `SqliteException ... no such column: \w+` | FTS5 syntax error in search query | Already handled; flag if it recurs frequently |
| `FileNotFoundException ... read_file` | Hallucinated path | Already returns guidance; flag if recurring |
| `UnauthorizedAccessException ... outside allowed roots` | Same as above | Already handled |
| `prompt is too long: .* tokens > 200000` | Tool loop spiral | Already capped; if it still happens lower MaxToolTurns |
| `TaskCanceledException ... HttpClient.Timeout` | Network timeout | Bump timeout; flag if from non-eval client |
| Any unhandled exception in `RunCaseAsync` | New unknown failure mode | Always flag |
| `proposer failed` | Proposer issue (parse error, timeout, etc.) | Inspect; may need to raise MaxTokens or revise instructions |

Count occurrences per category across the run.

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

## Phase 4: Apply mechanical fixes

For each category marked **auto-fix-applied**:

1. Apply the code change (see "Auto-fix recipes" below).
2. Add a unit test that exercises the fixed path.
3. Run the build + test suite. Both must succeed; if not, revert and flag instead.

For each category marked **flagged-for-review**: do nothing to code. The findings doc captures it.

If no auto-fixes are applicable this cycle, skip to Phase 5 with a "no improvements this cycle" note.

### Auto-fix recipes (extend as new categories emerge)

The set of recipes below is intentionally small and well-defined. If a category isn't in this list, flag it for review rather than improvising.

**Known categories with recipes:**
- *(none currently — the three known categories from the first cycle are already fixed in commits aa22828 and 7f7f807. New categories should be added to this section as they're discovered and fixed.)*

**Adding a new recipe**: when a flagged-for-review category gets manually fixed, append the recipe here so the next cycle can auto-fix it. Document: pattern, file to change, change to make, test to add.

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

## Phase 6: Commit improvements

First, generate `state/next-run.md` per the format defined in Phase 7. The file always exists at the end of every cycle, even when no fixes are applied — its purpose is to seed the next run.

Stage:
- `state/next-run.md` (always — this is the new requirement)
- All code/test changes from Phase 4 (if any)
- The findings doc from Phase 3 (if any)

Even when no fixes are applied AND no findings were written, still commit so `next-run.md` is captured. There is always a commit 2 of 2 now.

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

This is **commit 2 of 2** for the cycle.

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
  <sha2>  prompt-eval cycle <date>: <N> bug(s) fixed, baseline F2 <pre>→<post>

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
- **Pinned-best applied to production**: pinned-best.json's value differs from production system_prompt.md. → "Pinned best is captured in state but not yet promoted to production. Apply via `cp state/pinned-best.json's system_prompt.value → src/SecondBrain.Llm/Prompts/system_prompt.md` once you've reviewed it, then redeploy the MCP service."
- **No issues, no fixes, score moved**: no error patterns in logs and tuning improved scores anyway. → "Clean cycle. Re-run with a different surface or extend the iteration cap to push further."

## state/next-run.md — recommendations file format

This is a reference section (not a runtime phase). The file is generated and committed in Phase 6 above; the file's contents are read in Pre-flight Step 5 of the next cycle.

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
| Pinned-best != production system_prompt.md | Operational tasks: promote-to-production reminder |
| Index fingerprint at run-time differs from test-cases-v1.json's | Operational tasks: regenerate test cases |
| Verify regressed | Notes: warn that the previous cycle's fixes need review |

### When to write and commit it

**Write the file in Phase 6**, before staging the improvements commit. Stage `state/next-run.md` alongside the findings doc and any code/test changes, so the file lands in commit 2 of 2 — same commit that surfaces what was found. Single commit; no amend.

If Phase 6 would otherwise be skipped (no fixes AND no findings), still write `state/next-run.md` and commit it as commit 2. The cycle now always produces two commits as long as recommendations exist. Commit message:

```
prompt-eval cycle <date>: no improvements; recommendations recorded for next run
```

The next cycle's pre-flight Step 5 reads this file.

## Failure modes

- **Phase 1 crashes**: tee log captured; nothing committed yet; report and stop.
- **Phase 2 commit fails (e.g., signing)**: revert any state changes (`git checkout -- state/`), report and stop.
- **Phase 4 build/test breaks after auto-fix**: revert the fix file-by-file, mark the category as flagged-for-review, continue.
- **Phase 5 verify regresses**: do not commit improvements; print the regression and ask the user.
- **No working tree initially**: abort in pre-flight.

The cycle is idempotent: if it stops mid-way, the user can resolve the issue and re-invoke. The phases that already committed remain committed; subsequent phases pick up from a clean tree.
