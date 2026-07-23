using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Simple on-disk cache mapping a text's content hash to its precomputed embedding vector.
/// Without this, re-embedding every scenario on every single PR run would be wasteful —
/// ONNX inference for a few hundred short texts is fast but not free, and scenario text
/// rarely changes between runs. Only text whose hash isn't already cached gets embedded.
/// </summary>
public class EmbeddingCache
{
    private readonly string _cachePath;
    private readonly Dictionary<string, float[]> _entries;
    private bool _dirty;

    private EmbeddingCache(string cachePath, Dictionary<string, float[]> entries)
    {
        _cachePath = cachePath;
        _entries = entries;
    }

    public static EmbeddingCache Load(string reportsBaseDir)
    {
        var path = Path.Combine(reportsBaseDir, "embedding-cache.json");
        if (!File.Exists(path))
            return new EmbeddingCache(path, new Dictionary<string, float[]>());

        try
        {
            var raw = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<Dictionary<string, float[]>>(raw) ?? new();
            return new EmbeddingCache(path, entries);
        }
        catch
        {
            // A corrupted cache file just means everything gets re-embedded once — safe to ignore.
            return new EmbeddingCache(path, new Dictionary<string, float[]>());
        }
    }

    public float[] GetOrCompute(string text, Func<float[]> compute)
    {
        var key = Hash(text);
        if (_entries.TryGetValue(key, out var cached))
            return cached;

        var embedding = compute();
        _entries[key] = embedding;
        _dirty = true;
        return embedding;
    }

    public void SaveIfDirty()
    {
        if (!_dirty) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath) ?? ".");
        File.WriteAllText(_cachePath, JsonSerializer.Serialize(_entries));
    }

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }
}
