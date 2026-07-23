"""
PySemanticRanker — TF-IDF + Truncated SVD semantic candidate search.

No external model downloads, no Hugging Face, no pretrained weights of any kind. The
"model" is trained fresh, every run, purely from the scenario corpus and PR text passed in
as input — nothing here ever leaves the machine, and nothing here is learned from anything
outside this specific PR's own analysis data.

Usage (invoked as a subprocess by PySemanticRanker.cs):
    python semantic_rank.py <input.json> <output.json>

Input JSON shape:
    {
      "query": "PR title + description + work item text",
      "scenarios": [{"id": 0, "text": "scenario name + steps + tags"}, ...],
      "topK": 30
    }

Output JSON shape (always written, even on failure — never crashes without producing output):
    {"topMatches": [{"id": 7, "score": 0.83}, ...]}
    or, on failure:
    {"error": "message", "topMatches": []}
"""

import json
import sys


def main():
    input_path, output_path = sys.argv[1], sys.argv[2]

    try:
        with open(input_path, "r", encoding="utf-8") as f:
            data = json.load(f)

        query = data.get("query", "")
        scenarios = data.get("scenarios", [])
        top_k = data.get("topK", 30)

        if not query.strip() or len(scenarios) == 0:
            write_output(output_path, {"topMatches": []})
            return

        from sklearn.feature_extraction.text import TfidfVectorizer
        from sklearn.decomposition import TruncatedSVD
        from sklearn.metrics.pairwise import cosine_similarity

        texts = [s["text"] for s in scenarios] + [query]

        tfidf = TfidfVectorizer(stop_words="english", max_features=5000)
        matrix = tfidf.fit_transform(texts)

        # n_components must be strictly less than both the number of documents and the
        # number of features — cap dynamically so this doesn't crash on a small corpus
        # (e.g. a PR whose pre-filter only surfaced a handful of candidate scenarios).
        max_components = min(100, matrix.shape[0] - 1, matrix.shape[1] - 1)
        if max_components < 2:
            # Too few documents/features for SVD to do anything meaningful — fall back to
            # raw TF-IDF cosine similarity (no dimensionality reduction) rather than failing.
            reduced = matrix.toarray()
        else:
            svd = TruncatedSVD(n_components=max_components)
            reduced = svd.fit_transform(matrix)

        query_vec = reduced[-1].reshape(1, -1)
        scenario_vecs = reduced[:-1]
        scores = cosine_similarity(query_vec, scenario_vecs)[0]

        ranked_indices = scores.argsort()[::-1][:top_k]
        top_matches = [
            {"id": scenarios[i]["id"], "score": float(scores[i])}
            for i in ranked_indices
            if scores[i] > 0
        ]

        write_output(output_path, {"topMatches": top_matches})

    except Exception as ex:
        # Never let this crash without writing valid output — the C# caller expects a
        # parseable file either way, so a failure here should degrade gracefully rather
        # than propagate as a confusing subprocess error.
        write_output(output_path, {"error": str(ex), "topMatches": []})


def write_output(path, obj):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f)


if __name__ == "__main__":
    main()
