namespace Haytham.Core.Store;

/// <summary>Who is using the store, as shown to users when a lock is held: for example <c>gebinde 0.1.0</c>.</summary>
public sealed record ProgramInfo(string Name, string Version)
{
    public static ProgramInfo Unknown { get; } =
        new(Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "unknown", "unknown");
}
