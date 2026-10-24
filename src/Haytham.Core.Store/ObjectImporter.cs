namespace Haytham.Core.Store;

/// <summary>
/// Puts content into <c>objects/</c>: write <c>tmp/object-*.part</c> while hashing, check it against what was
/// expected, make it read-only, then move it in without overwriting. An object therefore is either complete and
/// read-only or absent, and its read-only flag is never cleared, not even to replace a corrupt copy.
/// </summary>
sealed class ObjectImporter(StoreLayout layout, ObjectVerifier verifier, VerificationRecords records)
{
    const int PublishAttempts = 3;

    public async Task<ImportResult> ImportAsync(Stream content, ExpectedContent? expected, CancellationToken cancellationToken)
    {
        var expectedSha512 = Validate(expected, nameof(expected));
        if (expected is not null
            && await FindExistingAsync(expected, expectedSha512, cancellationToken).ConfigureAwait(false) is { } existing)
            return existing;

        Directory.CreateDirectory(layout.TempDirectory);
        var part = layout.NewPartPath();
        try
        {
            HashedContent hashed;
            var stream = new FileStream(
                part,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 0,
                FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                hashed = await ContentHasher.CopyAndHashAsync(
                    content,
                    stream,
                    includeSha512: expectedSha512 is not null,
                    cancellationToken,
                    expected?.Size).ConfigureAwait(false);
                // Without this, a power cut after the move can leave an empty file under the object's name.
                stream.Flush(flushToDisk: true);
            }

            CheckExpected(expected, expectedSha512, hashed);
            File.SetAttributes(part, File.GetAttributes(part) | FileAttributes.ReadOnly);
            var outcome = await PublishAsync(part, hashed.Sha1, cancellationToken).ConfigureAwait(false);
            return new ImportResult(hashed.Sha1, hashed.Size, outcome);
        }
        finally
        {
            DeletePart(part);
        }
    }

