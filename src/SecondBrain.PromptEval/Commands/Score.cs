using System.Text.Json;
using SecondBrain.Llm;
using SecondBrain.PromptEval.Scoring;
using SecondBrain.PromptEval.TestCases;

namespace SecondBrain.PromptEval.Commands;

public static class Score
{
    public static async Task<int> RunAsync(HarnessEnvironment env, string[] args)
    {
        string testCasesPath = "test-cases-v1.json";
        string surface = "system_prompt";
        string? variantPath = null; // path to a file holding the variant value (e.g. a .md prompt file)
        string variantLabel = "baseline"; // human label for output
        string? outputPath = null;
        string effort = "low";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--test-cases" when i + 1 < args.Length: testCasesPath = args[++i]; break;
                case "--surface" when i + 1 < args.Length: surface = args[++i]; break;
                case "--variant" when i + 1 < args.Length: variantPath = args[++i]; break;
                case "--label" when i + 1 < args.Length: variantLabel = args[++i]; break;
                case "--output" when i + 1 < args.Length: outputPath = args[++i]; break;
                case "--effort" when i + 1 < args.Length: effort = args[++i].ToLowerInvariant(); break;
            }
        }

        var validEfforts = new[] { "low", "medium", "high" };
        if (!validEfforts.Contains(effort))
        {
            Console.Error.WriteLine($"Invalid --effort value: '{effort}'. Must be one of: {string.Join(", ", validEfforts)}");
            return 1;
        }

        var resolvedTestCases = Path.IsPathRooted(testCasesPath)
            ? testCasesPath
            : Path.Combine(env.StateDir, testCasesPath);

        if (!File.Exists(resolvedTestCases))
        {
            Console.Error.WriteLine($"Test cases not found: {resolvedTestCases}");
            Console.Error.WriteLine("Run `generate-test-cases` first.");
            return 1;
        }

        var json = await File.ReadAllTextAsync(resolvedTestCases);
        var set = JsonSerializer.Deserialize<TestCaseSet>(json)
            ?? throw new InvalidOperationException("Failed to parse test cases");

        Console.WriteLine($"Test set: {set.Id} ({set.Cases.Count} cases)");

        // Build the AskOverrides for the variant being scored.
        AskOverrides? overrides = null;
        string variantValue = "<production default>";
        if (variantPath != null)
        {
            variantValue = await File.ReadAllTextAsync(variantPath);
            overrides = surface switch
            {
                "system_prompt" => new AskOverrides(SystemPromptOverride: variantValue),
                "user_wrapper" => new AskOverrides(UserMessageWrapperTemplate: variantValue),
                // tool_descriptions tuning lives in PhaseRunner — needs to construct the ToolUnion list
                _ => throw new InvalidOperationException(
                    $"Surface '{surface}' not directly scorable from a file. Use `tune` or implement a tool-builder for it."),
            };
        }

        var variantId = ScoreCache.ComputeVariantId(surface, variantValue, effort);
        var cache = new ScoreCache(Path.Combine(env.StateDir, "score-cache.json"));
        var runner = new EvalRunner(env, cache);

        Console.WriteLine($"Variant: {variantLabel} ({variantId})");
        Console.WriteLine($"Effort:  {effort}");
        Console.WriteLine();

        var result = await runner.EvaluateAsync(variantId, set, overrides ?? new AskOverrides(), effort: effort);

        Console.WriteLine();
        Console.WriteLine("=== Aggregate ===");
        Console.WriteLine($"  Mean F2:           {result.Aggregate.MeanF2:F3}");
        Console.WriteLine($"  Min F2:            {result.Aggregate.MinF2:F3}");
        Console.WriteLine($"  Mean precision:    {result.Aggregate.MeanPrecision:F3}");
        Console.WriteLine($"  Mean recall:       {result.Aggregate.MeanRecall:F3}");
        Console.WriteLine($"  Acceptable rate:   {result.Aggregate.AcceptableRate:P0} (F2 >= 0.5)");
        Console.WriteLine($"  Total cost:        ${result.Aggregate.TotalCostUsd:F4}");
        Console.WriteLine();
        Console.WriteLine("By source type:");
        foreach (var (sourceType, meanF2) in result.Aggregate.MeanF2BySourceType.OrderBy(kv => kv.Key))
            Console.WriteLine($"  {sourceType,-12} F2={meanF2:F3}");

        if (outputPath != null)
        {
            var resolvedOutput = Path.IsPathRooted(outputPath)
                ? outputPath
                : Path.Combine(env.StateDir, outputPath);
            var resultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(resolvedOutput, resultJson);
            Console.WriteLine();
            Console.WriteLine($"Wrote detailed result to {resolvedOutput}");
        }

        return 0;
    }
}
