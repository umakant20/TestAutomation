"""
PR Sentiment & Workflow Analysis MCP Server
=============================================

A second, independent MCP server (separate process, separate language) registered
alongside the .NET pr-impact-mcp server. Copilot's agent mode can call tools from BOTH
servers in the same conversation — e.g. call the .NET server for changed-symbol/test-impact
analysis, and call this Python server for a sentiment/complexity read on the same PR,
then synthesize both into one combined answer.

Deliberately dependency-light: uses lexicon-based heuristics rather than downloading any
ML model, so it runs instantly with just `pip install mcp` and no GPU/model weights. If you
want more sophisticated analysis later, the natural upgrade path is swapping the heuristics
in `_score_sentiment` for a real classifier (e.g. `transformers` sentiment-analysis pipeline,
or `spaCy` + a trained pipeline) without changing the tool signatures Copilot already knows.

Run standalone for testing:
    pip install -r requirements.txt
    python server.py
"""

import re
from mcp.server.fastmcp import FastMCP

mcp = FastMCP("pr-sentiment-mcp")

# ── Lightweight lexicons (deliberately simple — see module docstring) ───────────────────────

_NEGATIVE_WORDS = {
    "urgent", "critical", "broken", "fail", "failing", "failed", "bug", "issue", "problem",
    "hotfix", "emergency", "blocker", "blocking", "regression", "crash", "crashing", "hack",
    "workaround", "temporary", "revert", "rollback", "risky", "unstable", "deprecated",
    "breaking", "incident", "outage", "escalation", "delay", "delayed", "concern", "confusing",
}

_POSITIVE_WORDS = {
    "improve", "improvement", "enhance", "enhancement", "refactor", "optimize", "optimization",
    "clean", "cleanup", "simplify", "modernize", "upgrade", "feature", "add", "support",
    "documentation", "test", "coverage", "stable", "consistent", "streamline",
}

_URGENCY_WORDS = {
    "urgent", "asap", "immediately", "critical", "emergency", "blocker", "blocking",
    "hotfix", "p0", "p1", "sev1", "sev2", "production", "prod",
}

_COMPLEXITY_KEYWORDS = re.compile(
    r"\b(if|else|elseif|for|foreach|while|switch|case|catch|try|async|await)\b",
    re.IGNORECASE,
)


def _tokenize(text: str) -> list[str]:
    return re.findall(r"[a-zA-Z][a-zA-Z\-']*", text.lower())


def _score_sentiment(text: str) -> dict:
    words = _tokenize(text)
    if not words:
        return {"label": "neutral", "score": 0.0, "matched_negative": [], "matched_positive": []}

    neg_hits = [w for w in words if w in _NEGATIVE_WORDS]
    pos_hits = [w for w in words if w in _POSITIVE_WORDS]

    score = (len(pos_hits) - len(neg_hits)) / max(len(words), 1)

    if score > 0.02:
        label = "positive"
    elif score < -0.02:
        label = "negative"
    else:
        label = "neutral"

    return {
        "label": label,
        "score": round(score, 4),
        "matched_negative": sorted(set(neg_hits)),
        "matched_positive": sorted(set(pos_hits)),
    }


def _score_urgency(text: str) -> dict:
    words = _tokenize(text)
    hits = [w for w in words if w in _URGENCY_WORDS]
    level = "high" if len(hits) >= 2 else ("medium" if len(hits) == 1 else "low")
    return {"level": level, "matched_terms": sorted(set(hits))}


@mcp.tool()
def analyze_pr_sentiment(title: str, description: str = "") -> dict:
    """
    Analyzes the tone and urgency of a pull request's title and description.

    Returns a sentiment label (positive/neutral/negative), a numeric score, the specific
    words that drove the classification, and a separate urgency assessment (low/medium/high)
    based on words like "urgent", "hotfix", "blocker", "production".

    Use this alongside the .NET pr-impact-mcp server's prepare_pr_analysis tool to add
    tone/urgency context to a test-impact report — e.g. flagging that a PR described as
    "urgent hotfix for production outage" deserves extra scrutiny on its impacted scenarios
    even if the code-level match confidence is only MEDIUM.
    """
    combined_text = f"{title}\n{description}"
    sentiment = _score_sentiment(combined_text)
    urgency = _score_urgency(combined_text)

    return {
        "sentiment": sentiment,
        "urgency": urgency,
        "word_count": len(_tokenize(combined_text)),
    }


@mcp.tool()
def analyze_workflow_complexity(diff_text: str) -> dict:
    """
    Estimates the structural complexity of a PR's code diff using simple heuristics:
    number of files touched, added/removed line counts, and a rough control-flow density
    score (count of if/for/while/switch/try keywords per changed line).

    Use this to add a "how risky/complex is this change, independent of test impact"
    signal alongside the .NET server's changed-symbol extraction — a PR touching many
    files with high control-flow density warrants more cautious review even when the
    test-impact analysis only flags a few scenarios.

    diff_text: the raw unified diff text (e.g. copy the "RawDiffText" content the .NET
    server's state.json captured, or any diff text you have available).
    """
    if not diff_text or not diff_text.strip():
        return {
            "files_touched": 0, "added_lines": 0, "removed_lines": 0,
            "control_flow_keyword_count": 0, "complexity_estimate": "unknown",
        }

    lines = diff_text.split("\n")

    files_touched = len({
        m.group(1) for line in lines
        if (m := re.match(r"^===\s+(.+?)\s+\(", line))
    })
    # Fallback for plain unified-diff style headers if no "=== file ===" markers present
    if files_touched == 0:
        files_touched = len({
            m.group(1) for line in lines
            if (m := re.match(r"^(?:\+\+\+|---)\s+(.+)$", line))
        })

    added_lines   = sum(1 for l in lines if l.startswith("+") and not l.startswith("+++"))
    removed_lines = sum(1 for l in lines if l.startswith("-") and not l.startswith("---"))

    changed_lines_text = "\n".join(
        l for l in lines if l.startswith("+") or l.startswith("-")
    )
    keyword_hits = len(_COMPLEXITY_KEYWORDS.findall(changed_lines_text))
    changed_line_count = max(added_lines + removed_lines, 1)
    density = keyword_hits / changed_line_count

    if density > 0.15 or files_touched > 10:
        estimate = "high"
    elif density > 0.05 or files_touched > 3:
        estimate = "medium"
    else:
        estimate = "low"

    return {
        "files_touched": files_touched,
        "added_lines": added_lines,
        "removed_lines": removed_lines,
        "control_flow_keyword_count": keyword_hits,
        "control_flow_density": round(density, 4),
        "complexity_estimate": estimate,
    }


if __name__ == "__main__":
    mcp.run(transport="stdio")
