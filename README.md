# second-brain

A personal knowledge retrieval system built on SQLite FTS5 and Claude Code agents. Given a natural-language query, it searches a BM25-ranked index across configured source folders, scores results for relevance, and synthesizes a structured report — all driven by a single Claude Code skill invocation.

## Quick start

```
/brain what were the action items from the rollout meeting
/brain --effort high what has the team said about Atlas requirements
/brain --effort low uvw lite
```

Run from a Claude Code session opened in this repo. The `--effort` flag scales thoroughness and cost (see [Effort levels](#effort-levels)).

---

## Setup

### Prerequisites

- Claude Code
- Python 3.8+ (stdlib only — no pip packages required)
- SQLite with FTS5 support (verified present: Python 3 + SQLite 3.41+)

### First run

**1. Configure sources** — edit `config/sources.json` to point at your source folders (see [Source configuration](#source-configuration)).

**2. Build the index** — run once from the repo root:

```bash
python3 scripts/index-build.py
```

This walks all configured sources, indexes every UTF-8 text file under 5 MB into `index/fts.db`, and prints a JSON summary:

```json
{"indexed": 4775, "skipped_size": 1, "skipped_binary": 94, "total_bytes_indexed": 74001654, "elapsed_seconds": 67.35, "db_path": "index/fts.db"}
```

The index is gitignored and must be rebuilt on each machine. Rebuild whenever source content changes significantly (no incremental sync — it's a full rebuild).

**3. Open Claude Code** in this repo and invoke `/brain`.

---

## Usage

```
/brain [--effort low|moderate|high] <query>
```

| Flag | Default | Effect |
|---|---|---|
| `--effort low` | | Fast, cheap, narrow. 15 candidates, 3-file cap, 200-word output. |
| `--effort moderate` | ✓ | Balanced. 30 candidates, 8-file cap, 600-word output. |
| `--effort high` | | Thorough. 50 candidates, unlimited files, full synthesis. |

Everything downstream scales from effort: search breadth, snippet context given to the scorer, how many files pass through to synthesis, and how long the output is allowed to be. See [Effort levels](#effort-levels) for the complete table.

---

## Source configuration

`config/sources.json` is an array of source entries. Two entry types are supported:

### Static path

```json
{
  "id": "my-notes",
  "path": "/absolute/path/to/notes",
  "exclude_subfolders": [".obsidian"]
}
```

Indexes everything under `path`, optionally excluding named subdirectories.

### Dynamic discovery

```json
{
  "id": "repos-context",
  "discover": {
    "root": "/mnt/c/repos",
    "directory_name": ".context",
    "max_depth": 4
  }
}
```

Walks `root` up to `max_depth` directories deep and indexes every folder named `directory_name` it finds. Each discovered folder gets an ID in the format `repos-context:<relative-path>` (e.g., `repos-context:my-project/.context`).

Both types coexist in the same config file. `lib.py` handles expansion at runtime, resolving dynamic entries to a flat list of concrete source paths before any indexing or searching begins.

### Current sources

| ID | Type | Root |
|---|---|---|
| `repos-context` | discover `.context` | `/mnt/c/repos` |
| `misc-context` | discover `.context` | `/mnt/c/misc` |
| `personal-notes` | static | `/mnt/c/data/your-data/obsidian/notes` |

---

## The FTS5 index

The index lives at `index/fts.db` — a single SQLite database file. It is gitignored and machine-local.

### Schema

```sql
CREATE TABLE files (
    id                INTEGER PRIMARY KEY,
    source_folder_id  TEXT NOT NULL,      -- e.g. "repos-context:my-project/.context"
    absolute_path     TEXT NOT NULL UNIQUE,
    relative_path     TEXT NOT NULL,      -- relative to the source root
    size_bytes        INTEGER NOT NULL,
    mtime             REAL NOT NULL,
    indexed_at        TEXT NOT NULL
);

CREATE INDEX idx_files_source ON files(source_folder_id);

CREATE VIRTUAL TABLE files_fts USING fts5(
    path,       -- the relative path, searchable as text
    content,    -- full file body
    tokenize='porter unicode61'
);
```

`files` and `files_fts` share the same rowid. Searching `files_fts` with a `JOIN` on `files` returns full metadata alongside BM25 scores and FTS5-generated snippets.

### Tokenizer

`porter unicode61` applies two things:

- **unicode61**: case-folding and diacritic normalization. Queries are case-insensitive by default — no need for alternations.
- **porter**: English stemming. `running` matches `run`, `runner`, `runs`. `authentication` matches `authenticate`, `authenticated`.

### Query syntax

The pipeline uses FTS5 MATCH syntax, not regex:

| Syntax | Meaning |
|---|---|
| `uvw lite` | Both terms must appear (implicit AND) |
| `"uvw lite"` | Exact phrase |
| `uvw OR lite` | Either term |
| `deploy*` | Prefix match: deploy, deployment, deploying |
| `(auth OR login) token*` | Grouping |

Regex syntax (`.`, `.*`, `[A-Z]`, `\|` alternation) is not valid here.

---

## Pipeline

Every `/brain` invocation runs this sequence in the main Claude Code session:

```
/brain <query>
    │
    ├─ 1. Parse flags & validate prerequisites
    │
    ├─ 2. Generate FTS5 query strings (inline, main session)
    │
    ├─ 3. index-search.py  ──────────────────────────────── BM25-ranked results
    │       --patterns <p>  --top <N>  --snippet-tokens <T>
    │
    ├─ 4. [optional second pass if hits < 3 and effort ≠ low]
    │       index-search.py + merge-search-results.py
    │
    ├─ 5. relevance-scorer agent ────────────────────────── filtered match list
    │       hits_data: <inline JSON>
    │       effort: low|moderate|high
    │       max_matches: 3|8|unlimited
    │
    └─ 6. synthesis-agent ───────────────────────────────── markdown report
            matches: [{source_path, relative_path, voice_source}]
            effort: low|moderate|high
            word_budget: 200|600|omit
```

### Step 1 — Parse and validate

Extracts `--effort` (default: `moderate`) and query text from `$ARGUMENTS`. Verifies `config/sources.json` is non-empty and `index/fts.db` exists. Sweeps stale session files from `tmp/sessions/`.

### Step 2 — FTS5 query generation (inline)

The main session generates FTS5 query strings directly, without spawning a subagent. This saves ~3 seconds and ~29K tokens compared to the original search-planner agent design. Pattern count is effort-scaled: 1 pattern at low, 1-2 at moderate, 1-3 at high.

The main session (Opus) is capable enough to produce valid FTS5 syntax. Explicit rules in the skill prevent regex-style patterns, which would trigger the sanitizer fallback in `index-search.py` and produce degraded results.

### Step 3 — BM25 search

`scripts/index-search.py` executes the FTS5 MATCH query, joins results against the `files` table, and returns a JSON array sorted by BM25 score (most relevant first). Each entry carries `source_folder_id`, `absolute_path`, `relative_path`, `score`, and a `matches` array with one snippet excerpt.

BM25 score is negative — more negative means more relevant. The `LIMIT` clause caps results at the effort-scaled `--top` value.

Multiple patterns are combined with OR: `(pattern1) OR (pattern2)`. If FTS5 rejects the query (syntax error), `index-search.py` falls back to a sanitized bare-word version of the query.

### Step 4 — Second-pass search (conditional)

If the primary search returns fewer than 3 hits and effort is not `low`, the main session generates broader alternative queries and runs a second search. Results are merged using `merge-search-results.py`, which deduplicates by `absolute_path` and combines match excerpts.

At `effort=low`, the second pass is skipped entirely — if the first search finds nothing, the pipeline stops.

### Step 5 — Relevance scoring

The `relevance-scorer` agent (Haiku) receives the full hits JSON inline in its prompt — no file read required. It scores each file against the query using the snippet context, assigns a category, and returns only the files that clear the effort-level threshold:

| Effort | Threshold | Max returned |
|---|---|---|
| low | `highly_relevant` only | 3 |
| moderate | `highly_relevant` + `relevant` | 8 |
| high | anything above `not_relevant` | unlimited |

When `max_matches` is set and more files qualify than the cap allows, the scorer returns the highest-category files first.

The scorer also opportunistically notes `source_type` and `voice_source` per file when the snippet makes it obvious (e.g., transcript formatting). The synthesis agent uses `voice_source` to apply extra skepticism to unusual proper nouns and spellings.

### Step 6 — Synthesis

The `synthesis-agent` (Haiku / Sonnet / Opus, determined by effort) reads each matched source file directly from its original path on disk. No intermediate copies. It synthesizes a markdown report with inline citations (`[source: relative_path]`) and a trailing `## Sources` section listing absolute paths.

Synthesis output is constrained by `word_budget`:

| Effort | Model | Word budget | Format |
|---|---|---|---|
| low | Haiku | 200 | Flat prose, no headers |
| moderate | Sonnet | 600 | Headers acceptable |
| high | Opus | None | Full structure, comprehensive |

---

## Effort levels

All six dimensions scale together:

| Dimension | low | moderate | high |
|---|---|---|---|
| Search candidates (`--top`) | 15 | 30 | 50 |
| Snippet context (`--snippet-tokens`) | 16 | 32 | 64 |
| FTS5 query patterns | 1 | 1-2 | 1-3 |
| Second-pass search | off | on | on |
| Scorer output cap | 3 files | 8 files | unlimited |
| Synthesis word budget | 200, flat prose | 600 | none |
| Synthesis model | Haiku | Sonnet | Opus |

Degradation at low effort is intentional. You get what you ask for.

---

## Agents

Agent specs live in `.claude/agents/`. Authoring conventions are documented in `.claude/agents/AGENT-GUIDELINES.md`.

### `relevance-scorer`

**Model:** Haiku | **Effort:** medium

Receives the BM25 search results as inline JSON in its prompt. Evaluates each file's snippet against the query. Returns a filtered, categorized match list. Never reads files from disk — the snippet context in the hits data is all it sees.

Input: `query`, `hits_data` (JSON inline), `effort`, `max_matches` (optional cap).
Output: `{matches: [{source_folder_id, absolute_path, relative_path, category, source_type, voice_source}]}`

Disallowed tools: `Read, Write, Edit, MultiEdit, Bash, Grep, Glob` — it is a pure reasoning step over data already in the prompt.

### `synthesis-agent`

**Model:** Sonnet (default; caller overrides per effort) | **Effort:** high

Reads matched source files from their original on-disk paths using `Read`. Synthesizes a markdown report with inline citations. Applies voice-source skepticism when `voice_source: true`. Enforces the word budget and format constraints passed by the caller.

Input: `query`, `matches` (array of `{source_path, relative_path, voice_source}`), `effort`, `word_budget` (optional).
Output: Markdown report with `[source: relative_path]` inline citations and `## Sources` section.

Disallowed tools: `Bash, Edit, Write, MultiEdit` — reads source files only, produces no side effects.

---

## Scripts

All scripts in `scripts/` are Python 3 stdlib only. Each prints JSON to stdout and errors to stderr with a non-zero exit code on failure. All are invoked from the repo root.

### `index-build.py`

Full rebuild of the FTS5 index. Drops and recreates `index/fts.db`. Walks all configured sources, skips binary files and files over `--max-bytes` (default 5 MB), indexes everything else. Single transaction for bulk insert performance.

```bash
python3 scripts/index-build.py [--config config/sources.json] [--db index/fts.db] [--max-bytes 5000000] [--verbose]
```

Output: `{indexed, skipped_size, skipped_binary, total_bytes_indexed, elapsed_seconds, db_path}`

### `index-search.py`

BM25-ranked search over the FTS5 index. Joins `files_fts` with `files` to return full metadata. Handles FTS5 syntax errors by falling back to a sanitized bare-word query. Reads the database in read-only mode.

```bash
python3 scripts/index-search.py --patterns <p> [--patterns <p>...] [--top 50] [--snippet-tokens 32] [--db index/fts.db]
```

Output: JSON array of `{source_folder_id, absolute_path, relative_path, score, matches: [{line, snippet}]}`

Exit codes: 0 = success, 2 = index missing, 3 = unrecoverable FTS5 syntax error.

### `scan-sources.py`

Enumerates all files across configured sources without indexing. Useful for auditing what the index would cover.

```bash
python3 scripts/scan-sources.py [--config config/sources.json] [--folder-id <id>]
```

Output: JSON array of `{source_folder_id, absolute_path, relative_path}`

### `merge-search-results.py`

Merges two or more search result files, deduplicating by `absolute_path`. When the same file appears in multiple result sets, match excerpts are combined.

```bash
python3 scripts/merge-search-results.py <file1.json> <file2.json> [...]
```

Output: Merged JSON array.

### `lib.py`

Shared module. Provides `load_expanded_config(config_path)` — loads `sources.json` and expands dynamic discover entries into concrete source paths. Provides `filter_by_folder_id(sources, folder_id)` — filters expanded sources by exact ID or discover-group prefix. Imported by all scripts that need source configuration.

### `session-state-write.py` / `session-state-read.py` / `session-state-sweep.py`

Persist, load, and expire named JSON blobs under `tmp/sessions/`. Sessions are 8-character hex IDs. `session-state-sweep.py` is called at the start of every `/brain` invocation to clean up files older than 7 days.

### `folder-summary.py`

Groups a file list by parent directory and returns the top-N folders by hit count. Used for debugging search result distributions.

```bash
python3 scripts/folder-summary.py [--top 5] < file-list.json
```

Output: JSON array of `{folder, count}` sorted descending.

---

## Repository layout

```
.claude/
  agents/
    AGENT-GUIDELINES.md     Authoring standards for all agent and skill specs
    relevance-scorer.md     Relevance scoring agent spec
    synthesis-agent.md      Report synthesis agent spec
    search-planner.md       Legacy — no longer used by the pipeline
  skills/
    brain/
      SKILL.md              /brain skill — main pipeline orchestration
  hooks/                    Claude Code hook configuration
config/
  sources.json              Source folder definitions
scripts/
  index-build.py            Build the FTS5 index (run once)
  index-search.py           BM25 search over the index
  scan-sources.py           Enumerate source files
  merge-search-results.py   Merge and deduplicate search result sets
  lib.py                    Shared config utilities
  search-with-context.py    Legacy grep-based search — superseded by index-search.py
  session-state-write.py    Persist session file lists
  session-state-read.py     Load persisted session file lists
  session-state-sweep.py    Delete stale session files
  folder-summary.py         Group file paths by parent folder, return top-N counts
index/
  fts.db                    SQLite FTS5 database (gitignored, machine-local)
tmp/
  sessions/                 Session file lists (gitignored)
  search-primary.json       Working file for current search pass
  search-secondary.json     Working file for second-pass search
  search-merged.json        Merged results when second-pass ran
```

---

## Maintenance

### Rebuilding the index

The index is a full snapshot. There is no incremental sync. Rebuild it when:
- Source files have changed significantly
- New source folders were added to `config/sources.json`
- The index is corrupt or missing

```bash
python3 scripts/index-build.py
```

On the current corpus (~4,800 files, ~74 MB of text), a full rebuild takes about 67 seconds.

### Adding a source

Add an entry to `config/sources.json`, then rebuild the index. The new source is searchable immediately after the rebuild.

### Checking what's indexed

```bash
python3 scripts/scan-sources.py | python3 -c "import json,sys; d=json.load(sys.stdin); print(f'{len(d)} files')"
```

Or inspect the database directly:

```bash
python3 -c "import sqlite3; c=sqlite3.connect('index/fts.db'); print(c.execute('SELECT COUNT(*) FROM files').fetchone()[0], 'files')"
```

---

## Design notes

**No corpus copies.** Earlier versions copied matched files into a local `corpus/` directory (hash-named) and maintained a `ledger/entries.json` registry. This was eliminated when FTS5 replaced grep as the search mechanism. The synthesis agent now reads source files directly from their original paths. The FTS5 index serves as the file registry.

**Scripts for deterministic work, agents for reasoning.** The pipeline enforces a strict boundary. BM25 search, result merging, session state persistence — all scripts. Relevance judgment and report synthesis — agents. Agents never grep, hash, or write files directly.

**Context discipline.** The main session stays lean by design. It reads the hits JSON once (to pass inline to the scorer), then discards it. It never reads source file bodies. Snippet content is confined to the scorer; file content is confined to the synthesis agent.

**Effort as a first-class parameter.** Every stage in the pipeline is parameterized by effort. Widening a query from 1 pattern to 3, deepening snippet context from 16 to 64 tokens, uncapping the scorer output, and removing the word budget all happen together when effort increases. The levels are designed to feel meaningfully different, not just cosmetically different.
