# Agent Writing Guidelines

Standards for every agent spec in `.claude/agents/` and every skill spec in `.claude/skills/`. Every spec follows these conventions. Drift is expensive.

---

## Agent Spec Structure

Every agent spec has these sections, in this order:

1. **Frontmatter** (name, description, model, effort, permissionMode, disallowedTools)
2. **Role** — one paragraph: what the agent is, what it does, what it does NOT do
3. **Input Contract** — arguments accepted, their format, required vs optional
4. **Behavior** — numbered workflow steps; scripts called by name with arguments
5. **Output Contract** — exact format of what is returned; small by design
6. **Scripts Used** — scripts this agent calls, with purpose and signature
7. **Constraints** — what the agent may not do (write paths, scope limits, judgment limits)

---

## Frontmatter Checklist

```yaml
---
name: agent-name               # kebab-case, matches filename (no .md)
description: "One sentence."   # specific enough for dispatch; no overlap with siblings
model: haiku|sonnet|opus       # see Model and Effort Calibration; default sonnet
effort: low|medium|high        # always explicit; never inherit session default
permissionMode: bypassPermissions  # default; use "default" only for sensitive ops
disallowedTools: Tool1, Tool2  # omit to inherit all; see Tool Policy
---
```

**`model`:** Default `sonnet`. Use `haiku` for mechanical triage agents. Use `opus` only when the task genuinely demands extended cross-document reasoning.

**`effort`:** Always explicit. Inheriting the session default is wrong in both directions: it overspends for triage agents and underpowers synthesis agents. See calibration matrix below.

**`permissionMode`:** Default `bypassPermissions`. Use `default` only when the agent's actions warrant per-call user approval.

**`disallowedTools`:** Omit to inherit all tools. Use to restrict specific tools when misuse is a real risk. See Tool Policy. Do not use an explicit `tools` allowlist — allowlists create surprises when an agent legitimately needs a tool that was not anticipated.

**`description`:** Specific enough that dispatch picks this agent and not a sibling. "Scores candidate file snippets for relevance to a query" beats "handles search."

---

## Model and Effort Calibration

Set both fields explicitly on every agent. Model determines capability tier; effort determines thinking budget within that tier.

### Model selection

| Task type | Model |
|---|---|
| Mechanical triage, light classification, relevance scoring | haiku |
| Routing decisions, validation, moderate reasoning | sonnet |
| Cross-document synthesis, complex reasoning, report generation | sonnet (default) or opus (caller override) |

In this system, the user's effort level at query time drives model selection for the synthesis agent at call time. The spec sets the default; the orchestrator (main session) overrides based on user input.

### Effort calibration (thinking budget within a model)

Classify each agent by two dimensions:

**Input size:**
- **Bounded** — compact JSON inputs, short script outputs, a handful of paths
- **Moderate** — 50–100 file paths, script results with metadata, a document or two
- **Large** — many documents, full corpus reads

**Reasoning depth:**
- **Low** — mechanical execution, file operations, structured data assembly
- **Medium** — evaluation against criteria, categorical classification, relevance judgment
- **High** — synthesis across multiple sources, argument construction, report generation

| | Low reasoning | Medium reasoning | High reasoning |
|---|---|---|---|
| **Bounded input** | low | medium | high |
| **Moderate input** | low | medium | high |
| **Large input** | low | medium | **high** (ceiling; thinking tokens compete with input past this) |

**Level definitions:**
- **`high`** — synthesis or judgment-intensive work with any input size. The ceiling for large-input agents.
- **`medium`** — evaluation, classification, careful reading. Most triage and routing work.
- **`low`** — mechanical or operational tasks. Explicit to prevent session-level inheritance from silently burning budget.

### This system's agents

| Agent | Model | Effort | Rationale |
|---|---|---|---|
| discovery-agent | haiku | medium | Relevance triage is light judgment; bounded per-snippet input |
| synthesis-agent | sonnet (default; caller overrides) | high | Cross-document synthesis; large input — high is the ceiling |

---

## Context Discipline

**The output contract is as important as the input contract.** Every boundary crossing between agents — or between an agent and the main session — is deliberate. Design output for the caller's context budget, not for completeness.

### Principles

**Return minimum sufficient for the caller's next action.** If the caller needs file paths to pass to synthesis, return those paths. Do not also return the snippets scored, the reasoning trace, or the files that were ruled out. Those stay inside the agent.

