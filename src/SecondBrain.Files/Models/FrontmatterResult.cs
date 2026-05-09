using System.Text.Json;

namespace SecondBrain.Files.Models;

public sealed record FrontmatterResult(string? SourceType, JsonElement? Metadata);
