# Aliases (template)

This file is a generic template. The live system never reads this file
directly. On startup, if `Prompts.local/aliases.md` does not exist, the
application copies this template there. After that, edit
`Prompts.local/aliases.md` to match your corpus.

`Prompts.local/` is gitignored. Your real aliases are never committed.

## Format

One group per line. Members are separated by ` ↔ ` (Unicode U+2194). The
order doesn't matter; the LLM treats every member of a group as equivalent
when constructing search queries.

## Categories

Organize by category for readability. Suggested categories below — adjust to
fit your corpus.

### People

- Full Name ↔ Nickname ↔ email-prefix ↔ account-suffix
- Jane Q Public ↔ Jane ↔ jane.public ↔ jpublic

### Products and systems

- Internal Codename ↔ Public Name ↔ Common Abbreviation
- Project Atlas ↔ Maps Service ↔ atlas-svc

### Methodologies

- Standard Term ↔ Variant ↔ Common Transcription Error
- standup ↔ daily sync ↔ stand-up