**Intermediate computations stay inside.** An agent that evaluated 80 snippets to produce 12 matches returns 12 matches, not 80 evaluations. An agent that read 20 files to produce a report returns the report, not the file contents.

**Bound list outputs.** Lists returned to callers are bounded: by the hit-count gate, by the effort-level threshold, or by an explicit max in the Output Contract. Never return an unbounded list.

**Write large output to a file; return the path.** Reports, cached file lists, analysis artifacts — write to the appropriate output directory and return a reference. The caller reads the file when needed rather than holding its content in context.

**On abort: return only what the caller needs to ask the user for refinement.** Count, session ID, folder breakdown. Not the file list.

---

## Scripts-First

**Agents are for orchestration and reasoning. Deterministic work is for scripts.**

### The rule

If a task can be expressed as a deterministic shell command — grep, hash, copy, check-existence, sort, count, dedup — it should be a script. The agent calls the script and reasons over the result.

The agent's Behavior section must name the specific script(s) called at each step. "Run `search-with-context --query X --folders Y` and evaluate the results" is correct. "Search the source folders for relevant content" is not — it leaves the mechanics to the agent.

### What scripts do vs. what agents do

| Scripts (deterministic) | Agents (reasoning) |
|---|---|
| grep source folders for terms | decide which terms to grep for |
| hash a file | decide whether a hash change is meaningful |
| check ledger for a path | decide what to do with a new vs existing file |
| compute folder breakdown from a file list | interpret the breakdown to guide a refinement prompt |
| copy a file to corpus | decide which files are worth ingesting |
| write a ledger entry | decide what metadata to record |

### Red flags

- The Behavior section describes what to do without naming a script that does it
- The agent is running `rg` or `grep` directly to find content (use `search-with-context`)
- The agent is computing a hash inline instead of calling `hash-file`
- The agent checks the ledger by reading a file directly instead of calling `ledger-lookup`

If a Behavior step is clearly deterministic but no script exists for it, that is a signal to add a script — not to let the agent do it inline.

---

## Tool Policy

**Default: inherit all tools. Restrict via `disallowedTools` when misuse is a credible risk.**

Do not use an explicit `tools` allowlist. Allowlists break when an agent legitimately needs a tool that was not anticipated (reading a config file, checking a path, etc.). Restriction via `disallowedTools` is narrower and safer.

Restrict a tool when:
- The agent must be read-only (restrict `Edit`, `Write`, `MultiEdit`)
- The agent should interact with the filesystem only through scripts, not direct reads (restrict `Read`, `Grep`, `Glob` to enforce scripts-first)
- There is a specific, credible risk of misuse in this agent's context

Do not restrict a tool on "probably won't need it" logic. If the use case is legitimate and possible, leave it available.

### This system's agents

| Agent | Restricted tools | Reason |
|---|---|---|
| discovery-agent | `Edit`, `Write`, `MultiEdit`, `Read`, `Grep`, `Glob` | Read-only; all search and read operations go through scripts |
| synthesis-agent | `Bash`, `Edit`, `Write`, `MultiEdit` | Reads corpus files with `Read` only; no scripts, no writes |

---

## Required Body Sections

### Role

One paragraph. What this agent is, what it does, and what it explicitly does NOT do. The negative statement guards against scope creep.

Pattern: "You are [role]. You [do X]. You do NOT [do Y or Z]."

### Input Contract

Every argument the agent accepts. For each: name, type, required/optional, format, valid values.

### Behavior

Numbered steps. Each step either:
- Calls a named script with explicit arguments, then reasons over the result
- Makes a decision and branches
- Returns a structured result to the caller

Do not write prose instructions to "search for X" or "check if Y" without naming the mechanism. Scripts are named, not described.

### Output Contract

Exact format of what is returned. Specify:
- Success case structure (field names, types, example shape)
- Abort or error case structure
- What is explicitly NOT in the output (state this when a caller might assume it is)

### Scripts Used

Every script this agent calls. For each: script name, purpose, argument signature. This is the agent's declared dependency on the script library.

### Constraints

Explicit limits the agent must not cross:
- Write paths (none, or specific paths only)
- Scope limits (only processes files within configured source paths)
- Judgment limits (does not decide X; returns Y for the caller to decide)

---

## Do

**Keep agents focused on a single responsibility.** If scope is growing, split. An agent that searches AND ingests AND synthesizes is unpredictable and hard to calibrate.

