namespace DocsSnippets;

/// <summary>An insurance claim: just enough shape for the samples that ask "as of the claim".</summary>
public sealed record Claim(int PolicyId, DateTimeOffset OccurredAt);
