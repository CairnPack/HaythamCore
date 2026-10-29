namespace Haytham.Core.Store;

/// <summary>
/// Puts content into <c>objects/</c>: write <c>tmp/object-*.part</c> while hashing, check it against what was
/// expected, make it read-only, then move it in without overwriting. An object therefore is either complete and
/// read-only or absent, and its read-only flag is never cleared, not even to replace a corrupt copy.
/// </summary>
sealed class ObjectImporter(
    StoreLayout layout, ObjectVerifier verifier, VerificationRecords records, ProgramInfo program, TimeProvider timeProvider)
{
    const int PublishAttempts = 3;
    static readonly TimeSpan RepairLockTimeout = TimeSpan.FromMinutes(2);

    public async Task<ImportResult> ImportAsync(
        Stream content, ExpectedContent? expected, CancellationToken cancellationToken, string source = "import")
    {
        var expectedSha512 = Validate(expected, nameof(expected));
        if (expected is not null
            && await FindExistingAsync(expected, expectedSha512, cancellationToken).ConfigureAwait(false) is { } existing)
            return existing;

        Directory.CreateDirectory(layout.TempDirectory);
        var part = layout.NewPartPath();
        var ownsPart = false;
        Exception? operationError = null;
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
            ownsPart = true;
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
            var outcome = await PublishAsync(part, hashed.Sha1, source, cancellationToken).ConfigureAwait(false);
            return new ImportResult(hashed.Sha1, hashed.Size, outcome);
        }
        catch (Exception error)
        {
            operationError = error;
            throw;
        }
        finally
        {
            if (ownsPart) DeletePart(part, operationError);
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

    /// <summary>Publishes a complete, verified, read-only part.</summary>
    async Task<ImportOutcome> PublishAsync(string part, Sha1 sha1, string source, CancellationToken cancellationToken)
    {
        var target = layout.ObjectPath(sha1);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        for (var attempt = 0; attempt < PublishAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryPublish(part, target, sha1)) return ImportOutcome.Added;
            switch (await verifier.VerifyAsync(sha1, VerifyMode.Full, cancellationToken).ConfigureAwait(false))
            {
                case ObjectState.Valid:
                    return ImportOutcome.AlreadyPresent;
                case ObjectState.Corrupt:
                    return await RepairAsync(part, sha1, source, cancellationToken).ConfigureAwait(false);
            }
        }

        throw PublishFailed(target);
    }

    /// <summary>Only prepared, verified content enters this leaf lock; no network or other lock is acquired here.</summary>
    async Task<ImportOutcome> RepairAsync(string part, Sha1 sha1, string source, CancellationToken cancellationToken)
    {
        using var held = await FileLock.AcquireExclusiveAsync(
            layout.RepairLockPath(sha1), program, $"repair {sha1} ({source})",
            RepairLockTimeout, timeProvider, cancellationToken).ConfigureAwait(false);
        // The pre-lock verdict may describe a file another repair has already replaced.
        var state = await verifier.VerifyAsync(sha1, VerifyMode.Full, cancellationToken).ConfigureAwait(false);
        if (state == ObjectState.Valid) return ImportOutcome.AlreadyPresent;
        var target = layout.ObjectPath(sha1);
        if (state == ObjectState.Corrupt) MoveAside(target, sha1);
        cancellationToken.ThrowIfCancellationRequested();
        // A normal import can publish first during the gap; every writer verifies its content before publishing.
        var added = TryPublish(part, target, sha1);
        if (state == ObjectState.Corrupt) return ImportOutcome.ReplacedCorrupt;
        return added ? ImportOutcome.Added : ImportOutcome.AlreadyPresent;
    }

    bool TryPublish(string part, string target, Sha1 sha1)
    {
        try
        {
            File.Move(part, target, overwrite: false);
        }
        catch (IOException e) when (IsTargetCollision(e))
        {
            return false;
        }

        // These failures must propagate; only the move itself can lose a publication race.
        records.Remember(sha1, FileStamp.Of(new FileInfo(target)));
        return true;
    }

    static IOException PublishFailed(string target) =>
        new($"Could not move a verified object into '{target}' after {PublishAttempts} attempts.");

    static bool IsTargetCollision(IOException error) =>
        OperatingSystem.IsWindows()
            ? (error.HResult & 0xFFFF) is 80 or 183 // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS.
            : error.HResult == 17; // EEXIST, reported as raw errno by .NET on POSIX.

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
        catch (IOException e) when (OperatingSystem.IsWindows() && (e.HResult & 0xFFFF) is 32 or 33)
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
    /// Only the part created by this import is cleaned up. A successful publication has already moved it away.
    /// Cleanup failures are reported, together with the import failure when both fail.
    /// </summary>
    internal static void DeletePart(string part, Exception? operationError)
    {
        try
        {
            try
            {
                File.SetAttributes(part, FileAttributes.Normal);
            }
            catch (FileNotFoundException)
            {
                return;
            }
            File.Delete(part);
        }
        catch (Exception cleanupError) when (operationError is not null)
        {
            throw new AggregateException("Import and part cleanup both failed.", operationError, cleanupError);
        }
    }
}
