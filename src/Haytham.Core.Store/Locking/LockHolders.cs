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
    /// Records the current process as a holder and removes records of processes that have ended.
    /// Returns the record path, or throws when registration or its cleanup fails.
    /// </summary>
    /// <param name="since">When the lock was taken, shown to whoever waits for it.</param>
    public static string Register(string lockPath, ProgramInfo program, string operation, DateTimeOffset since)
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

        Directory.CreateDirectory(directory);
        RemoveEnded(directory);
        var temp = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        var ownsTemp = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemp = true;
                JsonSerializer.Serialize(stream, record, StoreJsonContext.Default.LockHolderRecord);
            }
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception registrationError)
        {
            if (ownsTemp)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Holder registration and temporary file cleanup both failed.",
                        registrationError, cleanupError);
                }
            }
            throw;
        }
    }

    public static void Unregister(string recordPath) => File.Delete(recordPath);

    /// <summary>
    /// A readable list of the live holders, for example <c>gebinde 0.1.0 (PID 1234), download …</c>.
    /// No records means an unknown holder; failures to list or read records propagate to the caller.
    /// </summary>
    /// <param name="listRecords">Lists the record files in a holders directory; replaced only by tests.</param>
    public static string Describe(string lockPath, Func<string, IEnumerable<string>>? listRecords = null)
    {
        var holders = new List<string>();
        foreach (var (_, record) in ReadWithPaths(DirectoryOf(lockPath), listRecords ?? ListRecords))
        {
            if (new ProcessIdentity(record.Pid, record.StartTime).IsAlive())
                holders.Add($"{record.Program} {record.Version} (PID {record.Pid}), {record.Operation}, since {record.Since:u}");
        }

        return holders.Count == 0 ? "an unknown holder" : string.Join("; ", holders);
    }

    static void RemoveEnded(string directory)
    {
        foreach (var (path, record) in ReadWithPaths(directory, ListRecords))
        {
            if (new ProcessIdentity(record.Pid, record.StartTime).IsAlive()) continue;
            File.Delete(path);
        }
    }

    static IEnumerable<string> ListRecords(string directory) => Directory.EnumerateFiles(directory, "*.json");

    /// <summary>
    /// Missing holder directories have no records. Listing, record parsing and read failures are otherwise errors.
    /// </summary>
    static IEnumerable<(string Path, LockHolderRecord Record)> ReadWithPaths(
        string directory,
        Func<string, IEnumerable<string>> listRecords)
    {
        string[] paths;
        try
        {
            paths = listRecords(directory).ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }
        foreach (var path in paths)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (FileNotFoundException)
            {
                // The holder can unregister after the timeout reader listed its record.
                continue;
            }
            using var opened = stream;
            var record = JsonSerializer.Deserialize(stream, StoreJsonContext.Default.LockHolderRecord);
            if (record is not { Format: RecordFormat, Pid: > 0 })
                throw new InvalidDataException($"Invalid lock holder record '{path}'.");
            yield return (path, record);
        }
    }
}
