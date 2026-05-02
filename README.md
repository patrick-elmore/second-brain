# second-brain

A personal knowledge retrieval system. Searches configured source folders (meeting transcripts, notes, project artifacts, reports), ingests relevant files into a local corpus, and synthesizes a report in response to natural-language queries.

## Usage

```
/brain [--effort low|moderate|high] [--force] [--session-id ID] <query>
```

Default effort is `moderate`. Use `--force` to bypass the hit-count gate. Use `--session-id` to refine a previous query that returned too many results.

## Configuration

Edit `config/sources.json` to point at your source folders:

```json
[
  {
    "id": "my-notes",
    "path": "/absolute/path/to/notes",
    "exclude_subfolders": [".obsidian"]
  }
]
```

## Structure

```
config/          Source folder configuration
corpus/          Copied source files (auto-created)
ledger/          Ingestion metadata (auto-created)
scripts/         Deterministic pipeline operations
tmp/sessions/    Refinement session state (auto-created, gitignored)
.claude/agents/  Agent specs
.claude/skills/  Skill specs (entry points)
```

## Design

See `.context/plans/system-design.md` for the full system design.
See `.context/plans/implementation-plan.md` for the build plan.
