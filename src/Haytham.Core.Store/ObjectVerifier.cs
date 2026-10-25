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

/// <summary>Tells whether an object in the store is intact. Callers never inspect <c>objects/</c> themselves.</summary>
sealed class ObjectVerifier(StoreLayout layout, VerificationRecords records)
{
    public async Task<ObjectState> VerifyAsync(Sha1 sha1, VerifyMode mode, CancellationToken cancellationToken)
    {
        var file = new FileInfo(layout.ObjectPath(sha1));
        if (!file.Exists)
        {
            records.Forget(sha1);
            return ObjectState.Missing;
        }

        // Taken before reading: if the file changes while it is hashed, the next fast check sees a new stamp.
        var stamp = FileStamp.Of(file);
        if (mode == VerifyMode.Fast && records.Matches(sha1, stamp)) return ObjectState.Valid;

        HashedContent hashed;
        try
        {
            // Share delete so that a concurrent import can move a corrupt object aside while it is being read.
            var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 0,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var _ = stream.ConfigureAwait(false);
            hashed = await ContentHasher.CopyAndHashAsync(stream, null, includeSha512: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            records.Forget(sha1);
            return ObjectState.Missing;
        }

        if (hashed.Sha1 != sha1)
        {
            records.Forget(sha1);
            return ObjectState.Corrupt;
        }

        records.Remember(sha1, stamp);
        return ObjectState.Valid;
    }
}
