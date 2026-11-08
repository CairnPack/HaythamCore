using System.Net;
using System.Security.Cryptography;
using System.Text;
using Haytham.Core.Testing;

namespace Haytham.Core.Store.Tests;

public sealed class ContentStoreContractTests
{
    static readonly byte[] Content = Encoding.UTF8.GetBytes("store-consumer-contract");
    static readonly Sha1 ContentSha1 = Sha1.FromBytes(SHA1.HashData(Content));
    static readonly Uri Source = new("https://store.invalid/object.jar");
    const string UserAgent = "store-contract-tests/1.0";
    static readonly DownloadOptions Options = new()
    {
        MaxAttemptsPerUrl = 1,
        ReadIdleTimeout = Timeout.InfiniteTimeSpan,
    };

    static CancellationToken Token => TestContext.Current.CancellationToken;

    static ObtainRequest Request() => new()
    {
        Sha1 = ContentSha1,
        Size = Content.Length,
        Urls = [Source],
    };

    [Fact]
    public void Factory_requires_an_existing_store_unless_creation_is_requested()
    {
        using var dir = new TempStoreDirectory();

        var error = Assert.Throws<StoreException>(() => ContentStoreFactory.Open(dir.Path, Options));

        Assert.Equal(StoreError.StoreMissing, error.Error);
        Assert.False(Directory.Exists(dir.Path));

        IContentStore created = ContentStoreFactory.Open(dir.Path, Options, StoreOpenMode.CreateIfMissing);
        IContentStore reopened = ContentStoreFactory.Open(dir.Path, Options);

        Assert.Equal(Path.GetFullPath(dir.Path), created.Root);
        Assert.NotEqual(Guid.Empty, created.StoreId);
        Assert.Equal(created.StoreId, reopened.StoreId);
        Assert.Equal(created.Format, reopened.Format);
        Assert.False(created.IsReadOnly);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("file")]
    [InlineData("bytes")]
    public async Task Imports_use_the_injected_store_and_preserve_validation_and_source_ownership(string input)
    {
        using var dir = new TempStoreDirectory();
        using var sourceDir = new TempStoreDirectory();
        Directory.CreateDirectory(sourceDir.Path);
        var sourcePath = Path.Combine(sourceDir.Path, "original.jar");
        await File.WriteAllBytesAsync(sourcePath, Content, Token);
        using var stream = new MemoryStream(Content);
        var existing = dir.Create();
        IContentStore store = ContentStoreFactory.Create(existing, Options);
        var expected = new ExpectedContent
        {
            Sha1 = ContentSha1,
            Size = Content.Length,
            Sha512 = Convert.ToHexStringLower(SHA512.HashData(Content)),
        };

        Task<ImportResult> Import(ExpectedContent checks) => input switch
        {
            "stream" => store.ImportAsync(stream, checks, Token),
            "file" => store.ImportFileAsync(sourcePath, checks, Token),
            _ => store.ImportBytesAsync(Content, checks, Token),
        };

        var imported = await Import(expected);

        Assert.Equal(existing.StoreId, store.StoreId);
        Assert.Equal(existing.Root, store.Root);
        Assert.Equal(existing.Format, store.Format);
        Assert.Equal(existing.IsReadOnly, store.IsReadOnly);
        Assert.Equal(new ImportResult(ContentSha1, Content.Length, ImportOutcome.Added), imported);
        Assert.Equal(existing.GetObjectPath(ContentSha1), store.GetObjectPath(ContentSha1));
        Assert.Equal(ObjectState.Valid, await existing.VerifyAsync(ContentSha1, VerifyMode.Full, Token));
        Assert.Equal(ObjectState.Valid, await store.VerifyAsync(ContentSha1, VerifyMode.Full, Token));
        Assert.Equal(Content, await File.ReadAllBytesAsync(store.GetObjectPath(ContentSha1), Token));
        Assert.Equal(Content, await File.ReadAllBytesAsync(sourcePath, Token));
        Assert.False(File.GetAttributes(sourcePath).HasFlag(FileAttributes.ReadOnly));
        Assert.True(stream.CanRead);

        var mismatch = await Assert.ThrowsAsync<StoreException>(() => Import(expected with { Size = Content.Length + 1 }));

        Assert.Equal(StoreError.ContentMismatch, mismatch.Error);
        Assert.Empty(dir.TempEntries());
    }

