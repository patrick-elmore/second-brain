namespace SecondBrain.Llm.Prompts;

internal static class DefaultCompactionPrompt
{
    public const string Text = """
        You are compacting your own conversation context for a performance-review-evidence
        gathering session. The conversation contains queries, search results, file reads,
        and synthesized findings.

        Produce a structured summary that preserves:
        1. Topics discussed — themes, time periods, people, projects
        2. Files identified as relevant — full paths, with one line on each file's content
        3. Findings established — claims, themes, patterns surfaced (with source attribution)
        4. Outstanding threads — questions raised but not answered, files mentioned but not read

        Drop:
        - Intermediate reasoning and conversational filler
        - Search queries that returned no useful results
        - Superseded interpretations

        Preserve specificity. Keep file paths verbatim. Keep dates and names verbatim.
        Quote sources directly when the exact wording matters.

        Return only the compacted summary. The next message will be a new user query.
        """;
}
