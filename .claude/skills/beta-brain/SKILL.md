---
name: beta-brain
description: Beta/testing copy of the brain skill. Identical interface to /brain but used to validate changes before promoting to the global ~/.claude/skills/brain/SKILL.md. Query your second brain MCP. Subcommands cover search, ask, session management, and request retrieval.
argument-hint: "[ask|search|compact|reset|info|get|rebuild] [--effort low|medium|high] [--filter date:YYYY-MM-DD..YYYY-MM-DD] [--filter people:name] [--filter type:transcript] [--filter folder:id] [--top N] [--paths] [--list-sources] [--fields f1,f2] <text>"
---

# /beta-brain skill

Pre-promotion staging copy of `/brain`. Identical behavior — edit this version, validate it,
then copy to `~/.claude/skills/brain/SKILL.md` to promote globally.

Thin wrapper around the seven tools on the `second-brain` MCP. Pick a subcommand;
the rest of the input is the query/argument. Defaults to `ask` when no subcommand
is recognized.

## Subcommands

| Subcommand | MCP tool | Purpose |
|---|---|---|
| `ask` (default) | `ask` | LLM-mediated synthesis using the persistent session |
| `search` | `search` | Deterministic FTS5 search; returns file paths and snippets |
| `compact` | `compact_session` | Force the session to compact |
| `reset` | `reset_session` | Wipe session state |
| `info` | `session_info` | Report current session metadata |
| `get` | `get_request` | Fetch a stored request entity by ID |
| `rebuild` | `rebuild_index` | Update the FTS index (incremental by default; pass `full` for nuclear rebuild) |

## Workflow

### Step 1: Parse

Treat the first whitespace-delimited token as a subcommand if it matches
`ask|search|compact|reset|info|get|rebuild`. Otherwise treat the entire input as
the query for `ask`.

Parse the following flags from the remaining text. Anything left after flags
are removed is the positional argument (the query, instruction, or request_id).

**Common:**
- `--effort low|medium|high` — `ask` only. Maps to MCP `effort` parameter.

**`search`-only:**
- `--filter date:YYYY-MM-DD..YYYY-MM-DD` — sets both `date_start` and `date_end`. Either side may be empty (`..2026-04-30` or `2026-04-01..`).
- `--filter people:<value>` — appends to `people` array. Repeatable.
- `--filter type:<value>` — appends to `source_type` array. Repeatable. Values: `transcript`, `note`, `standup`, `1on1`, `planning`.
- `--filter folder:<value>` — appends to `source_folders` array. Repeatable.
- `--top N` — limits results (default 30).
- `--paths` — sets `return_mode=paths` (no snippets).
- `--list-sources` — sets `list_sources=true`.

**`get`-only:**
- `--fields f1,f2,...` — comma-separated field list (`query`, `filters`, `timestamp`, `tool`, `files`, `synthesis`, `result_count`).

**`rebuild`-only:**
- The positional argument selects mode. `full` → full rebuild. Anything else (or empty) → incremental.

If the user typed `--effort` (any value) on a non-`ask` call, ignore it.

### Step 2: Dispatch

Call the matching MCP tool with the parsed arguments.

| Subcommand | Tool call |
|---|---|
| `ask` | `mcp__second-brain__ask` with `{ question, effort? }` |
| `search` | `mcp__second-brain__search` with the assembled params |
| `compact` | `mcp__second-brain__compact_session` with `{ instruction? }` (the positional text) |
| `reset` | `mcp__second-brain__reset_session` |
| `info` | `mcp__second-brain__session_info` |
| `get` | `mcp__second-brain__get_request` with `{ request_id, fields? }` |
| `rebuild` | `mcp__second-brain__rebuild_index` with `{ mode? }` (`"full"` if positional arg is `full`, else omit for default `incremental`) |

### Step 3: Display

The output should be useful to both a human reading the session and an agent
parsing the response. Always append the `request_id` so the user can fetch the
record later with `/brain get <id>`.

**`ask`:**
1. Display the `synthesis` field as-is.
2. Append a `### Sources` section listing each `files_referenced` entry as a bullet (if non-empty).
3. Append a footer: `_request_id: {request_id} · model: {model_used} · tools_called: {tools_called}_`

**`search`:**
1. For each hit, render: `- **{relative_path}** _(score: {score:.2f})_` then if a snippet exists, render the snippet on the next line indented as a blockquote.
2. If `sources_summary` is present, append a `### Source folders` section listing each as `- {source_folder_id}: {hit_count}`.
3. Footer: `_request_id: {request_id} · {hits.length} hits_`.

**`compact`:** Render the four counts: `Messages: {before} → {after}, Tokens: {before} → {after}`.

**`reset`:** Render `Session reset.`

**`info`:** Render each field on its own line in `key: value` form.

**`get`:** Pretty-print the returned entity as JSON in a code block.

**`rebuild`:** Render `Mode: {mode} · added: {added} · modified: {modified} · removed: {removed} · unchanged: {unchanged} · skipped: {skipped} ({elapsed_seconds}s)` for incremental, or `Mode: full · indexed: {indexed} · skipped: {skipped} ({elapsed_seconds}s)` for full.

## Errors

- If the MCP server is unavailable, reply: `The second-brain MCP service is not running. Start it with: net start SecondBrainHttpMcp`
- If the user passes an unknown subcommand and the rest of the text is empty, reply with the usage line from `argument-hint`.
- If the user passes a flag that doesn't apply to the chosen subcommand, ignore the flag silently.

## Examples

```
/brain what did we decide about Atlas this week
   → ask, default effort=low

/brain --effort medium summarize the last three weekly 1:1s with my manager
   → ask, effort=medium

/brain search atlas --filter type:transcript --filter date:2026-04-01..
   → search with type=transcript and date_start=2026-04-01

/brain search inspection --top 5 --paths
   → search, paths only, top 5

/brain compact keep findings about UVW Light, drop search noise
   → compact_session with custom instruction

/brain reset
   → reset_session

/brain info
   → session_info

/brain get f93e220f
   → get_request for that ID, all fields

/brain get f93e220f --fields synthesis,files
   → get_request for that ID, only synthesis and files

/brain rebuild
   → rebuild_index, mode=incremental (default)

/brain rebuild full
   → rebuild_index, mode=full
```
