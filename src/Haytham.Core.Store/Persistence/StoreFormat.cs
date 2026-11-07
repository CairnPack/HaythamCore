using System.Text.Json;

namespace Haytham.Core.Store;

/// <summary>
/// Reads and creates <c>format.json</c>: the store format version, the lowest program format that may write to the
/// store, and the store identity. The file is written once, when the store is created, and never rewritten.
/// </summary>
static class StoreFormat
{
    /// <summary>The store format this program reads and writes.</summary>
    public const int Supported = 1;

    public static StoreFormatFile Read(StoreLayout layout)
    {
        StoreFormatFile? file;
        try
        {
            using var stream = new FileStream(layout.FormatFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            file = JsonSerializer.Deserialize(stream, StoreJsonContext.Default.StoreFormatFile);
        }
        catch (JsonException e)
        {
            throw Unreadable(layout, "it is not valid JSON", e);
        }

        if (file is null) throw Unreadable(layout, "it is empty");
        if (file.Format < 1) throw Unreadable(layout, "'format' is missing or below 1");
        if (file.MinWriterFormat < 1) throw Unreadable(layout, "'minWriterFormat' is missing or below 1");
        if (file.StoreId == Guid.Empty) throw Unreadable(layout, "'storeId' is missing");
        return file;
    }

    /// <summary>
    /// Creates <c>format.json</c> without overwriting. When another process creates the store at the same time,
    /// its file wins and is returned, so both see the same store identity.
    /// </summary>
    public static StoreFormatFile Create(StoreLayout layout)
    {
        var file = new StoreFormatFile { Format = Supported, MinWriterFormat = Supported, StoreId = Guid.NewGuid() };
        var temp = layout.NewFormatTempPath();
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, StoreJsonContext.Default.StoreFormatFile);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, layout.FormatFile, overwrite: false);
                return file;
            }
            catch (IOException) when (File.Exists(layout.FormatFile))
            {
                return Read(layout);
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    static StoreException Unreadable(StoreLayout layout, string reason, Exception? inner = null) =>
        new(StoreError.FormatUnreadable, $"Cannot read '{layout.FormatFile}': {reason}.", inner);
}
