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

Write findings to `.tools/second-brain-mcp/src/SecondBrain.PromptEval/state/findings/<phase-id>.md`:

```markdown
# Cycle findings: <phase-id>

## Score summary

- Baseline F2: <baseline>
- Best F2:     <best> (<+0.0XX or regressed>)
- Best iter:   <N>
- Stopped:     <reason>

## Error patterns observed

### <Category> (<N> occurrences)

Sample log line:
```
<one example>
```

Status: <already-fixed | new | recurring>
Action: <auto-fix-applied | flagged-for-review | none>

(repeat per category)

## Auto-fixes applied

- <one-line description per fix>

## Flagged for review

- <one-line description; why it needs human judgment>
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

If no auto-fixes were applied AND no findings were written, skip the commit and report "cycle complete — no improvements this round."

Otherwise stage:
- All code/test changes from Phase 4
- The findings doc from Phase 3

Commit message format:

```
prompt-eval cycle <date>: <N> bug(s) fixed, baseline F2 <before>→<after>

From cycle <phase-id>:

Fixes applied:
- <one-line per fix referencing what error pattern triggered it>

Flagged for review (no auto-fix):
- <one-line per flagged item, see state/findings/<phase-id>.md>

Re-baseline: F2=<after> (was <before>, delta <signed>)
```

This is **commit 2 of 2** for the cycle.

## Reporting

After both commits, print to stdout:

```
═══════════════════════════════════════════════════════════════════
prompt-eval cycle <phase-id> complete

Tuning result:    F2 <baseline>→<best> (delta <signed>) over <N> iterations
                  Stopped: <reason>

Improvements:     <count auto-fixed>, <count flagged>
Verify baseline:  F2 <after-fixes> (was <before-fixes>, delta <signed>)

Commits:
  <sha1>  prompt-eval cycle <date>: <surface> tuning ...
  <sha2>  prompt-eval cycle <date>: <N> bug(s) fixed ...

Findings doc:     state/findings/<phase-id>.md
═══════════════════════════════════════════════════════════════════
```

Suggest the next action:
- If best iter > baseline: consider extending iteration-cap to push further
- If flagged items present: address them next cycle
- If verify-baseline regressed: investigate before next cycle

## Failure modes

- **Phase 1 crashes**: tee log captured; nothing committed yet; report and stop.
- **Phase 2 commit fails (e.g., signing)**: revert any state changes (`git checkout -- state/`), report and stop.
- **Phase 4 build/test breaks after auto-fix**: revert the fix file-by-file, mark the category as flagged-for-review, continue.
- **Phase 5 verify regresses**: do not commit improvements; print the regression and ask the user.
- **No working tree initially**: abort in pre-flight.

The cycle is idempotent: if it stops mid-way, the user can resolve the issue and re-invoke. The phases that already committed remain committed; subsequent phases pick up from a clean tree.
