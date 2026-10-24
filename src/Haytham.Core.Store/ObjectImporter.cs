namespace Haytham.Core.Store;

/// <summary>
/// Puts content into <c>objects/</c>: write <c>tmp/object-*.part</c> while hashing, check it against what was
/// expected, make it read-only, then move it in without overwriting. An object therefore is either complete and
/// read-only or absent, and its read-only flag is never cleared, not even to replace a corrupt copy.
/// </summary>
sealed class ObjectImporter(StoreLayout layout, ObjectVerifier verifier)
{
    const int PublishAttempts = 3;

    public async Task<ImportResult> ImportAsync(Stream content, ExpectedContent? expected, CancellationToken cancellationToken)
    {
        var expectedSha512 = ParseSha512(expected?.Sha512);
        if (expected?.Size < 0) throw new ArgumentException("The expected size cannot be negative.", nameof(expected));

        // Known key and an intact object: skip reading the content at all.
        if (expected?.Sha1 is { } known
            && await verifier.VerifyAsync(known, VerifyMode.Fast, cancellationToken) == ObjectState.Valid)
            return new ImportResult(known, new FileInfo(layout.ObjectPath(known)).Length, ImportOutcome.AlreadyPresent);

        Directory.CreateDirectory(layout.TempDirectory);
        var part = layout.NewPartPath();
        try
        {
            HashedContent hashed;
            await using (var stream = new FileStream(
                part,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 0,
                FileOptions.Asynchronous))
            {
                hashed = await ContentHasher.CopyAndHashAsync(
                    content,
                    stream,
                    includeSha512: expectedSha512 is not null,
                    cancellationToken);
                // Without this, a power cut after the move can leave an empty file under the object's name.
                stream.Flush(flushToDisk: true);
            }

            CheckExpected(expected, expectedSha512, hashed);
            File.SetAttributes(part, File.GetAttributes(part) | FileAttributes.ReadOnly);
            var outcome = await PublishAsync(part, hashed.Sha1, cancellationToken);
            return new ImportResult(hashed.Sha1, hashed.Size, outcome);
        }
        finally
        {
            DeletePart(part);
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
                return replaced ? ImportOutcome.ReplacedCorrupt : ImportOutcome.Added;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Someone else's object, or an old one: only a full hash can tell whether to keep it.
                switch (await verifier.VerifyAsync(sha1, VerifyMode.Full, cancellationToken))
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

    static byte[]? ParseSha512(string? hex)
    {
        if (hex is null) return null;
        if (hex.Length != 128) throw new ArgumentException("SHA-512 must be 128 hex characters.", nameof(hex));
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException e)
        {
            throw new ArgumentException("SHA-512 must be 128 hex characters.", nameof(hex), e);
        }
    }

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
