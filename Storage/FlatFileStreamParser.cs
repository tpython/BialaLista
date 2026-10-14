namespace WhiteListApi.Storage;

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;

public sealed class ParsedFlatFileMetadata
{
    public int Iterations { get; set; } = 5000;
    public string GenerationDate { get; set; } = string.Empty;
    public List<string> Masks { get; } = [];
    public int TotalHashesCount { get; set; }
}

public static class FlatFileStreamParser
{
    public static async Task<ParsedFlatFileMetadata> ParseAndConvertAsync(Stream jsonStream, string outputBinaryPath, CancellationToken ct)
    {
        var metadata = new ParsedFlatFileMetadata();
        var hashes = new List<UInt128>(capacity: 6_500_000);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int bytesInBuffer = 0;
        var state = new JsonReaderState(new JsonReaderOptions { AllowTrailingCommas = true });
        string? currentProperty = null;
        int section = 0;

        try
        {
            while (true)
            {
                int bytesRead = await jsonStream.ReadAsync(buffer.AsMemory(bytesInBuffer, buffer.Length - bytesInBuffer), ct);
                bytesInBuffer += bytesRead;
                bool isFinalBlock = bytesRead == 0;

                var reader = new Utf8JsonReader(buffer.AsSpan(0, bytesInBuffer), isFinalBlock, state);
                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.PropertyName:
                            if (reader.ValueTextEquals("naglowek"u8)) section = 1;
                            else if (reader.ValueTextEquals("skrotyPodatnikowCzynnych"u8) || reader.ValueTextEquals("skrotyPodatnikowZwolnionych"u8)) section = 2;
                            else if (reader.ValueTextEquals("maski"u8)) section = 3;
                            else if (section == 1) currentProperty = reader.GetString();
                            break;
                        case JsonTokenType.String:
                            if (section == 2)
                            {
                                ReadOnlySpan<byte> utf8Hex = reader.ValueSpan;
                                if (utf8Hex.Length >= 32) hashes.Add(ParseHex(utf8Hex[..32]));
                            }
                            else if (section == 3) metadata.Masks.Add(reader.GetString()!);
                            else if (section == 1 && currentProperty == "liczbaTransformacji") metadata.Iterations = int.Parse(reader.ValueSpan);
                            else if (section == 1 && currentProperty == "dataGenerowaniaDanych") metadata.GenerationDate = reader.GetString() ?? string.Empty;
                            break;
                        case JsonTokenType.Number when section == 1 && currentProperty == "liczbaTransformacji":
                            metadata.Iterations = reader.GetInt32();
                            break;
                        case JsonTokenType.EndObject when section == 1:
                            section = 0;
                            currentProperty = null;
                            break;
                        case JsonTokenType.EndArray when section is 2 or 3:
                            section = 0;
                            break;
                    }
                }
                state = reader.CurrentState;
                if (isFinalBlock) break;

                int consumed = (int)reader.BytesConsumed;
                int leftover = bytesInBuffer - consumed;
                if (leftover > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, leftover);
                bytesInBuffer = leftover;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        Span<UInt128> span = CollectionsMarshal.AsSpan(hashes);
        span.Sort();
        using (var fs = new FileStream(outputBinaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
        {
            fs.Write(MemoryMarshal.AsBytes(span));
        }
        metadata.TotalHashesCount = hashes.Count;
        return metadata;
    }

    public static UInt128 ParseHex(ReadOnlySpan<byte> hex32)
    {
        Span<byte> b16 = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            int hi = hex32[i * 2] <= '9' ? hex32[i * 2] - '0' : (hex32[i * 2] | 0x20) - 'a' + 10;
            int lo = hex32[(i * 2) + 1] <= '9' ? hex32[(i * 2) + 1] - '0' : (hex32[(i * 2) + 1] | 0x20) - 'a' + 10;
            b16[i] = (byte)((hi << 4) | lo);
        }
        return BinaryPrimitives.ReadUInt128BigEndian(b16);
    }
}
