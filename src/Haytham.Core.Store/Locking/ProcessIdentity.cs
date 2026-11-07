using System.Diagnostics;

namespace Haytham.Core.Store;

/// <summary>A process as lock holder records name it: the PID plus its start time, since PIDs get reused.</summary>
readonly record struct ProcessIdentity(int Pid, DateTimeOffset StartTime)
{
    // Start times read back from the system can differ slightly from the recorded ones.
    static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public static ProcessIdentity Current { get; } = ReadCurrent();

    /// <summary>A process that is gone counts as ended; one the system will not tell about counts as alive.</summary>
    public bool IsAlive()
    {
        try
        {
            using var process = Process.GetProcessById(Pid);
            var actual = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return (actual - StartTime).Duration() <= StartTimeTolerance;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // The process exited between the lookup and reading its start time.
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    static ProcessIdentity ReadCurrent()
    {
        using var process = Process.GetCurrentProcess();
        return new ProcessIdentity(
            Environment.ProcessId,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
    }
}