    /// <summary>
    /// Checks the expectations themselves, before anything is read or downloaded, and returns the SHA-512 as bytes.
    /// Imports and downloads both call this, so a malformed expectation fails the same way in each.
    /// </summary>
    internal static byte[]? Validate(ExpectedContent? expected, string parameterName)
    {
        if (expected?.Size < 0) throw new ArgumentException("The expected size cannot be negative.", parameterName);
        if (expected?.Sha512 is not { } hex) return null;
        if (hex.Length != 128) throw new ArgumentException("SHA-512 must be 128 hex characters.", parameterName);
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException e)
        {
            throw new ArgumentException("SHA-512 must be 128 hex characters.", parameterName, e);
        }
    }

    /// <summary>
    /// What an import of <paramref name="expected"/> answers without reading any content. When the store holds an
    /// intact object under the expected SHA-1, the object must still meet the other expectations, so that the
    /// answer is the same as for an empty store: <see cref="ImportOutcome.AlreadyPresent"/> when it does, a
    /// <see cref="StoreError.ContentMismatch"/> when it does not. Returns <see langword="null"/> when there is no
    /// SHA-1 or no intact object. Downloads ask this before and after taking the download lock.
    /// </summary>
    /// <param name="expectedSha512">The SHA-512 from <see cref="Validate"/>.</param>
    internal async Task<ImportResult?> FindExistingAsync(
        ExpectedContent expected,
        byte[]? expectedSha512,
        CancellationToken cancellationToken)
    {
        if (expected.Sha1 is not { } known
            || await verifier.VerifyAsync(known, VerifyMode.Fast, cancellationToken).ConfigureAwait(false) != ObjectState.Valid)
            return null;

        var existing = new FileInfo(layout.ObjectPath(known));
        if (await FindMismatchAsync(existing, expected, expectedSha512, cancellationToken).ConfigureAwait(false) is { } detail)
            throw Mismatch(detail);
        return new ImportResult(known, existing.Length, ImportOutcome.AlreadyPresent);
    }

    /// <summary>Describes how an intact stored object differs from the expectations, or null when it does not.</summary>
    async Task<string?> FindMismatchAsync(
        FileInfo existing,
        ExpectedContent expected,
        byte[]? expectedSha512,
        CancellationToken cancellationToken)
    {
        if (expected.Size is { } size && size != existing.Length)
            return $"size {existing.Length} bytes, expected {size}";
        if (expectedSha512 is null) return null;

        try
        {
            var stream = new FileStream(
                existing.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 0,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var _ = stream.ConfigureAwait(false);
            var hashed = await ContentHasher.CopyAndHashAsync(stream, null, includeSha512: true, cancellationToken)
                .ConfigureAwait(false);
            return expectedSha512.AsSpan().SequenceEqual(hashed.Sha512)
                ? null
                : $"SHA-512 {Convert.ToHexStringLower(hashed.Sha512!)}, expected {expected.Sha512}";
        }
        catch (FileNotFoundException)
        {
            return "the object disappeared while it was being checked";
        }
    }

    async Task<ImportOutcome> PublishAsync(string part, Sha1 sha1, CancellationToken cancellationToken)
    {
        var target = layout.ObjectPath(sha1);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var replaced = false;
        for (var attempt = 0; attempt < PublishAttempts; attempt++)
        {
            try
            {
                File.Move(part, target, overwrite: false);
                // The first fast verify in this process then needs no rehash.
                records.Remember(sha1, FileStamp.Of(new FileInfo(target)));
                return replaced ? ImportOutcome.ReplacedCorrupt : ImportOutcome.Added;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Someone else's object, or an old one: only a full hash can tell whether to keep it.
                switch (await verifier.VerifyAsync(sha1, VerifyMode.Full, cancellationToken).ConfigureAwait(false))
                {
                    case ObjectState.Valid:
                        return ImportOutcome.AlreadyPresent;
                    case ObjectState.Corrupt:
                        MoveAside(target, sha1);
                        replaced = true;
                        break;
                }
            }
        }

        throw new IOException($"Could not move a verified object into '{target}' after {PublishAttempts} attempts.");
    }

    /// <summary>
    /// Renames a corrupt object into <c>tmp/</c>. A rename leaves the read-only flag alone, so hard links in
    /// instances keep pointing at the same (corrupt) data until their next install fixes them. Deleting it needs
    /// the delete that ignores the read-only flag, which comes with temp directory cleanup.
    /// </summary>
    void MoveAside(string target, Sha1 sha1)
    {
        try
        {
            File.Move(target, layout.NewCorruptPath(sha1), overwrite: false);
        }
        catch (FileNotFoundException)
        {
            // Another import moved it first.
        }
        catch (IOException e) when (File.Exists(target))
        {
            // On Windows a handle opened without delete sharing, a running game's jar for one, blocks the rename.
            throw new StoreException(
                StoreError.ObjectInUse,
                $"The corrupt object {sha1} cannot be replaced because another process is using '{target}'.",
                e);
        }
    }

    static void CheckExpected(ExpectedContent? expected, byte[]? expectedSha512, HashedContent hashed)
    {
        if (expected is null) return;
        if (expected.Size is { } size && size != hashed.Size)
            throw Mismatch($"size {hashed.Size} bytes, expected {size}");
        if (expected.Sha1 is { } sha1 && sha1 != hashed.Sha1)
            throw Mismatch($"SHA-1 {hashed.Sha1}, expected {sha1}");
        if (expectedSha512 is not null && !expectedSha512.AsSpan().SequenceEqual(hashed.Sha512))
            throw Mismatch($"SHA-512 {Convert.ToHexStringLower(hashed.Sha512!)}, expected {expected.Sha512}");
    }

    static StoreException Mismatch(string detail) =>
        new(StoreError.ContentMismatch, $"The content does not match: {detail}.");

    /// <summary>
    /// The part file belongs to this import and was never linked, so clearing its read-only flag is safe. A part
    /// that cannot be deleted now is left for temp directory cleanup.
    /// </summary>
    static void DeletePart(string part)
    {
        try
        {
            if (!File.Exists(part)) return;
            File.SetAttributes(part, FileAttributes.Normal);
            File.Delete(part);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