    [Fact]
    public async Task Downloads_and_imports_share_the_injected_store_and_use_the_callers_http_configuration()
    {
        using var dir = new TempStoreDirectory();
        var existing = dir.Create();
        var handler = new FakeHttpHandler();
        string? userAgent = null;
        handler.Respond(HttpMethod.Get, Source.AbsoluteUri, (request, _) =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Content),
            });
        });
        using var http = new HttpClient(handler);
        IContentStore store = ContentStoreFactory.Create(existing, Options);

        var downloaded = await store.ObtainAsync(Request(), http, UserAgent, Token);
        var imported = await store.ImportBytesAsync(Content, cancellationToken: Token);
        var reused = await store.ObtainAsync(Request(), http, UserAgent, Token);

        Assert.Equal(Source, downloaded.Source);
        Assert.Equal(ImportOutcome.Added, downloaded.Outcome);
        Assert.Equal(ImportOutcome.AlreadyPresent, imported.Outcome);
        Assert.Equal(ImportOutcome.AlreadyPresent, reused.Outcome);
        Assert.Null(reused.Source);
        Assert.Single(handler.Requests);
        Assert.Equal(UserAgent, userAgent);
        Assert.Equal(ObjectState.Valid, await existing.VerifyAsync(ContentSha1, VerifyMode.Full, Token));
        Assert.Equal(Content, await File.ReadAllBytesAsync(existing.GetObjectPath(ContentSha1), Token));

        using var response = await http.GetAsync(Source, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_reaches_imports_and_downloads_with_the_original_token(bool download)
    {
        using var dir = new TempStoreDirectory();
        var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        IContentStore store = ContentStoreFactory.Create(dir.Create(), Options);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (download)
                await store.ObtainAsync(Request(), http, UserAgent, cancellation.Token);
            else
                await store.ImportBytesAsync(Content, cancellationToken: cancellation.Token);
        });

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(handler.Requests);
        Assert.Empty(dir.TempEntries());
    }

    [Fact]
    public async Task Each_obtain_uses_its_own_client_and_user_agent()
    {
        using var dir = new TempStoreDirectory();
        IContentStore store = ContentStoreFactory.Create(dir.Create(), Options);
        byte[] secondContent = [1, 2, 3];
        var secondRequest = Request() with
        {
            Sha1 = Sha1.FromBytes(SHA1.HashData(secondContent)),
            Size = secondContent.Length,
        };
        var received = new List<string>();
        var firstHandler = new FakeHttpHandler().Respond(HttpMethod.Get, Source.AbsoluteUri, (request, _) =>
        {
            received.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) });
        });
        var secondHandler = new FakeHttpHandler().Respond(HttpMethod.Get, Source.AbsoluteUri, (request, _) =>
        {
            received.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(secondContent) });
        });
        using var firstClient = new HttpClient(firstHandler);
        using var secondClient = new HttpClient(secondHandler);

        var first = await store.ObtainAsync(Request(), firstClient, "first-host/1.0", Token);
        var second = await store.ObtainAsync(secondRequest, secondClient, "second-host/2.0", Token);

        Assert.Equal(ImportOutcome.Added, first.Outcome);
        Assert.Equal(ImportOutcome.Added, second.Outcome);
        Assert.Equal(["first-host/1.0", "second-host/2.0"], received);
        Assert.Single(firstHandler.Requests);
        Assert.Single(secondHandler.Requests);
        Assert.Empty(firstClient.DefaultRequestHeaders.UserAgent);
        Assert.Empty(secondClient.DefaultRequestHeaders.UserAgent);
    }

    [Fact]
    public async Task Read_only_state_and_failures_are_preserved_across_the_unified_contract()
    {
        using var dir = new TempStoreDirectory();
        var existing = dir.Create();
        await File.WriteAllTextAsync(
            Path.Combine(dir.Path, "format.json"),
            $$"""{"format":1,"minWriterFormat":2,"storeId":"{{existing.StoreId}}"}""",
            Token);
        var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        IContentStore store = ContentStoreFactory.Open(dir.Path, Options);

        Assert.True(store.IsReadOnly);
        Assert.Equal(existing.StoreId, store.StoreId);
        Assert.Equal(ObjectState.Missing, await store.VerifyAsync(ContentSha1, VerifyMode.Full, Token));
        var importError = await Assert.ThrowsAsync<StoreException>(() => store.ImportBytesAsync(Content, cancellationToken: Token));
        var downloadError = await Assert.ThrowsAsync<StoreException>(() => store.ObtainAsync(Request(), http, UserAgent, Token));

        Assert.Equal(StoreError.ReadOnly, importError.Error);
        Assert.Equal(StoreError.ReadOnly, downloadError.Error);
        Assert.Empty(handler.Requests);
        Assert.Empty(dir.TempEntries());
    }
}
