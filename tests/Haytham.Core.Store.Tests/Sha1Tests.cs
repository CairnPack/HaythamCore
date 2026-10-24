using System.Security.Cryptography;
using System.Text;

namespace Haytham.Core.Store.Tests;

public class Sha1Tests
{
    // SHA-1 of "abc", from FIPS 180-2.
    const string Abc = "a9993e364706816aba3e25717850c26c9cd0d89d";

    [Fact]
    public void Round_trips_through_text_and_bytes()
    {
        var sha1 = Sha1.Parse(Abc);

        Assert.Equal(Abc, sha1.ToString());
        Assert.Equal(sha1, Sha1.FromBytes(SHA1.HashData(Encoding.ASCII.GetBytes("abc"))));

        var bytes = new byte[Sha1.ByteLength];
        sha1.WriteBytes(bytes);
        Assert.Equal(sha1, Sha1.FromBytes(bytes));
    }

    [Fact]
    public void Accepts_uppercase_and_prints_lowercase()
    {
        var sha1 = Sha1.Parse(Abc.ToUpperInvariant());

        Assert.Equal(Abc, sha1.ToString());
        Assert.True(sha1 == Sha1.Parse(Abc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a9993e364706816aba3e25717850c26c9cd0d89")]
    [InlineData("a9993e364706816aba3e25717850c26c9cd0d89d0")]
    [InlineData("g9993e364706816aba3e25717850c26c9cd0d89d")]
    public void Rejects_anything_but_40_hex_characters(string text)
    {
        Assert.False(Sha1.TryParse(text, out _));
        Assert.Throws<FormatException>(() => Sha1.Parse(text));
    }

    [Fact]
    public void Differs_when_any_byte_differs()
    {
        var other = Sha1.Parse(Abc[..^1] + "e");

        Assert.NotEqual(Sha1.Parse(Abc), other);
        Assert.True(Sha1.Parse(Abc) != other);
    }
}
