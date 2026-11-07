namespace Haytham.Core.Store;

/// <summary>What the caller expects the content to be. Every field is optional; a mismatch fails the import.</summary>
public sealed record ExpectedContent
{
    public Sha1? Sha1 { get; init; }

    public long? Size { get; init; }

    /// <summary>SHA-512 as 128 hex characters, when the source provides one.</summary>
    public string? Sha512 { get; init; }
}

public enum ImportOutcome
{
    /// <summary>The object was not in the store and is now.</summary>
    Added,

    /// <summary>An intact copy was already in the store; nothing was written.</summary>
    AlreadyPresent,

    /// <summary>A corrupt copy was moved aside into <c>tmp/</c> and replaced.</summary>
    ReplacedCorrupt,
}

public sealed record ImportResult(Sha1 Sha1, long Size, ImportOutcome Outcome);
