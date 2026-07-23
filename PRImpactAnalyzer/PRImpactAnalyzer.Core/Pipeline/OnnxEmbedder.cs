using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Option B from the earlier accuracy discussion: real neural sentence embeddings via ONNX
/// Runtime, pure C#, no Python. Intended to run ALONGSIDE the existing BM25 ranker
/// (Bm25Ranker.cs) as an independent, additional candidate-surfacing signal — BM25 catches
/// exact/near-exact term overlap cheaply, embeddings additionally catch paraphrases and
/// synonyms BM25's term-matching structurally cannot (e.g. "cancel order" vs "void purchase").
///
/// SETUP REQUIRED — this is opt-in and gracefully skipped if not configured:
///   1. Download a small sentence-embedding model in ONNX format, e.g.
///      https://huggingface.co/Xenova/all-MiniLM-L6-v2/tree/main/onnx (model.onnx, ~90MB)
///      and its vocab.txt from the same repo root.
///   2. Set "embeddingModelPath" and "embeddingVocabPath" in pr-impact-config.json.
/// If either file is missing or fails to load, embedding-based search is silently skipped —
/// BM25, keyword pre-filter, and work-item matching continue working exactly as before.
///
/// TOKENIZATION: hand-rolled WordPiece (the algorithm BERT-family models use), not a
/// third-party tokenizer package — this is a small, fully-specified, deterministic algorithm,
/// so implementing it directly here avoids any uncertainty about a NuGet tokenizer package's
/// exact API surface. If your model's input/output tensor names differ from the common
/// defaults this code assumes, see the comments in EmbedInternal() for exactly what to adjust.
/// </summary>
public sealed class OnnxEmbedder : IDisposable
{
    private const int MaxTokens = 128;

    private readonly InferenceSession _session;
    private readonly Dictionary<string, int> _vocab;
    private readonly string _clsToken = "[CLS]";
    private readonly string _sepToken = "[SEP]";
    private readonly string _unkToken = "[UNK]";
    private readonly string _padToken = "[PAD]";

    private OnnxEmbedder(InferenceSession session, Dictionary<string, int> vocab)
    {
        _session = session;
        _vocab = vocab;
    }

