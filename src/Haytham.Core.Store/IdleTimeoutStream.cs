using System.Globalization;

namespace Haytham.Core.Store;

/// <summary>
/// Wraps a response body so that a read which gets no data for <c>timeout</c> fails with a
/// <see cref="TimeoutException"/>. The timer starts anew for every read, so a slow but steady body never times out,
/// and the time the reader spends between reads (hashing, writing to disk) does not count.
/// </summary>
/// <remarks>
/// The caller's own cancellation always wins: when its token is cancelled the read throws
/// <see cref="OperationCanceledException"/> as usual, even if the idle timer fired too. Only the asynchronous reads
/// are timed; the synchronous <see cref="Read(byte[], int, int)"/> passes straight through.
/// </remarks>
sealed class IdleTimeoutStream(Stream inner, TimeSpan timeout) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(timeout);
        try
        {
            return await inner.ReadAsync(buffer, idle.Token);
        }
        catch (Exception e) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Cancelling a read may surface as an OperationCanceledException or as an IOException from the
            // aborted connection, so the timer's state decides, not the exception type.
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"No data received for {timeout.TotalSeconds:0.###}s."),
                e);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
