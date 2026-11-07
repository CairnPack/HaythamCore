namespace Haytham.Core.Store;

public enum VerifyMode
{
    /// <summary>
    /// Passes an object whose size and last write time still match its last full hash, and hashes it otherwise.
    /// A performance shortcut, not a safety guarantee: an edit that keeps the size and restores the time passes.
    /// </summary>
    Fast,

    /// <summary>Always hashes the object.</summary>
    Full,
}

public enum ObjectState
{
    Valid,
    Missing,
    Corrupt,
}
