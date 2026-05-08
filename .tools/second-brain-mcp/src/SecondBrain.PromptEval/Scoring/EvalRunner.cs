using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SecondBrain.Llm;
using SecondBrain.PromptEval.TestCases;

namespace SecondBrain.PromptEval.Scoring;

/// <summary>
/// Runs a variant (an AskOverrides config) against a test set, scoring each case
/// on file-overlap. The session is fresh per case so prior test cases don't leak
/// into context.
/// </summary>
public sealed class EvalRunner
{
    private readonly HarnessEnvironment _env;
    private readonly ILogger _logger;
    private readonly ScoreCache _cache;

    public EvalRunner(HarnessEnvironment env, ScoreCache cache)
    {
        _env = env;
        _logger = env.LoggerFactory.CreateLogger<EvalRunner>();
        _cache = cache;
    }

    public async Task<VariantEvalResult> EvaluateAsync(
        string variantId,
        TestCaseSet set,
        AskOverrides overrides,
        IReadOnlyList<TestCase>? subset = null,
        string effort = "low",
        CancellationToken ct = default)
    {
        var cases = subset ?? set.Cases;
        var results = new List<CaseResult>(cases.Count);

        foreach (var tc in cases)
        {
            ct.ThrowIfCancellationRequested();

            // Cache hit: skip the API call entirely.
            var cached = _cache.TryGet(variantId, tc.Id);
            if (cached != null)
            {
                _logger.LogInformation("[cache] {Id} F2={F2:F2}", tc.Id, cached.Score.F2);
                results.Add(cached);
                continue;
            }

            CaseResult result;
            try
            {
                result = await RunCaseAsync(tc, overrides, effort, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // user-initiated cancel
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Id}] FAILED ({Type}: {Msg}) — recording as F2=0", tc.Id, ex.GetType().Name, ex.Message);
                result = new CaseResult
                {
                    TestCaseId = tc.Id,
                    Query = tc.Query,
                    ExpectedPaths = tc.TargetPaths,
                    ActualPaths = [],
                    Synthesis = $"[ERROR: {ex.GetType().Name}: {ex.Message}]",
                    Score = new CaseScore(0, 0, 0, 0, tc.TargetPaths.Count, 0),
                    ToolsCalled = 0,
                    CostUsd = 0m,
                    DurationMs = "0",
                };
            }
            _cache.Put(variantId, tc.Id, result);
            results.Add(result);

            _logger.LogInformation(
                "[{Id}] F2={F2:F2} P={P:F2} R={R:F2}  expected={E} actual={A} hit={H}",
                tc.Id, result.Score.F2, result.Score.Precision, result.Score.Recall,
                result.Score.Expected, result.Score.Actual, result.Score.Hit);
        }

        return new VariantEvalResult
        {
            VariantId = variantId,
            TestSetId = set.Id,
            Cases = results,
            Aggregate = AggregateScore.FromCases(results, cases),
            EvaluatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
    }

    private async Task<CaseResult> RunCaseAsync(TestCase tc, AskOverrides overrides, string effort, CancellationToken ct)
    {
        // Fresh session per case — no context bleed from prior cases.
        // No state persistence — these sessions are throwaway.
        var compactor = new Compactor(_env.Client, _env.EscalationModel);
        var session = new ClaudeSession(
            client: _env.Client,
            searchEngine: _env.SearchEngine,
            fileReader: _env.FileReader,
            compactor: compactor,
            statePersistence: null,
            defaultModel: _env.DefaultModel,
            escalationModel: _env.EscalationModel,
            // Very high threshold so compaction never fires mid-eval (would skew costs).
            compactThresholdTokens: long.MaxValue,
            persistEveryNMessages: int.MaxValue,
            logger: _env.LoggerFactory.CreateLogger<ClaudeSession>());

        var sw = Stopwatch.StartNew();
        var ask = await session.AskAsync(tc.Query, null, effort, ct, overrides);
        sw.Stop();

        var score = Scorer.Score(tc.TargetPaths, ask.FilesReferenced);

        return new CaseResult
        {
            TestCaseId = tc.Id,
            Query = tc.Query,
            ExpectedPaths = tc.TargetPaths,
            ActualPaths = ask.FilesReferenced,
            Synthesis = ask.Synthesis,
            Score = score,
            ToolsCalled = ask.ToolsCalled,
            CostUsd = ask.EstimatedCostUsd,
            DurationMs = sw.ElapsedMilliseconds.ToString(),
        };
    }
}
