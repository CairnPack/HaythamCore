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
    public static string? Register(string lockPath, ProgramInfo program, string operation)
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
            Since = DateTimeOffset.UtcNow,
        };

        try
        {
            Directory.CreateDirectory(directory);
            RemoveEnded(directory);
            var temp = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
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
    }

    public static void Unregister(string? recordPath)
    {
        if (recordPath is null) return;
        try
        {
            File.Delete(recordPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A readable list of the live holders, for example <c>gebinde 0.1.0 (PID 1234), download …</c>.</summary>
    public static string Describe(string lockPath)
    {
        var holders = Read(DirectoryOf(lockPath))
            .Where(r => new ProcessIdentity(r.Pid, r.StartTime).IsAlive())
            .Select(r => $"{r.Program} {r.Version} (PID {r.Pid}), {r.Operation}, since {r.Since:u}")
            .ToList();
        return holders.Count == 0 ? "an unknown holder" : string.Join("; ", holders);
    }

    static void RemoveEnded(string directory)
    {
        foreach (var (path, record) in ReadWithPaths(directory))
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

    static IEnumerable<LockHolderRecord> Read(string directory) => ReadWithPaths(directory).Select(p => p.Record);

    /// <summary>Records that cannot be read are skipped: they only ever affect what is shown.</summary>
    static IEnumerable<(string Path, LockHolderRecord Record)> ReadWithPaths(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
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
