using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// BM25 ranking — a term-frequency/inverse-document-frequency scorer that handles rare,
/// distinctive words much better than plain "does the keyword appear" matching, without
/// needing an embedding model, Python, or a vector store. This is Option C from the earlier
/// discussion: cheaper than neural embeddings, meaningfully better than keyword-contains,
/// pure C#, zero new dependencies.
///
/// Best-aligned use case (per that discussion): natural-language PR/work-item text against
/// natural-language scenario text (name, feature title, steps, tags) — general term-weighting
/// schemes like this work well for two documents in the same "register" (plain English), and
/// are noisier when one side is source code. This ranker is deliberately scoped to the
/// natural-language side of the problem, NOT code-diff-to-scenario matching.
///
/// This is a SOFT signal: it surfaces candidates for the LLM to verify, exactly like the
/// keyword pre-filter does — it does not itself decide a scenario is impacted. Unlike the
/// deterministic work-item TAG match (a hard fact, force-included at HIGH), a semantic-search
/// hit is a hypothesis the LLM can accept or reject.
/// </summary>
public static class Bm25Ranker
{
    private const double K1 = 1.5;  // term-frequency saturation — higher = diminishing returns kick in later
    private const double B  = 0.75; // length normalization — 0 = none, 1 = full

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","but","if","then","when","this","that","these","those",
        "is","are","was","were","be","been","being","to","of","in","on","at","for","with",
        "from","by","as","it","its","not","no","do","does","did","will","would","should",
        "can","could","may","might","must","shall","have","has","had","we","you","they",
        "i","he","she","him","her","them","us","our","your","their","also","into","onto",
    };

    private class Doc
    {
        public ScenarioRecord Scenario = null!;
        public Dictionary<string, int> TermCounts = new();
        public int Length;
    }

    /// <summary>
    /// Scores every scenario against the query text and returns the top K by BM25 score
    /// (score > 0 only — a zero score means no query term appeared in the scenario at all).
    /// </summary>
    public static List<(ScenarioRecord Scenario, double Score)> FindTopMatches(
        List<ScenarioRecord> scenarios, string queryText, int topK = 30)
    {
        if (scenarios.Count == 0 || string.IsNullOrWhiteSpace(queryText))
            return new List<(ScenarioRecord, double)>();

        var docs = scenarios.Select(BuildDoc).ToList();
        var avgDocLength = docs.Count == 0 ? 0 : docs.Average(d => d.Length);
        var docFrequency = BuildDocumentFrequency(docs);
        var totalDocs = docs.Count;

        var queryTerms = Tokenize(queryText).Distinct().ToList();
        if (queryTerms.Count == 0) return new List<(ScenarioRecord, double)>();

        return docs
            .Select(doc => (Scenario: doc.Scenario, Score: ScoreDocument(doc, queryTerms, docFrequency, totalDocs, avgDocLength)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();
    }

    private static Doc BuildDoc(ScenarioRecord s)
    {
        var text = string.Join(' ', new[] { s.ScenarioName, s.FeatureTitle }
            .Concat(s.Tags)
            .Concat(s.Steps)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        var terms = Tokenize(text).ToList();
        var counts = new Dictionary<string, int>();
        foreach (var t in terms)
            counts[t] = counts.GetValueOrDefault(t) + 1;

        return new Doc { Scenario = s, TermCounts = counts, Length = terms.Count };
    }

    private static Dictionary<string, int> BuildDocumentFrequency(List<Doc> docs)
    {
        var df = new Dictionary<string, int>();
        foreach (var doc in docs)
            foreach (var term in doc.TermCounts.Keys)
                df[term] = df.GetValueOrDefault(term) + 1;
        return df;
    }

    private static double ScoreDocument(Doc doc, List<string> queryTerms, Dictionary<string, int> df, int totalDocs, double avgDocLength)
    {
        double score = 0;
        foreach (var term in queryTerms)
        {
            if (!doc.TermCounts.TryGetValue(term, out var freq)) continue;

            var n = df.GetValueOrDefault(term, 0);
            var idf = Math.Log(1 + (totalDocs - n + 0.5) / (n + 0.5));
            var lengthNorm = 1 - B + B * (doc.Length / Math.Max(avgDocLength, 1));
            var termScore = idf * (freq * (K1 + 1)) / (freq + K1 * lengthNorm);
            score += termScore;
        }
        return score;
    }

    /// <summary>
    /// Builds the query text from natural-language PR/work-item context — deliberately
    /// excludes code symbols and diff snippets, since this ranker targets the natural-language
    /// side of the matching problem (see class-level remarks).
    /// </summary>
    public static string BuildQueryText(PrMetadata? prMetadata, List<WorkItemInfo>? linkedWorkItems)
    {
        var parts = new List<string?> { prMetadata?.Title, prMetadata?.Description };
        if (linkedWorkItems != null)
            foreach (var wi in linkedWorkItems)
                parts.AddRange(new[] { wi.Title, wi.Description, wi.ReproSteps, wi.AcceptanceCriteria });

        return string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static IEnumerable<string> Tokenize(string text) =>
        Regex.Matches(text, @"[a-zA-Z][a-zA-Z\-']{2,}")
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => !Stopwords.Contains(w));
}
