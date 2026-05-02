using System.Text.Json;
using Anthropic.Models.Messages;

namespace SecondBrain.Llm;

public sealed class StatePersistence
{
    private readonly string _statePath;
    private readonly int _backupCount;

    public StatePersistence(string statePath, int backupCount = 5)
    {
        _statePath = statePath;
        _backupCount = backupCount;
    }

    public void Persist(SessionState state)
    {
        var dir = Path.GetDirectoryName(_statePath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(state, StateSerializerOptions);

        // Rotate backups before overwriting
        RotateBackups();

        File.WriteAllText(_statePath, json);
    }

    public SessionState? Restore()
    {
        if (!File.Exists(_statePath))
            return null;

        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<SessionState>(json, StateSerializerOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void RotateBackups()
    {
        if (!File.Exists(_statePath))
            return;

        for (var i = _backupCount; i > 1; i--)
        {
            var older = $"{_statePath}.bak.{i}";
            var newer = $"{_statePath}.bak.{i - 1}";
            if (File.Exists(newer))
            {
                if (File.Exists(older))
                    File.Delete(older);
                File.Move(newer, older);
            }
        }

        File.Copy(_statePath, $"{_statePath}.bak.1", overwrite: true);
    }

    private static readonly JsonSerializerOptions StateSerializerOptions = new()
    {
        WriteIndented = false,
    };
}

public sealed class SessionState
{
    public int SchemaVersion { get; set; } = 1;
    public string SavedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string DefaultModel { get; set; } = string.Empty;
    public string? LastCompacted { get; set; }
    public long ApproximateTokens { get; set; }

    // Messages serialized as raw JSON elements to preserve exact wire format
    public List<JsonElement> Messages { get; set; } = [];
}
