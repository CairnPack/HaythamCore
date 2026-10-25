using System.Collections.Concurrent;

namespace Haytham.Core.Store;

/// <summary>Size and last write time of an object file, taken before it was hashed.</summary>
readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc)
{
    public static FileStamp Of(FileInfo file) => new(file.Length, file.LastWriteTimeUtc);
}

/// <summary>
/// What the fast verify mode compares against: the stamp of each object when it last passed a full hash. Kept in
/// memory only, so a new process hashes each object once again; losing the records only costs a rehash.
/// </summary>
sealed class VerificationRecords
{
    readonly ConcurrentDictionary<Sha1, FileStamp> _stamps = new();

    public bool Matches(Sha1 sha1, FileStamp stamp) => _stamps.TryGetValue(sha1, out var known) && known == stamp;

    public void Remember(Sha1 sha1, FileStamp stamp) => _stamps[sha1] = stamp;

    public void Forget(Sha1 sha1) => _stamps.TryRemove(sha1, out _);
}
