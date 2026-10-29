namespace Haytham.Core.Store;

public enum StoreOpenMode
{
    /// <summary>Fail with <see cref="StoreError.StoreMissing"/> when there is no store at the path.</summary>
    OpenExisting,

    /// <summary>Create the store when the directory is missing or empty.</summary>
    CreateIfMissing,
}

/// <summary>
/// The content-addressed store: one per machine, keyed by SHA-1. Haytham Core has no default location; the caller
/// passes the root.
/// </summary>
public sealed class ContentStore
{
    readonly string? _readOnlyReason;

    internal ContentStore(StoreLayout layout, StoreFormatFile format, ProgramInfo program, TimeProvider timeProvider)
    {
        Layout = layout;
        Format = format.Format;
        StoreId = format.StoreId;
        Program = program;
        _readOnlyReason = ReadOnlyReason(format);
        var records = new VerificationRecords();
        Verifier = new ObjectVerifier(layout, records);
        Importer = new ObjectImporter(layout, Verifier, records, program, timeProvider);
    }

    public string Root => Layout.Root;

    /// <summary>The store's identity; it never changes once the store exists.</summary>
    public Guid StoreId { get; }

    public int Format { get; }

    /// <summary>
    /// True when the store requires a newer writer, or file
    /// locking is switched off in this process so locks would silently do nothing.
    /// </summary>
    public bool IsReadOnly => _readOnlyReason is not null;

    internal StoreLayout Layout { get; }

    internal ProgramInfo Program { get; }

    internal ObjectVerifier Verifier { get; }

    internal ObjectImporter Importer { get; }

    /// <param name="root">The store directory. Haytham Core has no default; the caller decides.</param>
    /// <param name="mode">Whether a missing store may be created.</param>
    /// <param name="program">Shown to other programs while this one holds a lock; defaults to the process name.</param>
    public static ContentStore Open(
        string root,
        StoreOpenMode mode = StoreOpenMode.OpenExisting,
        ProgramInfo? program = null)
    {
        var layout = new StoreLayout(Path.GetFullPath(root));
        StoreFormatFile format;
        if (File.Exists(layout.FormatFile))
        {
            format = StoreFormat.Read(layout);
        }
        else
        {
            if (mode != StoreOpenMode.CreateIfMissing)
                throw new StoreException(StoreError.StoreMissing, $"There is no store at '{layout.Root}'.");
            format = CreateOrJoin(layout);
        }

        var store = new ContentStore(layout, format, program ?? ProgramInfo.Unknown, TimeProvider.System);
        if (!store.IsReadOnly)
        {
            Directory.CreateDirectory(layout.ObjectsDirectory);
            Directory.CreateDirectory(layout.TempDirectory);
        }

        return store;
    }

    /// <summary>Path of an object; it may not exist. Use <see cref="VerifyAsync"/> to know whether it is intact.</summary>
    public string GetObjectPath(Sha1 sha1) => Layout.ObjectPath(sha1);

    public Task<ObjectState> VerifyAsync(Sha1 sha1, VerifyMode mode, CancellationToken cancellationToken = default) =>
        Verifier.VerifyAsync(sha1, mode, cancellationToken);

    public Task<ImportResult> ImportAsync(
        Stream content,
        ExpectedContent? expected = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        return Importer.ImportAsync(content, expected, cancellationToken);
    }

    /// <summary>Copies a file into the store; the original is left untouched and never shared with the store.</summary>
    public async Task<ImportResult> ImportFileAsync(
        string path,
        ExpectedContent? expected = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 0,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var _ = stream.ConfigureAwait(false);
        return await Importer.ImportAsync(stream, expected, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportResult> ImportBytesAsync(
        byte[] content,
        ExpectedContent? expected = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        using var stream = new MemoryStream(content, writable: false);
        return await Importer.ImportAsync(stream, expected, cancellationToken).ConfigureAwait(false);
    }

    internal void ThrowIfReadOnly()
    {
        if (_readOnlyReason is not null)
            throw new StoreException(StoreError.ReadOnly, $"The store at '{Root}' is read-only here: {_readOnlyReason}.");
    }

    static string? ReadOnlyReason(StoreFormatFile format)
    {
        if (format.MinWriterFormat > StoreFormat.Supported)
            return $"it was written by a newer program (format {format.Format})";
        if (!OperatingSystem.IsWindows() && IsFileLockingDisabled())
            return "file locking is switched off (System.IO.DisableFileLocking), so its locks would not work";
        return null;
    }

    // On POSIX the share modes behind every store lock are flock calls that this switch turns off without an error.
    // Windows enforces share modes in the system, so the switch does not affect it.
    static bool IsFileLockingDisabled()
    {
        if (AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var disabled)) return disabled;
        var variable = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        return variable is "1" || string.Equals(variable, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs when <c>format.json</c> was missing a moment ago. Another program may have created the store since, so a
    /// non-empty directory is checked for <c>format.json</c> once more before it is refused.
    /// </summary>
    internal static StoreFormatFile CreateOrJoin(StoreLayout layout)
    {
        if (HasOtherEntries(layout.Root))
        {
            if (File.Exists(layout.FormatFile)) return StoreFormat.Read(layout);
            throw new StoreException(
                StoreError.NotAStore,
                $"'{layout.Root}' is not empty and has no format.json, so it is not taken over as a store.");
        }

        Directory.CreateDirectory(layout.Root);
        return StoreFormat.Create(layout);
    }

    /// <summary>
    /// True when the directory holds anything besides the temporary file of a store being created right now: only a
    /// regular file named exactly like the ones <see cref="StoreLayout.NewFormatTempPath"/> makes is ignored.
    /// </summary>
    static bool HasOtherEntries(string root)
    {
        var directory = new DirectoryInfo(root);
        return directory.Exists
            && directory.EnumerateFileSystemInfos()
                .Any(entry => entry is not FileInfo || !StoreLayout.IsFormatTempName(entry.Name));
    }
}
