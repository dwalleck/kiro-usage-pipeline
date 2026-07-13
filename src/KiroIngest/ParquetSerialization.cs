using Parquet;
using Parquet.Serialization;

namespace KiroIngest;

// Serializes fact DTOs to a Snappy-compressed Parquet byte[]. A single
// SerializeAsync call writes one row group (ticket 02 gotcha #5), and the DTO
// property names become the column names. No DateTime columns are written, so the
// INT96 date gotcha (#1) never applies.
public static class ParquetSerialization
{
    public static async Task<byte[]> SerializeAsync<T>(IReadOnlyList<T> records)
        where T : class, new()
    {
        using var buffer = new MemoryStream();
        await ParquetSerializer.SerializeAsync(records, buffer, new ParquetOptions
        {
            CompressionMethod = CompressionMethod.Snappy,
        });
        return buffer.ToArray();
    }
}
