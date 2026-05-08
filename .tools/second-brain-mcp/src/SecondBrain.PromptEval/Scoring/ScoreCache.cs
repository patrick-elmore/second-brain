using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecondBrain.PromptEval.Scoring;

/// <summary>
/// Disk-backed cache keyed by (variantId, testCaseId). Variants are addressed
/// by stable id (typically a SHA of the value being tested), so re-scoring the
/// same value is free.
/// </summary>
public sealed class ScoreCache
{
    private readonly string _filePath;
    private Dictionary<string, CaseResult> _cache;

    public ScoreCache(string filePath)
    {
        _filePath = filePath;
        _cache = Load();
    }

    public CaseResult? TryGet(string variantId, string testCaseId)
    {
        var key = MakeKey(variantId, testCaseId);
        return _cache.GetValueOrDefault(key);
    }

    public void Put(string variantId, string testCaseId, CaseResult result)
    {
        var key = MakeKey(variantId, testCaseId);
        _cache[key] = result;
        Save();
    }

    public int Count => _cache.Count;

    public static string ComputeVariantId(string surface, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(surface + "|" + value));
        return surface + "_" + Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static string MakeKey(string variantId, string testCaseId) => $"{variantId}::{testCaseId}";

    private Dictionary<string, CaseResult> Load()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, CaseResult>();

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, CaseResult>>(json) ?? new();
        }
        catch
        {
            return new Dictionary<string, CaseResult>();
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}
