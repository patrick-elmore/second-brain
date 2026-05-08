using System.Text.Json;
using Microsoft.Extensions.Logging;
using SecondBrain.Llm;
using SecondBrain.Llm.Prompts;
using SecondBrain.PromptEval.Scoring;
using SecondBrain.PromptEval.TestCases;

namespace SecondBrain.PromptEval.Tuning;

public sealed class PhaseRunner
{
    private readonly HarnessEnvironment _env;
    private readonly ILogger _logger;

    public const int DefaultIterationCap = 6;
    public const int DefaultPlateauThreshold = 3;
    public const double DefaultEpsilon = 0.02;

    public PhaseRunner(HarnessEnvironment env)
    {
        _env = env;
        _logger = env.LoggerFactory.CreateLogger<PhaseRunner>();
    }

    public sealed record PhaseConfig(
        string Surface,
        TestCaseSet TestCases,
        int MaxLlmCalls,
        int IterationCap = DefaultIterationCap,
        int PlateauThreshold = DefaultPlateauThreshold,
        double Epsilon = DefaultEpsilon,
        bool DryRun = false);

    public sealed record PhaseResult(
        string PhaseId,
        string Surface,
        IReadOnlyList<PromptVariant> Iterations,
        int BestIterationId,
        string StoppedReason);