**Write explicit input and output contracts.** Ambiguous contracts are the primary source of wasted tool calls. If the shape is not documented, callers and agents will assume different things.

**Name scripts by name in the Behavior section.** The spec is a runbook. "Run `batch-ingest --paths [paths]`" is unambiguous. "Process the matched files" is not.

**One approach, not options.** When multiple paths exist, the agent evaluates and commits. Surfacing "Option A or B" puts work back on the caller. The only exception: a decision that requires information the agent cannot access (a user preference, a business rule not yet set, a config value that must come from outside).

**Enforce the output contract in the Behavior section.** If the contract says return only corpus paths, the Behavior instructions must say exactly that. Contract and instructions must be consistent.

**Write large artifacts to disk; return the path.** Reports, cached file lists, analysis outputs — write to the appropriate output directory and return a reference.

---

## Don't

**Don't do mechanical work inline when a script exists for it.** If `hash-file` exists, the agent calls `hash-file`. It does not run `md5sum` in Bash and parse the output.

**Don't volunteer output the caller did not ask for.** Extra fields pollute the caller's context. The output contract defines what goes back; the agent sticks to it.

**Don't retry failed operations automatically.** Report the failure and what was skipped. Let the caller or user decide whether to retry.

**Don't include environment-specific config.** WSL, path separators, `.exe` suffixes — these are covered by global rules. Repeating them in agent specs creates drift when the environment changes.

**Don't hardcode tool names.** Write "use the DB MCP tool in your available tools" not a specific MCP function name. Tool names change across environments.

**Don't narrate routine tool calls.** A brief note for genuinely significant steps is fine. Play-by-play inflates context with noise.

**Don't duplicate content from the system design doc.** Source config format, ledger schema, corpus layout, script contracts — those live in `.context/plans/`. Agent specs reference outcomes, not mechanisms.

---

## Parallel Execution

**Independent operations go in one message.** When the agent has N independent things to do — N script calls, N file reads — they go in a single tool-call batch. Sequential execution of independent work multiplies wall time for no benefit.

**Compound at the script layer first.** One `search-with-context --pattern "A|B|C"` beats three separate calls. Consolidate at the script level before considering agent-level parallelism.

**Batch all snippets into one evaluation prompt.** The discovery agent does not evaluate snippets one at a time. It collects all snippets from script output and scores them in a single prompt. One Haiku call for N snippets, not N calls.

**Parallelize at the agent layer only when reasoning is genuinely independent per unit.** The consolidation rule (one compound script) applies to deterministic work. Agent-level parallelism applies when separate reasoning jobs have no dependency on each other's output.

---

## Skill Spec Structure

Skills are slash commands that load orchestration instructions into the main session. They are not agents: they run in the caller's context with the caller's model.

### Skill spec sections

1. **Trigger** — when the user invokes this skill; what arguments it accepts
2. **Input parsing** — how to parse the command line (query, flags, optional session ID)
3. **Workflow** — numbered steps the main session executes, referencing agents and scripts by name
4. **Error handling** — what to do when an agent returns an abort signal, error, or unexpected result
5. **Context discipline** — explicit statement of what the main session retains and discards after each step

### Key differences from agent specs

- No frontmatter
- No Role section (it is a runbook, not an agent identity)
- Error handling is prominent: the skill is the caller's only safety net
- The Workflow section references agents and scripts by name, just like agent Behavior sections

### Context discipline in skills

The main session is the orchestrator. It must stay lean. The skill spec must state explicitly what survives each step:

- After invoking discovery-agent: retain the Output Contract result only (status + paths, or abort signal). Discard nothing else because nothing else crossed the boundary.
- After calling batch-ingest: retain the ingested corpus paths only.
- After invoking synthesis-agent: retain the report only.

Document these boundaries in the skill's Context Discipline section. Future edits to the skill must not widen them without deliberate consideration.

---

## Performance

**Don't re-read after Edit.** Edit returns the post-edit state. Re-reading wastes context. Only re-read if something other than your own edit could have changed the file.

**Read selectively on large files.** Use `offset` and `limit`. Do not load a 50 KB file to read 10 lines.

**Write to disk; return the path.** Large text artifacts go to disk. Do not route large content through the model's response.

---

## Maintenance

When adding or modifying these guidelines, scan every existing agent in `.claude/agents/` and bring it in line. Drift between this file and the agents defeats its purpose.

When adding a new script to the system, update the Scripts Used section of every agent that calls it.

When changing an agent's output contract, update every caller's input parsing to match.
