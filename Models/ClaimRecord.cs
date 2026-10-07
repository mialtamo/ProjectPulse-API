namespace ProjectPulse.Api.Models;

public sealed record ClaimRecord(
    string UniqueId,
    string PayloadJson,
    string Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ProcessingAt,
    string? ResultJson);
