using System.Buffers;
using System.Security.Cryptography;

namespace Haytham.Core.Store;

readonly record struct HashedContent(long Size, Sha1 Sha1, byte[]? Sha512);

/// <summary>Reads a stream to the end, hashing it and optionally copying it, in one pass.</summary>
static class ContentHasher
{
    const int BufferSize = 81920;

    public static async Task<HashedContent> CopyAndHashAsync(
        Stream source,
        Stream? destination,
        bool includeSha512,
        CancellationToken cancellationToken)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = includeSha512 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA512) : null;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long size = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                sha1.AppendData(buffer, 0, read);
                sha512?.AppendData(buffer, 0, read);
                if (destination is not null) await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                size += read;
            }

            return new HashedContent(size, Sha1.FromBytes(sha1.GetHashAndReset()), sha512?.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