    /// <summary>
    /// Attempts to load the model + vocab. Returns null (never throws) if paths are missing,
    /// files don't exist, or loading fails for any reason — callers should treat a null
    /// result as "embedding search unavailable this run" and continue without it.
    /// </summary>
    public static OnnxEmbedder? TryCreate(string? modelPath, string? vocabPath, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(vocabPath))
            return null;

        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            logger?.LogInformation(
                "Embedding model/vocab not found (modelPath={ModelPath}, vocabPath={VocabPath}) — skipping embedding-based search.",
                modelPath, vocabPath);
            return null;
        }

        try
        {
            var session = new InferenceSession(modelPath);
            var vocab = LoadVocab(vocabPath);
            return new OnnxEmbedder(session, vocab);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to initialize ONNX embedder — skipping embedding-based search.");
            return null;
        }
    }

    private static Dictionary<string, int> LoadVocab(string vocabPath)
    {
        var vocab = new Dictionary<string, int>();
        var lines = File.ReadAllLines(vocabPath);
        for (int i = 0; i < lines.Length; i++)
            vocab[lines[i].Trim()] = i; // vocab.txt: one token per line, line number = token id

        return vocab;
    }

    /// <summary>Embeds a piece of text into a normalized (unit-length) vector, via mean-pooling
    /// over token embeddings weighted by the attention mask — the standard approach for
    /// sentence-transformers-family models exported without a built-in pooling layer.</summary>
    public float[] Embed(string text)
    {
        var (inputIds, attentionMask, tokenTypeIds) = Tokenize(text);
        return EmbedInternal(inputIds, attentionMask, tokenTypeIds);
    }

    private float[] EmbedInternal(long[] inputIds, long[] attentionMask, long[] tokenTypeIds)
    {
        var seqLen = inputIds.Length;

        var inputIdsTensor      = new DenseTensor<long>(inputIds, new[] { 1, seqLen });
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, new[] { 1, seqLen });
        var tokenTypeIdsTensor  = new DenseTensor<long>(tokenTypeIds, new[] { 1, seqLen });

        // NOTE: these three input names ("input_ids", "attention_mask", "token_type_ids") are
        // the standard names used by HuggingFace's ONNX exports of BERT-family models,
        // including the Xenova/all-MiniLM-L6-v2 export this class is documented against. If
        // you use a different ONNX export with different input names, update the strings
        // below to match — you can discover the actual names by inspecting
        // `session.InputMetadata.Keys` at runtime if this throws a "no such input" error.
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor),
        };

        using var results = _session.Run(inputs);

        // Standard output name for the token-level embeddings before pooling. If your export
        // already includes pooling (an output literally called "sentence_embedding"), swap
        // the logic below to just read that tensor directly instead of mean-pooling here.
        var lastHiddenState = results.First(r => r.Name == "last_hidden_state").AsTensor<float>();

        var hiddenSize = lastHiddenState.Dimensions[2];
        var pooled = new float[hiddenSize];
        float maskSum = 0;

        for (int t = 0; t < seqLen; t++)
        {
            if (attentionMask[t] == 0) continue;
            maskSum += 1;
            for (int h = 0; h < hiddenSize; h++)
                pooled[h] += lastHiddenState[0, t, h];
        }

        if (maskSum > 0)
            for (int h = 0; h < hiddenSize; h++)
                pooled[h] /= maskSum;

        // L2-normalize so cosine similarity reduces to a plain dot product.
        double norm = Math.Sqrt(pooled.Sum(v => (double)v * v));
        if (norm > 0)
            for (int h = 0; h < hiddenSize; h++)
                pooled[h] = (float)(pooled[h] / norm);

        return pooled;
    }

    public static double CosineSimilarity(float[] a, float[] b)
    {
        // Both vectors are already unit-normalized by Embed(), so this is just a dot product.
        double dot = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            dot += a[i] * b[i];
        return dot;
    }

    // ── Hand-rolled WordPiece tokenization ───────────────────────────────────────
    // BERT-family tokenization: lowercase, split on whitespace/punctuation, then greedily
    // match the longest known subword per word (continuation pieces prefixed with "##").
    // This is a small, fully-specified algorithm — implementing it directly here avoids any
    // dependency on a third-party tokenizer package's exact API surface.

    private (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Tokenize(string text)
    {
        var tokens = new List<string> { _clsToken };
        foreach (var word in BasicSplit(text))
            tokens.AddRange(WordPieceSplit(word));
        tokens.Add(_sepToken);

        if (tokens.Count > MaxTokens)
            tokens = tokens.Take(MaxTokens - 1).Append(_sepToken).ToList();

        var ids = tokens.Select(t => (long)(_vocab.TryGetValue(t, out var id) ? id : _vocab.GetValueOrDefault(_unkToken, 0))).ToList();
        var attentionMask = ids.Select(_ => 1L).ToList();

        // Pad to a fixed length so all tensors in a run share shape (simplifies batching if
        // ever extended; not strictly required for single-item embedding as done here).
        var padId = (long)_vocab.GetValueOrDefault(_padToken, 0);
        while (ids.Count < MaxTokens)
        {
            ids.Add(padId);
            attentionMask.Add(0);
        }

        var tokenTypeIds = Enumerable.Repeat(0L, ids.Count).ToArray();
        return (ids.ToArray(), attentionMask.ToArray(), tokenTypeIds);
    }

    private static IEnumerable<string> BasicSplit(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else if (char.IsPunctuation(c) || char.IsSymbol(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                yield return c.ToString(); // punctuation becomes its own token, per BERT convention
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private IEnumerable<string> WordPieceSplit(string word)
    {
        if (_vocab.ContainsKey(word)) { yield return word; yield break; }

        var subTokens = new List<string>();
        int start = 0;
        bool failed = false;

        while (start < word.Length)
        {
            int end = word.Length;
            string? matched = null;

            // Greedy longest-match-first: try the longest possible substring first, shrinking
            // until a known vocab entry is found (continuation pieces after the first use "##").
            while (end > start)
            {
                var candidate = (start == 0 ? "" : "##") + word[start..end];
                if (_vocab.ContainsKey(candidate)) { matched = candidate; break; }
                end--;
            }

            if (matched is null) { failed = true; break; }
            subTokens.Add(matched);
            start = end;
        }

        if (failed) { yield return _unkToken; yield break; }
        foreach (var t in subTokens) yield return t;
    }

    /// <summary>
    /// Embeds every scenario (using a simple content-hash cache so unchanged scenarios don't
    /// get re-embedded every run) and ranks them against the query text by cosine similarity.
    /// Same shape/contract as Bm25Ranker.FindTopMatches, so both can be used interchangeably
    /// or in combination by the pipeline.
    /// </summary>
    public List<(ScenarioRecord Scenario, double Score)> FindTopMatches(
        List<ScenarioRecord> scenarios, string queryText, int topK, EmbeddingCache cache)
    {
        if (scenarios.Count == 0 || string.IsNullOrWhiteSpace(queryText))
            return new List<(ScenarioRecord, double)>();

        var queryEmbedding = Embed(queryText);

        var scored = new List<(ScenarioRecord Scenario, double Score)>();
        foreach (var s in scenarios)
        {
            var docText = string.Join(' ', new[] { s.ScenarioName, s.FeatureTitle }
                .Concat(s.Tags).Concat(s.Steps)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            var embedding = cache.GetOrCompute(docText, () => Embed(docText));
            scored.Add((s, CosineSimilarity(queryEmbedding, embedding)));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();
    }

    public void Dispose() => _session.Dispose();
}
