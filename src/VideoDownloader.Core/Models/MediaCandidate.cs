namespace VideoDownloader.Core.Models;

public sealed record MediaCandidate(
    MediaResource Resource,
    CandidateKind Kind,
    int Confidence,
    string DetectionReason);
