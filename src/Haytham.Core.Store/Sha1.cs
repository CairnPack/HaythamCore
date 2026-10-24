using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Haytham.Core.Store;

/// <summary>
/// A SHA-1 digest: the key of every object in the store (<c>objects/&lt;first two&gt;/&lt;sha1&gt;</c>). Parsing
/// accepts either case; the text form is always lowercase.
/// </summary>
public readonly struct Sha1 : IEquatable<Sha1>
{
    public const int ByteLength = 20;
    public const int HexLength = ByteLength * 2;

    // Three fields keep the struct at 20 bytes: GC holds sets of hundreds of thousands of keys.
    readonly ulong _a;
    readonly ulong _b;
    readonly uint _c;

    Sha1(ulong a, ulong b, uint c)
    {
        _a = a;
        _b = b;
        _c = c;
    }

    public static Sha1 FromBytes(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != ByteLength)
            throw new ArgumentException($"A SHA-1 digest is {ByteLength} bytes, got {digest.Length}.", nameof(digest));
        return new Sha1(
            BinaryPrimitives.ReadUInt64BigEndian(digest),
            BinaryPrimitives.ReadUInt64BigEndian(digest[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(digest[16..]));
    }

    public static Sha1 Parse(string text) =>
        TryParse(text, out var sha1) ? sha1 : throw new FormatException($"'{text}' is not a SHA-1 hex string.");

    public static bool TryParse([NotNullWhen(true)] string? text, out Sha1 sha1) => TryParse(text.AsSpan(), out sha1);

    public static bool TryParse(ReadOnlySpan<char> text, out Sha1 sha1)
    {
        sha1 = default;
        if (text.Length != HexLength) return false;
        Span<byte> digest = stackalloc byte[ByteLength];
        if (Convert.FromHexString(text, digest, out _, out var written) != System.Buffers.OperationStatus.Done
            || written != ByteLength)
            return false;
        sha1 = FromBytes(digest);
        return true;
    }

    public void WriteBytes(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, _a);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _b);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], _c);
    }

    public override string ToString()
    {
        Span<byte> digest = stackalloc byte[ByteLength];
        WriteBytes(digest);
        return Convert.ToHexStringLower(digest);
    }

    public bool Equals(Sha1 other) => _a == other._a && _b == other._b && _c == other._c;

    public override bool Equals(object? obj) => obj is Sha1 other && Equals(other);

    // The digest is already uniformly distributed, so its first bytes are a good hash.
    public override int GetHashCode() => (int)_a;

    public static bool operator ==(Sha1 left, Sha1 right) => left.Equals(right);

    public static bool operator !=(Sha1 left, Sha1 right) => !left.Equals(right);
}
