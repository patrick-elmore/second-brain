using System.Security.Cryptography;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using SecondBrain.Files;
using SecondBrain.Index.Search;
using SecondBrain.Llm.Prompts;

using ApiEffort = Anthropic.Models.Messages.Effort;

namespace SecondBrain.Llm;

public sealed class ClaudeSession
{
    private readonly IMessageCreator _client;
    private readonly ToolLoop _toolLoop;
    private readonly Compactor _compactor;
    private readonly StatePersistence? _statePersistence;
    private readonly ILogger _sessionLogger;

    private readonly List<MessageParam> _messages = [];
    private readonly string _defaultModel;
    private readonly string _escalationModel;
    private readonly long _compactThresholdTokens;
    private readonly int _persistEveryNMessages;

    private long _approximateTokens;
    private DateTime? _lastCompacted;
    private DateTime? _lastActivity;
    private DateTime? _statePersistedAt;
    private int _messagesSinceLastPersist;

    public ClaudeSession(
        IMessageCreator client,
        SearchEngine searchEngine,
        FileReader fileReader,
        Compactor compactor,
        StatePersistence? statePersistence = null,
        string defaultModel = "claude-haiku-4-5",
        string escalationModel = "claude-sonnet-4-6",
        long compactThresholdTokens = 150_000,
        int persistEveryNMessages = 5,
        ILogger? logger = null,
        IStatsRecorder? stats = null)
    {
        _client = client;
        _compactor = compactor;
        _statePersistence = statePersistence;
        _defaultModel = defaultModel;
        _escalationModel = escalationModel;
        _compactThresholdTokens = compactThresholdTokens;
        _persistEveryNMessages = persistEveryNMessages;
        _sessionLogger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _toolLoop = new ToolLoop(client, searchEngine, fileReader, logger, stats);

        RestoreState();
    }

    public async Task<AskResult> AskAsync(
        string question,
        string? compactInstruction,
        string effort,
        CancellationToken ct)
    {
        // Track any compaction cost incurred inside this ask so the per-ask
        // cost reflects the full LLM spend (tool loop + auto/explicit compact).
        var compactionCost = 0m;

        if (!string.IsNullOrWhiteSpace(compactInstruction))
            compactionCost += (await CompactAsync(compactInstruction, ct)).EstimatedCostUsd;

        if (_approximateTokens >= _compactThresholdTokens && _messages.Count > 0)
            compactionCost += (await CompactAsync(null, ct)).EstimatedCostUsd;

        var requestId = GenerateRequestId();
        var (model, apiEffort) = ResolveEffort(effort);

        // Append user question
        _messages.Add(new MessageParam
        {
            Role = Role.User,
            Content = question,
        });

        // Run the tool loop (modifies _messages in place)
        var loopResult = await _toolLoop.RunAsync(_messages, model, apiEffort, ct);

        _approximateTokens += loopResult.InputTokensUsed + loopResult.OutputTokensUsed;
        _lastActivity = DateTime.UtcNow;

        // Persist after every ask completes — the cost is trivial vs. the API call
        // that just ran, and we want context to survive any restart.
        PersistState();

        return new AskResult(
            RequestId: requestId,
            Synthesis: loopResult.Synthesis,
            ModelUsed: model,
            ToolsCalled: loopResult.ToolsCalled,
            FilesReferenced: loopResult.FilesReferenced,
            EstimatedCostUsd: loopResult.EstimatedCostUsd + compactionCost);
    }

    public async Task<CompactResult> CompactAsync(string? instruction, CancellationToken ct)
    {
        var messagesBefore = _messages.Count;
        var tokensBefore = _approximateTokens;

        if (_messages.Count == 0)
            return new CompactResult(0, 0, tokensBefore, 0, 0m);

        var compaction = await _compactor.CompactAsync(_messages, instruction, ct);

        _messages.Clear();
        _messages.Add(new MessageParam
        {
            Role = Role.User,
            Content = $"[Context summary from prior conversation]\n\n{compaction.Summary}",
        });

        // After compaction, token count approximation resets to something small
        _approximateTokens = compaction.Summary.Length / 4; // rough token estimate
        _lastCompacted = DateTime.UtcNow;

        PersistState();

        return new CompactResult(
            MessagesBefore: messagesBefore,
            MessagesAfter: _messages.Count,
            ApproximateTokensBefore: tokensBefore,
            ApproximateTokensAfter: _approximateTokens,
            EstimatedCostUsd: compaction.EstimatedCostUsd);
    }

    public void Reset()
    {
        _messages.Clear();
        _approximateTokens = 0;
        _lastCompacted = null;
        _lastActivity = null;
        _messagesSinceLastPersist = 0;
        PersistState();
    }

    public SessionInfo Info() => new(
        Messages: _messages.Count,
        ApproximateTokens: _approximateTokens,
        CurrentDefaultModel: _defaultModel,
        LastCompacted: _lastCompacted,
        LastActivity: _lastActivity,
        StatePersistedAt: _statePersistedAt);

    // MCP effort -> (model, API OutputConfig.Effort)
    // All tiers run on the default model (haiku); only the API thinking effort
    // changes. The escalation model is reserved for the compactor.
    //   low    -> haiku, Low
    //   medium -> haiku, Medium
    //   high   -> haiku, High
    private (string model, ApiEffort apiEffort) ResolveEffort(string effort) => effort?.ToLowerInvariant() switch
    {
        "medium" => (_defaultModel, ApiEffort.Medium),
        "high" => (_defaultModel, ApiEffort.High),
        _ => (_defaultModel, ApiEffort.Low), // "low" or anything else
    };

    private void RestoreState()
    {
        if (_statePersistence == null) return;

        var state = _statePersistence.Restore();
        if (state == null) return;

        try
        {
            foreach (var msgJson in state.Messages)
            {
                var msg = JsonSerializer.Deserialize<MessageParam>(msgJson);
                if (msg != null)
                    _messages.Add(msg);
            }
            _approximateTokens = state.ApproximateTokens;
            if (state.LastCompacted != null && DateTime.TryParse(state.LastCompacted, out var lc))
                _lastCompacted = lc;
        }
        catch (Exception)
        {
            // If restore fails, start fresh
            _messages.Clear();
            _approximateTokens = 0;
        }
    }

    private void MaybePersistState()
    {
        if (_statePersistence == null) return;
        if (_messagesSinceLastPersist >= _persistEveryNMessages)
            PersistState();
    }

    private void PersistState()
    {
        if (_statePersistence == null) return;

        try
        {
            var state = new SessionState
            {
                DefaultModel = _defaultModel,
                LastCompacted = _lastCompacted?.ToString("o"),
                ApproximateTokens = _approximateTokens,
                Messages = _messages
                    .Select(m => JsonSerializer.SerializeToElement(m))
                    .ToList(),
            };

            _statePersistence.Persist(state);
            _statePersistedAt = DateTime.UtcNow;
            _messagesSinceLastPersist = 0;
            _sessionLogger.LogDebug("Persisted session state ({MessageCount} messages)", _messages.Count);
        }
        catch (Exception ex)
        {
            _sessionLogger.LogError(ex, "Failed to persist session state ({MessageCount} messages)", _messages.Count);
        }
    }

    private static string GenerateRequestId()
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
