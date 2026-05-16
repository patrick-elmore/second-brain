# CLAUDE.md

Project instructions for the Second Brain MCP server.

## Configuration over hardcoded values

Tunable values must come from configuration, not hardcoded constants in code. The pattern:

- The value lives on `SecondBrainSettings` (or another settings class) with a `[JsonPropertyName(...)]` and a default
- The runtime reads it from `mcp_config.json` via `McpSettings.Load(...)`
- Code that consumes the value reads it from the loaded settings, not a literal in the call site
- A literal fallback at the consumer site is only acceptable when the consumer cannot reach the settings object (e.g., a separate executable that doesn't reference `SecondBrain.Mcp`); in that case, parse `mcp_config.json` inline and keep the literal as a last-resort fallback

Examples of values that belong in config: byte caps, timeouts, retry counts, batch sizes, refresh intervals, model names, file-size thresholds, port numbers.

When you find a hardcoded constant that fits this pattern, lift it to config rather than tolerating it. Hardcoded constants drift from their config counterparts and become a source of "why is the deployed behavior different from the configured behavior" bugs.

## Genericize all examples in source-controlled files

When writing any content to a source-controlled file — prompt text, inline examples, comments, docstrings, test fixtures, or documentation — always use generic placeholder names and terms. Never use real names of teammates, managers, customers, internal product codenames, internal acronyms, or organization-specific terminology, even as illustrative examples.

Generic replacements to default to:
- **People**: Alice, Bob, Carol, Dave (or similar clearly fictional names)
- **Companies**: Acme, Example Corp
- **Projects / codenames**: use descriptive generic terms ("the legacy service", "the migration project")
- **Internal acronyms / methodologies**: spell out a generic equivalent or omit

This applies everywhere in committed files: C# string literals, SQL query strings, prompt templates, XML/JSON examples, markdown docs, and test data. The commit scan is a safety net — genericizing at write time is the correct prevention.

## Plans

Write plans to `.context/plans/` in the repo root. No need to ask.

## Scan commits for personal or identifying content

This is a public repo. Before any `git commit`, scan the staged diff for content that ties the commit to a specific person, employer, project, or local environment. The concern isn't security — it's keeping the repo clean and portable for anyone reading or forking it.

Run a quick check against staged files for things like:

- **Personal identifiers**: names of teammates, managers, customers, the operator
- **Employer / domain identifiers**: company names, internal product codenames, team names, customer names
- **Local paths**: absolute Windows or POSIX paths, user profile folders, home directories
- **Personal corpus vocabulary**: meeting names, internal acronyms, voice-to-text aliases, project codenames that only make sense inside one organization
- **Real test fixtures**: dates, work item numbers, ticket IDs, repo names from the operator's actual work
- **Calendar / time references** that pin the commit to a specific person's schedule (1:1 cadence, sprint dates from one team)

Where to look:
- Source files (`.cs`, `.ps1`, `.md`, `.json`) — particularly templates, examples, comments, test fixtures
- Documentation (`README.md`, in-repo guides) — the place where personal worked examples leak in most often
- Test data and fixtures — generic placeholders (Phoenix, Alex, Acme) over real names
- Sample config files — never the live personal copy

If you find something, either generalize it (replace with a placeholder), move it to a gitignored location (`Prompts.local/`, `config/<live>.json`, `.context/`), or pull it from the commit. Templates committed to source control should describe the *shape* of personal data, never carry the data itself.

The existing template/live pattern (see [Templates and live overrides](README.md#templates-and-live-overrides) in the README) is the standard mechanism: anything personal lives in a gitignored live file; the committed template is generic and bootstraps the live file on first run. When adding new personal-data surfaces, extend that pattern rather than inventing a new one.

Before committing, run `git diff --cached` and evaluate the staged diff for all of the above, plus API keys, tokens, secrets, and passwords. If the diff is clean, prefix the commit command with `COMMIT_SCANNED=1`:

```
COMMIT_SCANNED=1 git commit -m "message"
```

A project hook enforces this — any commit command without the prefix will be blocked and the reason injected back into context.
