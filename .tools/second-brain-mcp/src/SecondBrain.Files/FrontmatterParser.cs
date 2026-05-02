using System.Text.Json;
using System.Text.RegularExpressions;
using SecondBrain.Files.Models;

namespace SecondBrain.Files;

public sealed class FrontmatterParser
{
    // Matches ** bold headers like **Type:** value (colon is inside the bold markers)
    private static readonly Regex BoldHeaderRegex = new(
        @"^\*\*(?<key>[^*:]+):\*\*\s*(?<value>.*)$",
        RegexOptions.Compiled);

    // Detects attendees as comma-separated emails or names
    private static readonly Regex AttendeesSplitRegex = new(
        @",\s*",
        RegexOptions.Compiled);

    public FrontmatterResult Parse(string fileContent)
    {
        if (string.IsNullOrEmpty(fileContent))
            return new FrontmatterResult(null, null);

        var yamlResult = TryParseYamlFrontmatter(fileContent);
        if (yamlResult != null)
            return yamlResult;

        var boldResult = TryParseBoldHeaders(fileContent);
        if (boldResult != null)
            return boldResult;

        return new FrontmatterResult(null, null);
    }

    private static FrontmatterResult? TryParseYamlFrontmatter(string content)
    {
        // Must start with ---
        if (!content.StartsWith("---"))
            return null;

        var afterOpen = content.IndexOf('\n');
        if (afterOpen < 0)
            return null;

        // Find closing ---
        var closeIndex = content.IndexOf("\n---", afterOpen);
        if (closeIndex < 0)
            return null;

        var bodyLength = closeIndex - afterOpen - 1;
        if (bodyLength <= 0)
            return null;

        var yamlBody = content.Substring(afterOpen + 1, bodyLength);
        var fields = ParseYamlFields(yamlBody);

        if (fields.Count == 0)
            return null;

        var sourceType = DetectSourceTypeFromYaml(fields);
        var metadata = BuildMetadataElement(fields);

        return new FrontmatterResult(sourceType, metadata);
    }

    private static Dictionary<string, object> ParseYamlFields(string yamlBody)
    {
        var fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var lines = yamlBody.Split('\n');
        string? currentKey = null;
        var listAccumulator = new List<string>();
        bool inList = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (string.IsNullOrEmpty(line))
                continue;

            // List item: starts with "  - " or "- "
            if (line.TrimStart().StartsWith("- "))
            {
                var itemValue = line.TrimStart()[2..].Trim();
                if (inList && currentKey != null)
                {
                    listAccumulator.Add(itemValue);
                }
                continue;
            }

            // Flush accumulated list before moving to next key
            if (inList && currentKey != null && listAccumulator.Count > 0)
            {
                fields[currentKey] = listAccumulator.ToArray();
                listAccumulator = new List<string>();
                inList = false;
            }

            // Key: value pair
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0)
                continue;

            currentKey = line[..colonIdx].Trim();
            var rawValue = line[(colonIdx + 1)..].Trim();

            if (string.IsNullOrEmpty(rawValue))
            {
                // The value will be on the next lines (list or block)
                inList = true;
                listAccumulator = new List<string>();
            }
            else
            {
                inList = false;
                // Strip surrounding quotes
                if ((rawValue.StartsWith('"') && rawValue.EndsWith('"')) ||
                    (rawValue.StartsWith('\'') && rawValue.EndsWith('\'')))
                {
                    rawValue = rawValue[1..^1];
                }
                fields[currentKey] = rawValue;
            }
        }

        // Flush trailing list
        if (inList && currentKey != null && listAccumulator.Count > 0)
            fields[currentKey] = listAccumulator.ToArray();

        return fields;
    }

    private static string? DetectSourceTypeFromYaml(Dictionary<string, object> fields)
    {
        // Explicit type field takes priority
        if (fields.TryGetValue("type", out var typeVal) && typeVal is string typeStr)
        {
            return MapSourceType(typeStr);
        }

        // Infer from title
        if (fields.TryGetValue("title", out var titleVal) && titleVal is string title)
        {
            return InferSourceTypeFromTitle(title);
        }

        return null;
    }

    private static FrontmatterResult? TryParseBoldHeaders(string content)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            var match = BoldHeaderRegex.Match(trimmed);
            if (!match.Success)
                continue;

            var key = match.Groups["key"].Value.Trim();
            var value = match.Groups["value"].Value.Trim();
            fields[key] = value;
        }

        if (fields.Count == 0)
            return null;

        var sourceType = DetectSourceTypeFromBoldHeaders(fields);
        var metadata = BuildMetadataFromBoldHeaders(fields);

        return new FrontmatterResult(sourceType, metadata);
    }

    private static string? DetectSourceTypeFromBoldHeaders(Dictionary<string, string> fields)
    {
        if (fields.TryGetValue("Type", out var typeStr))
            return MapSourceType(typeStr);

        // No title field in bold-header format, so can't infer from title
        return null;
    }

    private static string MapSourceType(string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "transcript" => "transcript",
            "note" => "note",
            "1on1" or "1:1" or "one-on-one" or "one on one" => "1on1",
            "standup" or "stand-up" or "stand up" or "dsu" => "standup",
            "planning" or "plan" => "planning",
            var other => other
        };
    }

    private static string? InferSourceTypeFromTitle(string title)
    {
        var lower = title.ToLowerInvariant();
        if (lower.Contains("standup") || lower.Contains("stand-up") || lower.Contains("dsu"))
            return "standup";
        if (lower.Contains("1:1") || lower.Contains("1on1") || lower.Contains("one-on-one"))
            return "1on1";
        if (lower.Contains("planning"))
            return "planning";
        if (lower.Contains("transcript"))
            return "transcript";
        return null;
    }

    private static JsonElement? BuildMetadataElement(Dictionary<string, object> fields)
    {
        if (fields.Count == 0)
            return null;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in fields)
            {
                var jsonKey = key.Replace(' ', '_').Replace('-', '_').ToLowerInvariant();
                switch (value)
                {
                    case string[] arr:
                        writer.WriteStartArray(jsonKey);
                        foreach (var item in arr)
                            writer.WriteStringValue(item);
                        writer.WriteEndArray();
                        break;
                    case string str:
                        writer.WriteString(jsonKey, str);
                        break;
                }
            }
            writer.WriteEndObject();
        }

        var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    private static JsonElement? BuildMetadataFromBoldHeaders(Dictionary<string, string> fields)
    {
        if (fields.Count == 0)
            return null;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in fields)
            {
                var jsonKey = key.Replace(' ', '_').Replace('-', '_').ToLowerInvariant();

                // Attendees are comma-separated in bold-header format
                if (string.Equals(key, "Attendees", StringComparison.OrdinalIgnoreCase) && value.Contains(','))
                {
                    var parts = AttendeesSplitRegex.Split(value)
                        .Select(p => p.Trim())
                        .Where(p => p.Length > 0)
                        .ToArray();
                    writer.WriteStartArray(jsonKey);
                    foreach (var part in parts)
                        writer.WriteStringValue(part);
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteString(jsonKey, value);
                }
            }
            writer.WriteEndObject();
        }

        var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }
}
