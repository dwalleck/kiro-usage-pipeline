using Parquet;
using Parquet.Serialization;

namespace KiroIngest;

// Serializes fact DTOs to a Snappy-compressed Parquet Stream. A single
// SerializeAsync call writes one row group (ticket 02 gotcha #5), and the DTO
// property names become the column names. No DateTime columns are written, so the
// INT96 date gotcha (#1) never applies. The caller owns and must dispose the
// returned MemoryStream.
public static class ParquetSerialization
{
    public static async Task<MemoryStream> SerializeAsync<T>(IReadOnlyList<T> records)
        where T : class, new()
    {
        var buffer = new MemoryStream();
        await ParquetSerializer.SerializeAsync(records, buffer, new ParquetOptions
        {
            CompressionMethod = CompressionMethod.Snappy,
        });
        buffer.Position = 0;
        return buffer;
    }
}
