namespace Haytham.Core.Store;

/// <summary>
/// A cross-process lock on a file, built on <see cref="FileStream"/> share modes: on Windows the system enforces
/// them; on POSIX .NET takes a non-blocking <c>flock</c> when it opens the file. Opening the file is taking the
/// lock and disposing it is releasing. Two handles in one process conflict just like two processes do. The lock
/// file is never deleted (deleting it races with a waiter that already opened the path), and the handle is not
/// inheritable, so child processes such as the game never keep it alive.
/// </summary>
sealed class FileLock : IDisposable
{
    static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(50);
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(1);

    readonly FileStream _handle;
    readonly string _holderRecord;

    FileLock(FileStream handle, string holderRecord)
    {
        _handle = handle;
        _holderRecord = holderRecord;
    }

    /// <summary>
    /// Waits for the exclusive lock, backing off from 50 ms up to 1 s between tries. Fails with
    /// <see cref="StoreError.LockTimeout"/>, naming the holders, once <paramref name="timeout"/> has passed.
    /// </summary>
    /// <param name="timeProvider">Measures the wait, runs the backoff, and dates the holder record.</param>
    public static async Task<FileLock> AcquireExclusiveAsync(
        string path,
        ProgramInfo program,
        string operation,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var started = timeProvider.GetTimestamp();
        var backoff = FirstBackoff;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream handle;
            try
            {
                handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException e) when (IsHeldByOther(e))
            {
                var elapsed = timeProvider.GetElapsedTime(started);
                if (elapsed >= timeout)
                    throw TimedOut(path, operation, timeout, e);
                await Task.Delay(Min(backoff, timeout - elapsed), timeProvider, cancellationToken).ConfigureAwait(false);
                backoff = Min(backoff * 2, MaxBackoff);
                continue;
            }

            try
            {
                return new FileLock(handle, LockHolders.Register(path, program, operation, timeProvider.GetUtcNow()));
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            // Remove the record first, so nobody reads it after the lock is free.
            LockHolders.Unregister(_holderRecord);
        }
        finally
        {
            _handle.Dispose();
        }
    }

    static StoreException TimedOut(string path, string operation, TimeSpan timeout, IOException contention)
    {
        string description;
        Exception cause = contention;
        try
        {
            description = LockHolders.Describe(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            description = $"an unknown holder (the holder records could not be read: {error.GetType().Name}: {error.Message})";
            cause = new AggregateException(contention, error);
        }

        return new StoreException(StoreError.LockTimeout,
            $"Waited {timeout} for the lock '{path}' to {operation}; it is held by {description}.", cause);
    }

    /// <summary>
    /// True only when the open failed because someone holds the lock. Any other I/O error fails at once instead of
    /// being retried until the timeout.
    /// </summary>
    static bool IsHeldByOther(IOException e)
    {
        if (e is FileNotFoundException or DirectoryNotFoundException or PathTooLongException) return false;
        if (OperatingSystem.IsWindows())
        {
            // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION.
            var code = e.HResult & 0xFFFF;
            return code is 32 or 33;
        }

        // .NET reports a failed non-blocking flock with the raw errno: EWOULDBLOCK is 11 on Linux, 35 on macOS.
        return e.HResult == (OperatingSystem.IsLinux() ? 11 : 35);
    }

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
