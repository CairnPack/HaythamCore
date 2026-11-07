using System.Text.Json.Serialization;

namespace Haytham.Core.Store;

/// <summary>Contents of <c>format.json</c>.</summary>
sealed class StoreFormatFile
{
    public int Format { get; set; }

    public int MinWriterFormat { get; set; }

    public Guid StoreId { get; set; }
}

// Source-generated so that NativeAOT needs no reflection.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(StoreFormatFile))]
[JsonSerializable(typeof(LockHolderRecord))]
partial class StoreJsonContext : JsonSerializerContext;
