using System.Text.Json;

namespace Haytham.Core.Store;

/// <summary>Contents of <c>&lt;lock file&gt;.holders/&lt;pid&gt;-&lt;start time&gt;.json</c>.</summary>
sealed class LockHolderRecord
{
    public int Format { get; set; }

    public string Program { get; set; } = "";

    public string Version { get; set; } = "";

    public int Pid { get; set; }

    public DateTimeOffset StartTime { get; set; }

    public string Operation { get; set; } = "";

    public DateTimeOffset Since { get; set; }
}

/// <summary>
/// Who holds a lock, written next to it so that a waiter can tell the user. These records are for display only:
/// whether a lock is held is decided by the system lock alone, never by these files.
/// </summary>
static class LockHolders
{
    const int RecordFormat = 1;

    public static string DirectoryOf(string lockPath) => lockPath + ".holders";

    /// <summary>
    /// Records the current process as a holder and removes records of processes that have ended. Returns the record
    /// path, or <see langword="null"/> when it could not be written: a missing record never fails the lock.
    /// </summary>
    /// <param name="since">When the lock was taken, shown to whoever waits for it.</param>
    public static string? Register(string lockPath, ProgramInfo program, string operation, DateTimeOffset since)
    {
        var directory = DirectoryOf(lockPath);
        var self = ProcessIdentity.Current;
        var path = Path.Combine(directory, $"{self.Pid}-{self.StartTime.ToUnixTimeMilliseconds()}.json");
        var record = new LockHolderRecord
        {
            Format = RecordFormat,
            Program = program.Name,
            Version = program.Version,
            Pid = self.Pid,
            StartTime = self.StartTime,
            Operation = operation,
            Since = since,
        };

        string? temp = null;
        try
        {
            Directory.CreateDirectory(directory);
            RemoveEnded(directory);
            temp = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, record, StoreJsonContext.Default.LockHolderRecord);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            // After a successful move there is nothing left to delete. Only the file this call created is removed;
            // a failed cleanup never fails the lock either.
            if (temp is not null) DeleteQuietly(temp);
        }
    }

    public static void Unregister(string? recordPath)
    {
        if (recordPath is not null) DeleteQuietly(recordPath);
    }

    static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// A readable list of the live holders, for example <c>gebinde 0.1.0 (PID 1234), download …</c>. The list is
    /// extra information on an error that is already being thrown, so a failure to read it never escapes: the text
    /// then says "an unknown holder" and names the exception type and message that stopped the listing.
    /// </summary>
    /// <param name="listRecords">Lists the record files in a holders directory; replaced only by tests.</param>
    public static string Describe(string lockPath, Func<string, IEnumerable<string>>? listRecords = null)
    {
        var holders = new List<string>();
        string? failure = null;
        try
        {
            foreach (var (_, record) in ReadWithPaths(DirectoryOf(lockPath), listRecords ?? ListRecords))
            {
                if (new ProcessIdentity(record.Pid, record.StartTime).IsAlive())
                    holders.Add($"{record.Program} {record.Version} (PID {record.Pid}), {record.Operation}, since {record.Since:u}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var reason = e.Message.Split(['\r', '\n'])[0].Trim();
            failure = $"{e.GetType().Name}: {reason}";
        }

        var text = holders.Count == 0 ? "an unknown holder" : string.Join("; ", holders);
        return failure is null ? text : $"{text} (the holder records could not be fully listed: {failure})";
    }

    static void RemoveEnded(string directory)
    {
        foreach (var (path, record) in ReadWithPaths(directory, ListRecords))
        {
            if (new ProcessIdentity(record.Pid, record.StartTime).IsAlive()) continue;
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    static IEnumerable<string> ListRecords(string directory) => Directory.EnumerateFiles(directory, "*.json");

    /// <summary>
    /// Records that cannot be read are skipped: they only ever affect what is shown. Failing to list the directory
    /// is not skipped; the caller decides what to do with it.
    /// </summary>
    static IEnumerable<(string Path, LockHolderRecord Record)> ReadWithPaths(
        string directory,
        Func<string, IEnumerable<string>> listRecords)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var path in listRecords(directory))
        {
            LockHolderRecord? record;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                record = JsonSerializer.Deserialize(stream, StoreJsonContext.Default.LockHolderRecord);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }

            if (record is { Format: RecordFormat, Pid: > 0 }) yield return (path, record);
        }
    }
}
