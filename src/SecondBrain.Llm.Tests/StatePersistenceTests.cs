using System.Text.Json;
using SecondBrain.Llm;

namespace SecondBrain.Llm.Tests;

public sealed class StatePersistenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _statePath;

    public StatePersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _statePath = Path.Combine(_tempDir, "session-state.json");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Restore_NoFile_ReturnsNull()
    {
        var persistence = new StatePersistence(_statePath);
        persistence.Restore().Should().BeNull();
    }

    [Fact]
    public void PersistAndRestore_RoundTrips()
    {
        var state = new SessionState
        {
            DefaultModel = "claude-haiku-4-5",
            ApproximateTokens = 12345,
            LastCompacted = "2026-05-01T10:00:00Z",
            Messages = [JsonSerializer.SerializeToElement(new { role = "user", content = "hello" })],
        };

        var persistence = new StatePersistence(_statePath);
        persistence.Persist(state);

        var restored = persistence.Restore();

        restored.Should().NotBeNull();
        restored!.DefaultModel.Should().Be("claude-haiku-4-5");
        restored.ApproximateTokens.Should().Be(12345);
        restored.LastCompacted.Should().Be("2026-05-01T10:00:00Z");
        restored.Messages.Should().HaveCount(1);
    }

    [Fact]
    public void Persist_CreatesDirectoryIfMissing()
    {
        var nested = Path.Combine(_tempDir, "sub", "dir", "state.json");
        var persistence = new StatePersistence(nested);

        persistence.Persist(new SessionState { DefaultModel = "test" });

        File.Exists(nested).Should().BeTrue();
    }

    [Fact]
    public void Persist_MultipleTimes_RotatesBackups()
    {
        var persistence = new StatePersistence(_statePath, backupCount: 3);

        persistence.Persist(new SessionState { DefaultModel = "v1" });
        persistence.Persist(new SessionState { DefaultModel = "v2" });
        persistence.Persist(new SessionState { DefaultModel = "v3" });
        persistence.Persist(new SessionState { DefaultModel = "v4" });

        // After 4 persists, should have .bak.1, .bak.2, .bak.3
        File.Exists(_statePath).Should().BeTrue();
        File.Exists($"{_statePath}.bak.1").Should().BeTrue();
        File.Exists($"{_statePath}.bak.2").Should().BeTrue();
        File.Exists($"{_statePath}.bak.3").Should().BeTrue();
        // .bak.4 should NOT exist (only 3 backups)
        File.Exists($"{_statePath}.bak.4").Should().BeFalse();
    }

    [Fact]
    public void Restore_CorruptFile_ReturnsNull()
    {
        File.WriteAllText(_statePath, "{ this is not valid json [[[");
        var persistence = new StatePersistence(_statePath);

        var result = persistence.Restore();
        result.Should().BeNull();
    }

    [Fact]
    public void Restore_EmptyMessages_ReturnsEmptyList()
    {
        var state = new SessionState { Messages = [] };
        var persistence = new StatePersistence(_statePath);
        persistence.Persist(state);

        var restored = persistence.Restore();

        restored!.Messages.Should().BeEmpty();
    }
}
