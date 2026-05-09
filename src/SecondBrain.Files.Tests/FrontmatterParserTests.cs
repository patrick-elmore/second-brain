using System.Text.Json;
using FluentAssertions;
using SecondBrain.Files;

namespace SecondBrain.Files.Tests;

public sealed class FrontmatterParserTests
{
    private readonly FrontmatterParser _parser = new();

    // ── YAML frontmatter ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_YamlFrontmatter_ExtractsScalarFields()
    {
        var content = """
            ---
            granola_id: abc-123
            title: My Meeting
            type: transcript
            created: 2026-04-27T18:39:34.838Z
            ---

            Body text here.
            """;

        var result = _parser.Parse(content);

        result.SourceType.Should().Be("transcript");
        result.Metadata.Should().NotBeNull();
        result.Metadata!.Value.GetProperty("granola_id").GetString().Should().Be("abc-123");
        result.Metadata!.Value.GetProperty("title").GetString().Should().Be("My Meeting");
        result.Metadata!.Value.GetProperty("created").GetString().Should().Be("2026-04-27T18:39:34.838Z");
    }

    [Fact]
    public void Parse_YamlFrontmatter_ExtractsAttendeesAsList()
    {
        var content = """
            ---
            type: transcript
            attendees:
              - alice@example.com
              - bob@example.com
            ---
            """;

        var result = _parser.Parse(content);

        result.Metadata.Should().NotBeNull();
        var attendees = result.Metadata!.Value.GetProperty("attendees");
        attendees.ValueKind.Should().Be(JsonValueKind.Array);
        attendees.EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["alice@example.com", "bob@example.com"]);
    }

    [Fact]
    public void Parse_YamlFrontmatter_GranolaTranscriptType_ReturnsTranscript()
    {
        var content = """
            ---
            type: transcript
            title: Alice / Bob - Transcript
            ---
            """;

        var result = _parser.Parse(content);
        result.SourceType.Should().Be("transcript");
    }

    [Fact]
    public void Parse_YamlFrontmatter_NoteType_ReturnsNote()
    {
        var content = """
            ---
            type: note
            title: Release Retro
            ---
            """;

        var result = _parser.Parse(content);
        result.SourceType.Should().Be("note");
    }

    [Fact]
    public void Parse_YamlFrontmatter_NoType_InfersFromTitle_Standup()
    {
        var content = """
            ---
            title: Daily Standup Notes
            ---
            """;

        var result = _parser.Parse(content);
        result.SourceType.Should().Be("standup");
    }

    [Fact]
    public void Parse_YamlFrontmatter_NoType_InfersFromTitle_OneOnOne()
    {
        var content = """
            ---
            title: 1:1 with Manager
            ---
            """;

        var result = _parser.Parse(content);
        result.SourceType.Should().Be("1on1");
    }

    [Fact]
    public void Parse_YamlFrontmatter_NoteWithQuotedValue()
    {
        var content = """
            ---
            note: "[[2026-04-27#Alice / Bob]]"
            type: transcript
            ---
            """;

        var result = _parser.Parse(content);

        result.Metadata.Should().NotBeNull();
        result.Metadata!.Value.GetProperty("note").GetString()
            .Should().Be("[[2026-04-27#Alice / Bob]]");
    }

    [Fact]
    public void Parse_EmptyYamlBlock_ReturnsNull()
    {
        var content = """
            ---
            ---

            Just body text.
            """;

        var result = _parser.Parse(content);
        result.SourceType.Should().BeNull();
        result.Metadata.Should().BeNull();
    }

    // ── Bold-header format ────────────────────────────────────────────────────

    [Fact]
    public void Parse_BoldHeaders_ExtractsType()
    {
        var content = """
            ## Granola Notes
            ### Release Retro
            **Granola ID:** 32338b2d-988a-4910-bd19-e6d4cf28e8a8
            **Type:** note
            **Created:** 2026-04-27T20:00:35.002Z
            **Updated:** 2026-04-27T20:28:02.033Z
            **Attendees:** alice@example.com, bob@example.com, carol@example.com
            """;

        var result = _parser.Parse(content);

        result.SourceType.Should().Be("note");
        result.Metadata.Should().NotBeNull();
        result.Metadata!.Value.GetProperty("granola_id").GetString()
            .Should().Be("32338b2d-988a-4910-bd19-e6d4cf28e8a8");
        result.Metadata!.Value.GetProperty("created").GetString()
            .Should().Be("2026-04-27T20:00:35.002Z");
    }

    [Fact]
    public void Parse_BoldHeaders_AttendeesAreList()
    {
        var content = """
            **Type:** transcript
            **Attendees:** alice@example.com, bob@example.com
            """;

        var result = _parser.Parse(content);

        var attendees = result.Metadata!.Value.GetProperty("attendees");
        attendees.ValueKind.Should().Be(JsonValueKind.Array);
        attendees.EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["alice@example.com", "bob@example.com"]);
    }

    [Fact]
    public void Parse_BoldHeaders_SingleAttendee_IsString()
    {
        var content = """
            **Type:** note
            **Attendees:** alice@example.com
            """;

        var result = _parser.Parse(content);

        result.Metadata!.Value.GetProperty("attendees").GetString()
            .Should().Be("alice@example.com");
    }

    [Fact]
    public void Parse_BoldHeaders_TypeMappings()
    {
        Check("transcript", "transcript");
        Check("note", "note");
        Check("1on1", "1on1");
        Check("1:1", "1on1");
        Check("standup", "standup");
        Check("planning", "planning");

        void Check(string raw, string expected)
        {
            var content = $"**Type:** {raw}\n**Created:** 2026-01-01";
            var result = _parser.Parse(content);
            result.SourceType.Should().Be(expected, because: $"raw type '{raw}' should map to '{expected}'");
        }
    }

    // ── No frontmatter ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_PlainMarkdown_ReturnsNullSourceTypeAndNullMetadata()
    {
        var content = """
            # My Note

            This is just a plain markdown file with no frontmatter.

            - Item one
            - Item two
            """;

        var result = _parser.Parse(content);

        result.SourceType.Should().BeNull();
        result.Metadata.Should().BeNull();
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNulls()
    {
        var result = _parser.Parse(string.Empty);

        result.SourceType.Should().BeNull();
        result.Metadata.Should().BeNull();
    }

    [Fact]
    public void Parse_NoBoldHeadersNoYaml_ReturnsNulls()
    {
        var content = "Just some random text without any frontmatter markers.";

        var result = _parser.Parse(content);

        result.SourceType.Should().BeNull();
        result.Metadata.Should().BeNull();
    }

    [Fact]
    public void Parse_UnterminatedYamlBlock_ReturnsNulls()
    {
        // Starts with --- but never closes
        var content = """
            ---
            title: Never closed
            type: note
            Just continues into body without closing ---
            """;

        var result = _parser.Parse(content);

        // Without closing ---, not valid YAML frontmatter
        // But it has no bold headers either.
        // Should return null (or if body happens to contain bold headers, those)
        // In this case no bold headers so both null.
        result.SourceType.Should().BeNull();
        result.Metadata.Should().BeNull();
    }

    [Fact]
    public void Parse_YamlTakesPriorityOverBoldHeaders()
    {
        // If both present, YAML wins (content starts with ---)
        var content = """
            ---
            type: planning
            ---

            **Type:** standup
            """;

        var result = _parser.Parse(content);

        result.SourceType.Should().Be("planning");
    }

    // ── Real-world Granola sample ─────────────────────────────────────────────

    [Fact]
    public void Parse_RealGranolaYamlSample_ParsesCorrectly()
    {
        var content = """
            ---
            granola_id: d8bf6012-83ab-4228-aa63-c64eb435581d
            title: Alice / Bob - Transcript
            type: transcript
            created: 2026-04-27T18:39:34.838Z
            updated: 2026-04-27T18:39:45.014Z
            attendees:
              - aj.public@example.com
              - Conference Room A (6) [Zoom]
            note: "[[2026-04-27#Alice / Bob]]"
            ---

            # Transcript for: Alice / Bob
            """;

        var result = _parser.Parse(content);

        result.SourceType.Should().Be("transcript");
        result.Metadata.Should().NotBeNull();
        result.Metadata!.Value.GetProperty("granola_id").GetString()
            .Should().Be("d8bf6012-83ab-4228-aa63-c64eb435581d");

        var attendees = result.Metadata!.Value.GetProperty("attendees");
        attendees.ValueKind.Should().Be(JsonValueKind.Array);
        attendees.GetArrayLength().Should().Be(2);
    }
}
