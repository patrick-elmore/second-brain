using System.Text.Json;
using SecondBrain.PromptEval.TestCases;
using SecondBrain.PromptEval.Tuning;

namespace SecondBrain.PromptEval.Commands;

public static class Tune
{
    public static async Task<int> RunAsync(HarnessEnvironment env, string[] args)
    {
        string surface = "system_prompt";
        string testCasesPath = "test-cases-v1.json";
        int maxLlmCalls = 125;
        int iterationCap = PhaseRunner.DefaultIterationCap;
        bool dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--surface" when i + 1 < args.Length: surface = args[++i]; break;
                case "--test-cases" when i + 1 < args.Length: testCasesPath = args[++i]; break;
                case "--max-llm-calls" when i + 1 < args.Length: maxLlmCalls = int.Parse(args[++i]); break;
                case "--iteration-cap" when i + 1 < args.Length: iterationCap = int.Parse(args[++i]); break;
                case "--dry-run": dryRun = true; break;
            }
        }

        // Fail fast on unsupported surface — keep this list in sync with
        // PhaseRunner.ResolveSurface. Without this, an invalid surface would
        // sail through baseline scoring (which uses the production prompt
        // regardless of surface) and only crash on iter 1's proposer call.
        var supportedSurfaces = new[] { "system_prompt", "user_wrapper" };
        if (!supportedSurfaces.Contains(surface))
        {
            Console.Error.WriteLine($"Unsupported surface: '{surface}'.");
            Console.Error.WriteLine($"Supported: {string.Join(", ", supportedSurfaces)}");
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
        Console.WriteLine($"Surface:  {surface}");
        Console.WriteLine($"Budget:   ~{maxLlmCalls} LLM calls (informational; loop bounded by iteration cap and plateau)");
        Console.WriteLine($"Iter cap: {iterationCap}");
        if (dryRun) Console.WriteLine("DRY RUN: proposer returns identity; only baseline + N identity scores will be made.");
        Console.WriteLine();

        var runner = new PhaseRunner(env);
        var config = new PhaseRunner.PhaseConfig(
            Surface: surface,
            TestCases: set,
            MaxLlmCalls: maxLlmCalls,
            IterationCap: iterationCap,
            DryRun: dryRun);

        var result = await runner.RunAsync(config, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"=== Phase complete ===");
        Console.WriteLine($"Phase id:        {result.PhaseId}");
        Console.WriteLine($"Stopped:         {result.StoppedReason}");
        Console.WriteLine($"Best iteration:  {result.BestIterationId} (F2={result.Iterations[result.BestIterationId].Score?.MeanF2:F3})");
        Console.WriteLine();
        Console.WriteLine("Iterations:");
        foreach (var v in result.Iterations)
        {
            var marker = v.Id == result.BestIterationId ? "★" : " ";
            Console.WriteLine($" {marker} #{v.Id,2}  F2={v.Score?.MeanF2:F3}  P={v.Score?.MeanPrecision:F3}  R={v.Score?.MeanRecall:F3}  cost=${v.Score?.TotalCostUsd:F4}  {(v.IsBaseline ? "(baseline)" : v.Rationale?.Split('\n')[0])}");
        }

        return 0;
    }
}
