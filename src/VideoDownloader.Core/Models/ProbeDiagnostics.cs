namespace VideoDownloader.Core.Models;

/// <summary>Whether a probe result can deliver full A/V or only a partial set.</summary>
public enum MediaAvailabilityKind
{
    Complete = 0,
    /// <summary>Audio tracks validated; video tracks denied or missing.</summary>
    AudioOnly = 1,
    /// <summary>Video access was explicitly denied (e.g. HTTP 403).</summary>
    VideoDenied = 2,
    Unavailable = 3
}

/// <summary>Structured probe decision for diagnostics (Y2) — not user-facing copy.</summary>
public sealed record ProbeCandidateDecision(
    string Source,
    string MediaKind,
    string? Ownership,
    string Outcome,
    string Reason,
    string? Host = null,
    string? Detail = null);
