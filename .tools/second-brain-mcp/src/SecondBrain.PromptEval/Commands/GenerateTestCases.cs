using System.Text.Json;
using SecondBrain.PromptEval.TestCases;

namespace SecondBrain.PromptEval.Commands;

public static class GenerateTestCases
{
    public static async Task<int> RunAsync(HarnessEnvironment env, string[] args)
    {
        string outputName = "test-cases-v1.json";
        string setId = "tc-v1";
        int seed = 42;
        bool overwrite = false;
        var counts = new Dictionary<string, int>(TestCaseGenerator.DefaultConfig().CountPerSourceType);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output" when i + 1 < args.Length: outputName = args[++i]; break;
                case "--set-id" when i + 1 < args.Length: setId = args[++i]; break;
                case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i]); break;
                case "--overwrite": overwrite = true; break;
                case "--count" when i + 1 < args.Length:
                    // Apply the same count to all source types
                    var n = int.Parse(args[++i]);
                    foreach (var k in counts.Keys.ToList()) counts[k] = n;
                    break;
            }
        }

        var outputPath = Path.IsPathRooted(outputName)
            ? outputName
            : Path.Combine(env.StateDir, outputName);

        if (File.Exists(outputPath) && !overwrite)
        {
            Console.Error.WriteLine($"{outputPath} exists. Use --overwrite to replace it.");
            return 1;
        }

        var generator = new TestCaseGenerator(env, seed);
        var config = new TestCaseGenerator.GenerationConfig(counts, setId);

        var ct = CancellationToken.None;
        var set = await generator.GenerateAsync(config, ct);

        var json = JsonSerializer.Serialize(set, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        await File.WriteAllTextAsync(outputPath, json, ct);

        Console.WriteLine();
        Console.WriteLine($"Wrote {set.Cases.Count} test cases to {outputPath}");
        Console.WriteLine($"Set id: {set.Id}");
        Console.WriteLine($"Index fingerprint: {set.IndexFingerprint}");
        Console.WriteLine();
        Console.WriteLine("Review the queries by hand before running `tune`. Edit, delete, or regenerate cases as needed.");
        return 0;
    }
}
