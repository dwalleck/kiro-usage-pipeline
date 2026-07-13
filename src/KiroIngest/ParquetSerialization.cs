using Parquet;
using Parquet.Serialization;

namespace KiroIngest;

// Serializes fact DTOs to a Snappy-compressed Parquet stream. The returned
// stream is positioned at zero and owned by the caller.
public static class ParquetSerialization
{
    public static async Task<MemoryStream> SerializeAsync<T>(IReadOnlyList<T> records)
    {
        var buffer = new MemoryStream();
        try
        {
            await ParquetSerializer.SerializeAsync(records, buffer, new ParquetOptions
            {
                CompressionMethod = CompressionMethod.Snappy,
            });
            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }
}
