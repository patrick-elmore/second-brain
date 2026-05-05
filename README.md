# second-brain

A personal knowledge retrieval system. Indexes meeting transcripts, daily notes, planning docs, and other personal artifacts into a local SQLite/FTS5 database, then exposes them through a long-running HTTP MCP service that wraps a persistent Claude session.

You can hit the service two ways:
- **As a human**, via the `/brain` skill in any Claude Code session.
- **As an agent**, by calling MCP tools on the `second-brain` server directly. The service is the same; the skill is a thin wrapper.

If you are an agent reading this README to learn the interface, jump to [MCP tools](#mcp-tools). The persistent-session model is documented in [The persistent session](#the-persistent-session) — agents must understand it before invoking `ask` repeatedly.

---

## Architecture

```
┌─────────────────────────┐        ┌────────────────────────────────────┐
│  Claude Code session    │        │  SecondBrainHttpMcp (Windows svc)  │
│  /brain or direct MCP   │  HTTP  │  ASP.NET Core 10  •  port 9998     │
│  tool call              │ ─────▶ │  ┌──────────────────────────────┐  │
└─────────────────────────┘        │  │ MCP JSON-RPC handler         │  │
                                   │  │  ├─ search   (FTS5 only)     │  │
                                   │  │  ├─ ask      (persistent     │  │
                                   │  │  │           Claude session) │  │
                                   │  │  ├─ compact_session          │  │
                                   │  │  ├─ reset_session            │  │
                                   │  │  ├─ session_info             │  │
                                   │  │  ├─ get_request              │  │
                                   │  │  └─ rebuild_index            │  │
                                   │  └──────────────┬───────────────┘  │
                                   │                 │                  │
                                   │  ┌──────────────▼───────────────┐  │
                                   │  │  Persistent ClaudeSession    │  │
                                   │  │   • messages on disk         │  │
                                   │  │   • cache_control breakpoints│  │
                                   │  │   • internal tools:          │  │
                                   │  │       search, read_file      │  │
                                   │  └──────────────┬───────────────┘  │
                                   │  ┌──────────────────────────────┐  │
                                   │  │ IndexRefreshService          │  │
                                   │  │  • background loop           │  │
                                   │  │  • incremental every N sec   │  │
                                   │  └──────────────────────────────┘  │
                                   │                 │                  │
                                   │       ┌─────────┼─────────┐        │
                                   │       ▼         ▼         ▼        │
                                   │   ┌──────┐ ┌────────┐ ┌─────────┐  │
                                   │   │fts.db│ │requests│ │session- │  │
                                   │   │      │ │  .db   │ │state.json│ │
                                   │   └──────┘ └────────┘ └─────────┘  │
                                   └────────────────────────────────────┘
                                                    ▲
                                                    │ Claude API
                                                    │ (Vertex AI or
                                                    │  direct Anthropic)
```

- **Service**: `SecondBrainHttpMcp`, ASP.NET Core 10, listens on `0.0.0.0:9998`. Runs as a Windows service installed at `%LOCALAPPDATA%\SecondBrainMcpServer\`.
- **Index**: a SQLite database with one FTS5 virtual table for content + a regular `files` table for metadata. Built once by the `SecondBrain.IndexBuilder` console app, then read-only at query time.
- **Persistent session**: a single in-process `ClaudeSession` accumulates messages across every `ask` call. State is persisted to `index/session-state.json` after each call and restored on service restart.
- **Inference**: Claude via Vertex AI (default in this install) or direct Anthropic API. Routing decided by `CLAUDE_CODE_USE_VERTEX` environment variable.

---

## Quick start

### Human — slash command

```
/brain what did we decide about Atlas this week
/brain --effort medium summarize the last three weekly 1:1s with my manager
/brain search atlas --filter type:transcript --filter date:2026-04-01..
/brain info
```

The `/brain` skill at `~/.claude/skills/brain/SKILL.md` is a thin wrapper around the MCP tools — see [The skill](#the-skill).

### Agent — direct MCP

The `second-brain` MCP server is registered in `~/.claude.json` (added automatically by `install.ps1`). Once registered, an agent invokes tools using the standard `mcp__second-brain__<tool>` interface.

```jsonc
// Bare-minimum search
mcp__second-brain__search({ "query": "atlas requirements" })

// LLM-mediated synthesis
mcp__second-brain__ask({
  "question": "What did the team decide about Atlas requirements this week?",
  "effort": "medium"
})
```

The service is a long-running Windows daemon. It is always-on and stateful between calls — agents do not need to bootstrap or initialize it. They do, however, need to understand that `ask` shares state with every other ask: see [The persistent session](#the-persistent-session) before chaining ask calls.

---

## MCP tools

All seven tools are exposed via JSON-RPC at `POST /mcp`. The service implements the MCP `initialize`, `tools/list`, and `tools/call` methods. Every tool returns a `request_id` (search, ask) or status payload that callers can inspect later.

### `search` — deterministic FTS5

No LLM involvement. Runs an FTS5 query (or filter-only query) against `fts.db`, joins to `files` for metadata, and returns ranked hits with snippets.

| Param | Type | Default | Notes |
|---|---|---|---|
| `query` | string | none | FTS5 syntax. Optional if you only want filtered enumeration. |
| `date_start` | string | none | `YYYY-MM-DD`. Filters on `metadata.created`. |
| `date_end` | string | none | `YYYY-MM-DD`. Filters on `metadata.created`. |
| `people` | string[] | none | Substring match against `metadata.attendees` (array or scalar). |
| `source_type` | string[] | none | One of `transcript`, `note`, `1on1`, `standup`, `planning`. |
| `source_folders` | string[] | none | Restrict to specific source folder IDs (see `sources.json`). |
| `top` | integer | 30 | Result cap. |
| `snippet_tokens` | integer | 32 | Tokens of context per snippet. Clamped to `[1, 64]`. |
| `return_mode` | string | `snippets` | `snippets` or `paths`. `paths` returns no snippet text. |
| `list_sources` | boolean | false | When true, includes a `sources_summary` rollup grouping hits by `source_folder_id`. |

**FTS5 syntax cheat sheet** (porter unicode61 tokenizer — case-insensitive, English-stemmed):
- `atlas requirements` — both terms (implicit AND)
- `"atlas requirements"` — exact phrase
- `atlas OR sagemaker` — either
- `auth*` — prefix match
- `(login OR signin) flow` — grouping
- `atlas NEAR/3 requirement` — within 3 tokens

BM25 weights are biased toward the path column (10.0) over content (1.0), so a hit in the file path outranks a hit only in the body.

**Returns:**
```jsonc
{
  "request_id": "a1b2c3d4",
  "hits": [
    {
      "absolute_path": "C:\\data\\...\\2026-04-12 Standup.md",
      "relative_path": "Granola/Transcripts/2026-04-12 Standup.md",
      "source_folder_id": "personal-notes",
      "score": -8.42,                  // BM25; lower = more relevant
      "metadata": { "type": "standup", "attendees": ["..."], "created": "2026-04-12" },
      "matches": [{ "snippet": "...the team decided to <<atlas>> for inference..." }]
    }
  ],
  "sources_summary": [                 // present only when list_sources=true
    { "source_folder_id": "personal-notes", "hit_count": 7 }
  ]
}
```

### `ask` — persistent-session synthesis

Routes a question through the in-process Claude session. The session has its own internal tools (`search`, `read_file`) that the model invokes autonomously to find evidence and read full files. Synthesis text is returned as `synthesis`.

| Param | Type | Default | Notes |
|---|---|---|---|
| `question` | string | **required** | Natural-language query. |
| `compact_instruction` | string | none | If provided, the session is compacted with this instruction before answering. |
| `effort` | string | `low` | `low` / `medium` / `high`. See [Effort levels](#effort-levels). |

**Returns:**
```jsonc
{
  "request_id": "1f2a3b4c",
  "synthesis": "...markdown answer with [source: relative/path/to/file.md] citations...",
  "model_used": "claude-haiku-4-5",
  "tools_called": 4,                  // # of internal search/read_file calls
  "files_referenced": [               // every file the session opened during this ask
    "C:\\data\\...\\2026-04-12 Standup.md",
    "C:\\repos\\...\\.context\\atlas-decision.md"
  ]
}
```

`files_referenced` is the agent's audit trail — exactly which sources fed the synthesis, in absolute-path form. Pair it with `get_request` later if you need to revisit.

### `compact_session` — collapse history

Runs the session's prior conversation through the compaction model (`claude-sonnet-4-6` by default) and replaces the message list with a single summary. Preserves session continuity at lower token cost.

| Param | Type | Default | Notes |
|---|---|---|---|
| `instruction` | string | none | Additional steering for what to keep. The standard prompt is always applied first. |

**Returns:** `messages_before`, `messages_after`, `approximate_tokens_before`, `approximate_tokens_after`.

Compaction also fires automatically when `approximate_tokens` exceeds the threshold (default 150,000) at the start of an `ask`.

### `reset_session` — wipe state

Clears all messages and counters in memory, persists the empty state to disk. Use when starting a genuinely new line of inquiry where prior context would only confuse the model.

**Returns:** `{"status": "reset"}`.

### `session_info` — inspect

Returns metadata about the current persistent session: message count, approximate token count, current default model, and timestamps for `last_compacted`, `last_activity`, `state_persisted_at`.

### `get_request` — fetch a stored request

Both `search` and `ask` persist their request + response to `requests.db`. `get_request` retrieves a record by ID.

| Param | Type | Default | Notes |
|---|---|---|---|
| `request_id` | string | **required** | Returned by an earlier `search` or `ask`. |
| `fields` | string[] | all | Optional projection: any subset of `query`, `filters`, `timestamp`, `tool`, `files`, `synthesis`, `result_count`. |

**Returns:** the requested fields plus `request_id`. For `tool=search`, `files` is the ranked hit list at query time. For `tool=ask`, `synthesis` is the rendered answer and `files` is the post-hoc `files_referenced` capture.

### `rebuild_index` — refresh fts.db

Updates the FTS5 index in place against the current `sources.json`. Two modes:

| Param | Type | Default | Notes |
|---|---|---|---|
| `mode` | string | `incremental` | `incremental` walks every source folder, then adds new files, refreshes files whose mtime is newer than the indexed copy, and removes rows whose file no longer exists. `full` drops `files` + `files_fts` and rebuilds from scratch. |

If the index file doesn't exist or has no `files` table when `incremental` is requested, the call falls back to a full rebuild and the response reports `mode: "full (fallback)"`.

**Returns (incremental):**
```jsonc
{
  "mode": "incremental",
  "added": 3,
  "modified": 7,
  "removed": 1,
  "unchanged": 4823,
  "skipped": 0,           // failed reads (e.g., binary files newly placed in a source folder)
  "elapsed_seconds": 2.41,
  "db_path": "C:\\Users\\...\\index\\fts.db"
}
```

**Returns (full):**
```jsonc
{
  "mode": "full",
  "indexed": 4831,
  "skipped": 95,
  "elapsed_seconds": 64.12,
  "db_path": "C:\\Users\\...\\index\\fts.db"
}
```

The MCP handler's per-call mutex serialises rebuilds against `ask` and `search`, and the database uses WAL mode so search readers operating in other connections aren't blocked. Adding a *new* source folder to `sources.json` will be picked up by the rebuild, but the in-memory `FileReader`'s allowed-roots set is only refreshed at service start — restart the service after the rebuild if you've added a folder you want the LLM's `read_file` tool to be able to access.

---

## The persistent session

`ask` is fundamentally different from `search`. Treat it as a long-running chat, not a stateless RPC:

- **Every `ask` appends to the same conversation.** The model sees prior questions, prior tool results, and prior answers. Follow-up questions like "elaborate on the third point" or "narrow that to 2026" work without re-stating prior context.
- **The conversation is replayed on every call.** Each ask sends the entire message history to the API. Token count grows monotonically until compaction or reset.
- **Auto-compact at 150K tokens.** When `approximate_tokens` ≥ 150,000 at the start of an `ask`, compaction runs first. The full message log becomes a single summary message before the new question is appended.
- **Disk persistence is unconditional.** State is written to `index/session-state.json` after every `ask` and after every `compact`/`reset`. Restarting the service preserves the conversation.
- **Prompt caching is on.** Three `cache_control: ephemeral` breakpoints are placed per request: on the system prompt, on the last tool definition, and on the last message. Cache hits drop input cost by ~10× on Sonnet/Opus and ~10× on Haiku, but only fire above the per-model minimum prefix size (4096 tokens for Haiku 4.5, 1024 for Sonnet/Opus). Short conversations don't cache.
- **Internal tools are not the MCP tools.** Inside `ask`, the model uses its own `search` and `read_file` tools defined in `ToolDefinitions.cs`. These hit the same SearchEngine and FileReader as the MCP-level `search`, but they are invoked by the model, not the caller. Callers only see `tools_called` (a count) and `files_referenced` (paths) in the response.

Practical guidance for agents:

- **Group related questions in one session.** Ask the broad question first, then drill in. The model already has the context loaded.
- **Use `compact_session` between phases.** When you're done with one topic and moving to another, compact with an instruction like "keep the summary findings about Atlas; drop the search noise." Saves cost on subsequent calls.
- **Use `reset_session` when topics are unrelated.** Don't pollute a "performance review evidence" thread with a one-off "what's the office WiFi password" query.
- **Prefer `search` for one-shot lookups.** If you just need ranked file paths and snippets, `search` is cheaper, deterministic, and doesn't touch the session.

---

## Effort levels

The `effort` arg on `ask` selects the API thinking budget. All three tiers run on the default model (`claude-haiku-4-5`); only the thinking effort changes. The escalation model is reserved for compaction.

| `effort` | Model | API thinking effort | When to use |
|---|---|---|---|
| `low` (default) | `claude-haiku-4-5` | Low | Most queries. Fast and cheap; the model still searches, reads, and synthesizes — just with minimal deliberation. |
| `medium` | `claude-haiku-4-5` | Medium | When the question requires more deliberation (comparing perspectives across sources, weighing evidence). |
| `high` | `claude-haiku-4-5` | High | Long-form synthesis, performance-review style narratives, anything where output completeness matters more than latency. |

The model and effort are recorded in the response (`model_used`) and in `requests.db`. Per-model token usage is tracked in `/stats`.

---

## Source configuration

`config/sources.json` (in the repo root) defines what the IndexBuilder ingests. The same file is copied into the install dir's `config/` on first install, and the running service reads it from there.

Two entry shapes are supported:

### Static path

```json
{
  "id": "personal-notes",
  "path": "C:\\data\\your-data\\obsidian\\notes",
  "exclude_subfolders": [".obsidian"]
}
```

Indexes everything under `path`. Excluded subfolders are skipped at any depth.

### Dynamic discovery

```json
{
  "id": "repos-context",
  "discover": {
    "root": "C:\\repos",
    "directory_name": ".context",
    "max_depth": 4
  }
}
```

Walks `root` to `max_depth` directories deep, indexes every directory whose name matches `directory_name`. The same `id` is reused across every match — useful for grouping all `.context` folders across a workspace under one logical source.

### Currently configured sources

| ID | Type | Root |
|---|---|---|
| `repos-context` | discover `.context` | `C:\repos` |
| `misc-context` | discover `.context` | `C:\misc` |
| `your-data` | static (excludes `.obsidian`, `claude-docs`) | `C:\data\your-data` |

---

## The FTS5 index

Two SQLite databases live under `index/` in the install directory:

### `fts.db` — content index (drop-and-recreate)

```sql
CREATE TABLE files (
    id                INTEGER PRIMARY KEY,
    source_folder_id  TEXT NOT NULL,
    absolute_path     TEXT NOT NULL UNIQUE,
    relative_path     TEXT NOT NULL,
    size_bytes        INTEGER NOT NULL,
    mtime             REAL NOT NULL,
    indexed_at        TEXT NOT NULL,
    source_type       TEXT,           -- transcript, standup, 1on1, planning, note
    metadata          TEXT            -- JSON: parsed frontmatter
);

CREATE VIRTUAL TABLE files_fts USING fts5(
    path,                              -- relative_path; weight 10.0 in BM25
    content,                           -- file body; weight 1.0
    tokenize='porter unicode61'
);
```

Built by `SecondBrain.IndexBuilder.exe` in a single transaction. Files larger than 5 MB and binary files are skipped.

### Frontmatter parsing

Two formats are recognized when populating `source_type` and `metadata`:

1. **YAML frontmatter** — standard `---` block at the top of the file. `type:` field maps directly to `source_type`. `attendees:` populates the metadata for the `people` filter.
2. **Bold-header format** — `**Type:** transcript` / `**Attendees:** Alice, Bob` as the first lines of a file (Granola transcript convention).

When `type:` is absent, `source_type` is inferred from the title: `standup` → standup, `1:1`/`1on1`/`one-on-one` → 1on1, `planning` → planning, `transcript` → transcript.

### `requests.db` — query history (persisted)

```sql
CREATE TABLE requests (
    id            TEXT PRIMARY KEY,
    timestamp     TEXT NOT NULL,
    tool          TEXT NOT NULL,       -- "search" or "ask"
    query         TEXT,
    filters_json  TEXT,
    result_count  INTEGER NOT NULL,
    synthesis     TEXT                 -- only populated for ask
);

CREATE TABLE request_files (
    request_id        TEXT NOT NULL REFERENCES requests(id) ON DELETE CASCADE,
    rank              INTEGER NOT NULL,
    absolute_path     TEXT NOT NULL,
    relative_path     TEXT NOT NULL,
    source_folder_id  TEXT NOT NULL,
    score             REAL,
    PRIMARY KEY (request_id, rank)
);
```

Every `search` and `ask` writes a row. `get_request` reads from these tables.

### `session-state.json` — persistent ClaudeSession

JSON file containing the serialized message list, approximate token count, and last-compacted timestamp. Restored on service start; rewritten after every `ask` / `compact` / `reset`.

---

## HTTP endpoints

Beyond the MCP JSON-RPC endpoint, the service exposes three GETs for diagnostics:

| Method | Path | Purpose |
|---|---|---|
| POST | `/mcp` | JSON-RPC 2.0 entry point. Accepts `initialize`, `tools/list`, `tools/call`. |
| GET | `/health` | `{"status": "healthy", "service": "SecondBrainHttpMcp", "version": "1.0.0"}`. Returns 503 if the handler isn't ready. |
| GET | `/.well-known/mcp` | Discovery: protocol version, transport, endpoint URL. |
| GET | `/stats` | HTML dashboard summarizing per-model LLM usage (requests, tokens, cache hits, estimated USD cost via `pricing.json`), tool call counts (last 24h, by name), file read counts, **index state** (file count, total indexed bytes, db file size, last indexed-row timestamp, breakdown by source folder and source type), **auto-refresh activity** (refreshes since start, last run, last delta), and process memory. |
| GET | `/stats.json` | Same data as `/stats`, raw JSON for programmatic consumers. |

`/stats` is useful for monitoring cost. The dashboard surfaces total estimated USD and a per-model breakdown with `cache_creation_tokens` and `cache_read_tokens` so you can verify caching is firing. Use `/stats.json` for the same data as raw JSON.

---

## Configuration files

### `config/mcp_config.json` (per-install)

Service-level settings. Read by `Program.cs` at startup. Lives at `%LOCALAPPDATA%\SecondBrainMcpServer\mcp_config.json`.

Key fields:
- `service_name`, `display_name`, `description` — Windows service registration.
- `http_host`, `http_port` — listen address. Default `0.0.0.0:9998`.
- `mcp_timeout` — request timeout in seconds.
- `log_level` — `DEBUG`, `INFO`, `WARNING`, `ERROR`, `CRITICAL`. Logs go to `logs/second_brain_<timestamp>.log` next to the binary.
- `second_brain.default_model` — default Claude model (`claude-haiku-4-5`).
- `second_brain.escalation_model` — model used by the compactor (`claude-sonnet-4-6`). Not used by `ask`; all effort tiers run on `default_model`.
- `second_brain.compact_threshold_tokens` — auto-compact trigger (`150000`).
- `second_brain.fts_db_path`, `requests_db_path`, `session_state_path` — relative to install dir.
- `second_brain.sources_config` — points at `config/sources.json` in the install dir.
- `second_brain.index_max_bytes` — file-size cap for indexing (`5000000`).
- `second_brain.index_refresh_interval_seconds` — interval for the background incremental-refresh loop (`300` = every 5 minutes). Set to `0` to disable. The loop runs once on startup to catch drift, then on the configured cadence.
- `second_brain.vertex_base_url` — optional override for the Vertex endpoint. When non-empty, the service routes Vertex requests to this URL (e.g. `http://localhost:9996` for a local proxy) instead of the SDK's region-derived Google URL. Leave as `""` to use the default.

### `config/pricing.json` (versioned in repo)

USD per 1M tokens, per Claude model, with both `standard` and `large_context` (>200K input tokens) tiers. Used by `PricingTable` to compute the cost numbers in `/stats`.

### `config/sources.json` (versioned in repo)

The source folder list — see [Source configuration](#source-configuration).

### Environment variables (machine scope)

The service runs as `LocalSystem` and only sees machine-scope env vars.

| Variable | Required when | Purpose |
|---|---|---|
| `ANTHROPIC_API_KEY` | direct Anthropic API | Read by the SDK. |
| `CLAUDE_CODE_USE_VERTEX` | Vertex inference | Set to `1` to route through Vertex AI. |
| `ANTHROPIC_VERTEX_PROJECT_ID` | Vertex inference | GCP project ID. |
| `CLOUD_ML_REGION` | Vertex inference | Vertex region (`global` works for Claude). |
| `GOOGLE_APPLICATION_CREDENTIALS` | Vertex inference | Path to the service account or ADC JSON the service can read. `LocalSystem` cannot see user-scoped gcloud ADC files; either copy/symlink the file or use a service account. |

---

## Service operations

### Install (one-time)

From an admin PowerShell at `.tools/second-brain-mcp/`:

```powershell
.\install.ps1
```

Verifies .NET 10 SDK and ASP.NET Core 10 runtime, builds and publishes both `SecondBrain.Mcp` and `SecondBrain.IndexBuilder` to `%LOCALAPPDATA%\SecondBrainMcpServer\`, copies `config/mcp_config.json` (only on first install — preserves any local edits on subsequent runs), copies `pricing.json` and `sources.json`, registers the Windows service, and adds the `second-brain` entry to `~/.claude.json`.

After install, you must:
1. Set `ANTHROPIC_API_KEY` (or the Vertex env vars) at machine scope.
2. Build the index with `SecondBrain.IndexBuilder.exe <sources.json> <fts.db>`.
3. `net start SecondBrainHttpMcp`.

### Update (after code changes)

```powershell
.\update.ps1
```

Stops the service, rebuilds, redeploys, leaves config and index in place, restarts.

### Uninstall

```powershell
.\uninstall.ps1
```

Stops and removes the service, prompts before deleting the install directory.

### Rebuilding the index

A background loop inside the service (`IndexRefreshService`) runs an incremental update on startup and then every `index_refresh_interval_seconds` (default 300 = 5 minutes). For most use, you do not need to think about rebuilds — the index trails the filesystem by at most that interval. Set the interval to `0` in `mcp_config.json` to disable the loop.

For an immediate refresh, the MCP exposes the `rebuild_index` tool — see [`rebuild_index`](#rebuild_index--refresh-ftsdb). Two ways to invoke:

```
/brain rebuild                  # via the skill (incremental by default)
/brain rebuild full             # nuclear rebuild
```

Or directly:

```jsonc
mcp__second-brain__rebuild_index({})                    // incremental
mcp__second-brain__rebuild_index({ "mode": "full" })    // full
```

If you'd rather rebuild from a shell (e.g., from a scheduled job that doesn't talk MCP), the standalone console app still works:

```powershell
& "$env:LOCALAPPDATA\SecondBrainMcpServer\SecondBrain.IndexBuilder.exe" `
    "$env:LOCALAPPDATA\SecondBrainMcpServer\config\sources.json" `
    "$env:LOCALAPPDATA\SecondBrainMcpServer\index\fts.db"
```

The console app is full-rebuild only. WAL mode lets it run while the service is up — readers may briefly see partial data mid-rebuild; stop the service first if that matters for your use case.

### Restart cycle (without uninstall)

```powershell
net stop SecondBrainHttpMcp
net start SecondBrainHttpMcp
```

The persistent session reloads from `session-state.json` on start.

---

## The skill

`~/.claude/skills/brain/SKILL.md` is a thin parser that maps user input into MCP tool calls. The skill:

1. Splits the first token off as a subcommand (`ask`, `search`, `compact`, `reset`, `info`, `get`). Defaults to `ask`.
2. Strips known flags (`--effort`, `--filter`, `--top`, `--paths`, `--list-sources`, `--fields`).
3. Dispatches to `mcp__second-brain__<tool>` with the parsed args.
4. Renders the response in a human-readable format.

Subcommands are 1:1 with MCP tools. The skill exists for convenience — the underlying capability is identical to direct MCP invocation.

A staging copy lives at `.claude/skills/beta-brain/SKILL.md` in this repo. Edit-validate-promote loop: change `beta-brain`, exercise it via `/beta-brain`, then `cp` over the global `~/.claude/skills/brain/SKILL.md` to promote.

---

## Repository layout

```
README.md                              this file
CLAUDE.md                              project-level guidance for Claude Code

config/
  sources.json                         source folder definitions

.claude/
  skills/beta-brain/SKILL.md           staging copy of the global /brain skill

.tools/second-brain-mcp/               .NET solution
  second-brain-mcp.slnx
  install.ps1, update.ps1, uninstall.ps1
  config/
    mcp_config.json                    template; copied to install dir on first install
    pricing.json                       per-model USD pricing for cost tracking
  src/
    SecondBrain.Files/                 source folder enumeration, file reading, frontmatter parsing
    SecondBrain.Index/                 FTS5 schema, search engine, request history
    SecondBrain.IndexBuilder/          console app: rebuild fts.db
    SecondBrain.Llm/                   ClaudeSession, ToolLoop, Compactor, system prompts
    SecondBrain.Mcp/                   ASP.NET Core host, MCP handler, /mcp + /health + /stats endpoints
    SecondBrain.{Files,Index,Llm}.Tests/  xUnit test projects

(install location, gitignored, machine-local)
%LOCALAPPDATA%\SecondBrainMcpServer\
  SecondBrain.Mcp.exe                  the service binary
  SecondBrain.IndexBuilder.exe         the indexer binary
  mcp_config.json                      live service config
  config/
    sources.json                       live source folder definitions
    pricing.json                       live pricing data
  index/
    fts.db                             FTS5 content index
    requests.db                        request/response history
    session-state.json                 persistent ClaudeSession state
    stats.json                         persisted /stats counters
  logs/
    second_brain_*.log                 Serilog output
```

The legacy Python `scripts/` directory and pre-MCP `index/` and `tmp/` folders at the repo root are vestigial — they are gitignored but harmless. The .NET service does not read them.
