namespace Haytham.Core.Store.Tests;

/// <summary>A fresh directory per test. Store objects are read-only, so cleanup clears the flag before deleting.</summary>
sealed class TempStoreDirectory : IDisposable
{
    public TempStoreDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "haytham-store-tests", Guid.NewGuid().ToString("N"));
    }

    public string Path { get; }

    public ContentStore Create() => ContentStore.Open(Path, StoreOpenMode.CreateIfMissing);

    public string[] TempEntries() =>
        Directory.GetFileSystemEntries(System.IO.Path.Combine(Path, "tmp"))
            .Select(System.IO.Path.GetFileName)
            .ToArray()!;

    /// <summary>Overwrites an object the way a mod writing through a hard link would. Tests only.</summary>
    public static void Tamper(string objectPath, byte[] content)
    {
        File.SetAttributes(objectPath, FileAttributes.Normal);
        File.WriteAllBytes(objectPath, content);
        File.SetAttributes(objectPath, FileAttributes.ReadOnly);
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, recursive: true);
    }
}
