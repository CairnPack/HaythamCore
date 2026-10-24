using System.Security.Cryptography;
using System.Text;

namespace Haytham.Core.Store.Tests;

public class ImportAndVerifyTests
{
    static readonly byte[] Content = Encoding.UTF8.GetBytes("fabric-api-0.92.2+1.20.1.jar");
    static readonly Sha1 ContentSha1 = Sha1.FromBytes(SHA1.HashData(Content));

    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bytes_land_under_their_sha1_read_only_and_tmp_is_left_empty()
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();

        var result = await store.ImportBytesAsync(Content, cancellationToken: Token);

        var hex = ContentSha1.ToString();
        var path = Path.Combine(dir.Path, "objects", hex[..2], hex);
        Assert.Equal(new ImportResult(ContentSha1, Content.Length, ImportOutcome.Added), result);
        Assert.Equal(path, store.GetObjectPath(ContentSha1));
        Assert.Equal(Content, File.ReadAllBytes(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        Assert.Empty(dir.TempEntries());
    }

    [Fact]
    public async Task Files_and_streams_are_imported_the_same_way_and_the_original_is_untouched()
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();
        var original = Path.Combine(dir.Path, "..", $"{Guid.NewGuid():N}.jar");
        await File.WriteAllBytesAsync(original, Content, Token);
        try
        {
            var fromFile = await store.ImportFileAsync(original, cancellationToken: Token);
            using var stream = new MemoryStream(Content);
            var fromStream = await store.ImportAsync(stream, cancellationToken: Token);

            Assert.Equal(ImportOutcome.Added, fromFile.Outcome);
            Assert.Equal(ImportOutcome.AlreadyPresent, fromStream.Outcome);
            Assert.Equal(ContentSha1, fromStream.Sha1);
            Assert.False(File.GetAttributes(original).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(Content, await File.ReadAllBytesAsync(original, Token));
        }
        finally
        {
            File.Delete(original);
        }
    }

    [Fact]
    public async Task Importing_again_reports_already_present()
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();

        await store.ImportBytesAsync(Content, cancellationToken: Token);
        var again = await store.ImportBytesAsync(Content, cancellationToken: Token);

        Assert.Equal(ImportOutcome.AlreadyPresent, again.Outcome);
        Assert.Empty(dir.TempEntries());
    }

    [Fact]
    public async Task A_known_intact_object_is_not_read_again()
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();
        await store.ImportBytesAsync(Content, cancellationToken: Token);

        var result = await store.ImportAsync(
            new UnreadableStream(),
            new ExpectedContent { Sha1 = ContentSha1 },
            Token);

        Assert.Equal(new ImportResult(ContentSha1, Content.Length, ImportOutcome.AlreadyPresent), result);
    }

    public static TheoryData<ExpectedContent> Mismatches() =>
    [
        new ExpectedContent { Size = Content.Length + 1 },
        new ExpectedContent { Sha1 = Sha1.Parse("a9993e364706816aba3e25717850c26c9cd0d89d") },
        new ExpectedContent { Sha512 = new string('0', 128) },
    ];

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task Content_that_does_not_match_never_enters_the_store(ExpectedContent expected)
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();

        var error = await Assert.ThrowsAsync<StoreException>(() => store.ImportBytesAsync(Content, expected, Token));

        Assert.Equal(StoreError.ContentMismatch, error.Error);
        Assert.Equal(ObjectState.Missing, await store.VerifyAsync(ContentSha1, VerifyMode.Full, Token));
        Assert.Empty(dir.TempEntries());
    }

    [Fact]
    public async Task Matching_size_sha1_and_sha512_are_accepted()
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();
        var expected = new ExpectedContent
        {
            Size = Content.Length,
            Sha1 = ContentSha1,
            Sha512 = Convert.ToHexString(SHA512.HashData(Content)),
        };

        var result = await store.ImportBytesAsync(Content, expected, Token);

        Assert.Equal(ImportOutcome.Added, result.Outcome);
    }

    [Fact]
    public async Task Concurrent_imports_of_one_object_publish_it_once()
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.ImportBytesAsync(Content, cancellationToken: Token))));

        Assert.Single(results, r => r.Outcome == ImportOutcome.Added);
        Assert.All(results, r => Assert.Equal(ContentSha1, r.Sha1));
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "objects"), "*", SearchOption.AllDirectories));
        Assert.Empty(dir.TempEntries());
    }

    [Fact]
    public async Task A_corrupt_object_is_moved_aside_and_replaced_without_clearing_read_only()
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();
        await store.ImportBytesAsync(Content, cancellationToken: Token);
        var path = store.GetObjectPath(ContentSha1);
        TempStoreDirectory.Tamper(path, Encoding.UTF8.GetBytes("overwritten by a mod"));

        Assert.Equal(ObjectState.Corrupt, await store.VerifyAsync(ContentSha1, VerifyMode.Full, Token));

        var result = await store.ImportBytesAsync(Content, cancellationToken: Token);

        Assert.Equal(ImportOutcome.ReplacedCorrupt, result.Outcome);
        Assert.Equal(ObjectState.Valid, await store.VerifyAsync(ContentSha1, VerifyMode.Full, Token));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        var aside = Assert.Single(dir.TempEntries());
        Assert.StartsWith($"corrupt-{ContentSha1}-", aside);
        var asidePath = Path.Combine(dir.Path, "tmp", aside);
        Assert.Equal("overwritten by a mod", await File.ReadAllTextAsync(asidePath, Token));
        Assert.True(File.GetAttributes(asidePath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task Verify_reports_missing_objects()
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();

        Assert.Equal(ObjectState.Missing, await store.VerifyAsync(ContentSha1, VerifyMode.Fast, Token));
        Assert.Equal(ObjectState.Missing, await store.VerifyAsync(ContentSha1, VerifyMode.Full, Token));
    }

    [Fact]
    public async Task Fast_mode_rehashes_when_size_or_time_changed()
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();
        await store.ImportBytesAsync(Content, cancellationToken: Token);
        Assert.Equal(ObjectState.Valid, await store.VerifyAsync(ContentSha1, VerifyMode.Full, Token));

        TempStoreDirectory.Tamper(store.GetObjectPath(ContentSha1), Encoding.UTF8.GetBytes("shorter"));

        Assert.Equal(ObjectState.Corrupt, await store.VerifyAsync(ContentSha1, VerifyMode.Fast, Token));
    }

    [Fact]
    public async Task Fast_mode_is_a_shortcut_that_misses_edits_which_keep_size_and_time()
    {
        using var dir = new TempStoreDirectory();
        var store = dir.Create();
        await store.ImportBytesAsync(Content, cancellationToken: Token);
        var path = store.GetObjectPath(ContentSha1);
        Assert.Equal(ObjectState.Valid, await store.VerifyAsync(ContentSha1, VerifyMode.Full, Token));
        var time = File.GetLastWriteTimeUtc(path);

        var sameLength = (byte[])Content.Clone();
        sameLength[0] ^= 0xFF;
        TempStoreDirectory.Tamper(path, sameLength);
        File.SetAttributes(path, FileAttributes.Normal);
        File.SetLastWriteTimeUtc(path, time);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.Equal(ObjectState.Valid, await store.VerifyAsync(ContentSha1, VerifyMode.Fast, Token));
        Assert.Equal(ObjectState.Corrupt, await store.VerifyAsync(ContentSha1, VerifyMode.Full, Token));
    }

    /// <summary>Fails the test if anything tries to read it.</summary>
    sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The content should not have been read.");

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