    public async Task<PhaseResult> RunAsync(PhaseConfig config, CancellationToken ct)
    {
        var phaseId = $"{DateTime.UtcNow:yyyy-MM-dd_HHmmss}_{config.Surface}";
        var runFile = Path.Combine(env_runsDir(), $"{phaseId}.json");

        var cache = new ScoreCache(Path.Combine(_env.StateDir, "score-cache.json"));
        var runner = new EvalRunner(_env, cache);
        var proposer = new VariantProposer(_env);

        var (productionDefault, makeOverrides) = ResolveSurface(config.Surface);
        var iterations = new List<PromptVariant>();

        // Iteration 0: baseline (production default).
        _logger.LogInformation("Phase {Surface}: starting baseline scoring", config.Surface);
        var baselineId = ScoreCache.ComputeVariantId(config.Surface, productionDefault);
        var baselineEval = await runner.EvaluateAsync(
            baselineId, config.TestCases, makeOverrides(productionDefault), ct: ct);

        var baseline = new PromptVariant
        {
            Id = 0,
            VariantId = baselineId,
            Surface = config.Surface,
            Value = productionDefault,
            IsBaseline = true,
            Rationale = "production default",
            Score = baselineEval.Aggregate,
            Cases = baselineEval.Cases,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        iterations.Add(baseline);

        SaveRun(runFile, phaseId, config, iterations, bestId: 0, stoppedReason: "in_progress");
        _logger.LogInformation("Baseline F2={F2:F3}", baseline.Score!.MeanF2);

        // Approximate LLM call usage: each EvaluateAsync = N test cases (sub minus cache hits)
        // plus the proposer calls. We don't track exactly; we use iteration count vs cap as the stop.
        var bestId = 0;
        var bestScore = baseline.Score.MeanF2;
        var currentValue = productionDefault;
        var noImprovementStreak = 0;
        var stoppedReason = "iteration_cap";

        for (var iter = 1; iter <= config.IterationCap; iter++)
        {
            ct.ThrowIfCancellationRequested();

            var failures = iterations[^1].Cases!.Where(c => c.Score.F2 < 0.5).ToList();
            // If everything passed, propose using all cases as context (proposer can still polish).
            if (failures.Count == 0)
                failures = iterations[^1].Cases!.OrderBy(c => c.Score.F2).Take(3).ToList();

            VariantProposer.Proposal proposal;
            if (config.DryRun)
            {
                _logger.LogInformation("Iter {N}: dry-run, no proposer call", iter);
                proposal = new VariantProposer.Proposal(currentValue, "dry-run identity");
            }
            else
            {
                _logger.LogInformation("Iter {N}: requesting proposal from {Model}", iter, _env.EscalationModel);
                proposal = await proposer.ProposeAsync(
                    config.Surface, currentValue, productionDefault, iterations, failures, ct);
            }

            var variantId = ScoreCache.ComputeVariantId(config.Surface, proposal.Value);
            _logger.LogInformation("Iter {N}: scoring variant {Vid}", iter, variantId);
            var eval = await runner.EvaluateAsync(
                variantId, config.TestCases, makeOverrides(proposal.Value), ct: ct);

            var variant = new PromptVariant
            {
                Id = iter,
                VariantId = variantId,
                Surface = config.Surface,
                Value = proposal.Value,
                ParentId = iterations[^1].Id,
                Rationale = proposal.Rationale,
                Score = eval.Aggregate,
                Cases = eval.Cases,
                CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            };
            iterations.Add(variant);

            _logger.LogInformation(
                "Iter {N}: F2={F2:F3} (best so far {Best:F3} delta {Delta:+0.000;-0.000})",
                iter, variant.Score!.MeanF2, bestScore, variant.Score.MeanF2 - bestScore);

            if (variant.Score.MeanF2 > bestScore + config.Epsilon)
            {
                bestScore = variant.Score.MeanF2;
                bestId = iter;
                noImprovementStreak = 0;
            }
            else
            {
                noImprovementStreak++;
            }

            // Walk forward — next proposal is conditioned on this one (even if worse).
            currentValue = proposal.Value;

            SaveRun(runFile, phaseId, config, iterations, bestId, stoppedReason: "in_progress");

            if (noImprovementStreak >= config.PlateauThreshold)
            {
                stoppedReason = "plateau";
                break;
            }
        }

        // Pin the best.
        var best = iterations[bestId];
        UpdatePinnedBest(config.Surface, best, config.TestCases.Id);

        SaveRun(runFile, phaseId, config, iterations, bestId, stoppedReason);

        _logger.LogInformation("Phase {Surface} complete: stopped={Reason} best=iter {BestId} F2={F2:F3}",
            config.Surface, stoppedReason, bestId, bestScore);

        return new PhaseResult(phaseId, config.Surface, iterations, bestId, stoppedReason);
    }

    // ── Surface resolution ───────────────────────────────────────────────────────

    private (string productionDefault, Func<string, AskOverrides> makeOverrides)
        ResolveSurface(string surface) => surface switch
        {
            "system_prompt" => (
                SystemPrompt.Text,
                v => new AskOverrides(SystemPromptOverride: v)),
            "user_wrapper" => (
                "{query}", // identity = production default
                v => new AskOverrides(UserMessageWrapperTemplate: v)),
            // tool_descriptions and query_gen_prompt are intentionally not implemented in v1.
            _ => throw new InvalidOperationException(
                $"Surface '{surface}' tuning not yet supported. Implement in PhaseRunner.ResolveSurface."),
        };

    // ── Persistence ──────────────────────────────────────────────────────────────

    private string env_runsDir()
    {
        var dir = Path.Combine(_env.StateDir, "runs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SaveRun(
        string filePath,
        string phaseId,
        PhaseConfig config,
        IReadOnlyList<PromptVariant> iterations,
        int bestId,
        string stoppedReason)
    {
        var doc = new
        {
            phase_id = phaseId,
            surface = config.Surface,
            test_set_id = config.TestCases.Id,
            iteration_cap = config.IterationCap,
            iterations,
            best_iteration_id = bestId,
            stopped_reason = stoppedReason,
            updated_at = DateTimeOffset.UtcNow.ToString("o"),
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void UpdatePinnedBest(string surface, PromptVariant best, string testSetId)
    {
        var file = Path.Combine(_env.StateDir, "pinned-best.json");
        Dictionary<string, object> map;
        if (File.Exists(file))
        {
            var json = File.ReadAllText(file);
            map = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
        }
        else
        {
            map = new Dictionary<string, object>();
        }

        map[surface] = new
        {
            value = best.Value,
            score = best.Score?.MeanF2,
            test_set_id = testSetId,
            tuned_at = DateTimeOffset.UtcNow.ToString("o"),
            iteration_id = best.Id,
            rationale = best.Rationale,
        };

        File.WriteAllText(file, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }
}
