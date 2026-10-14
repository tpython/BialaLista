namespace WhiteListApi.Tests;

using System.Text;
using WhiteListApi.Storage;
using Xunit;

public class FlatFileStreamParserTests
{
    [Fact]
    public async Task ParseAndConvertAsync_ParsesHeaderMasksAndHashes()
    {
        string json = """
        {
          "naglowek": {
            "dataGenerowaniaDanych": "2026-10-14",
            "liczbaTransformacji": 5000
          },
          "skrotyPodatnikowCzynnych": [
            "0123456789abcdef0123456789abcdef000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            "fedcba9876543210fedcba987654321000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
          ],
          "skrotyPodatnikowZwolnionych": [],
          "maski": [
            "021020YYYYYYYYYYXXXXXXXXXX"
          ]
        }
        """;

        string tempBin = Path.GetTempFileName();
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var metadata = await FlatFileStreamParser.ParseAndConvertAsync(stream, tempBin, CancellationToken.None);

            Assert.Equal(5000, metadata.Iterations);
            Assert.Equal("2026-10-14", metadata.GenerationDate);
            Assert.Single(metadata.Masks);
            Assert.Equal("021020YYYYYYYYYYXXXXXXXXXX", metadata.Masks[0]);
            Assert.Equal(2, metadata.TotalHashesCount);

            // Sprawdzamy czy plik binarny ma dokładnie 32 bajty (2 hashe po 16 bajtów)
            var fileInfo = new FileInfo(tempBin);
            Assert.Equal(32, fileInfo.Length);

            using var store = new WhiteListBinaryStore(tempBin, metadata.Iterations);
            Assert.Equal(2, store.ElementsCount);

            UInt128 hash1 = FlatFileStreamParser.ParseHex("0123456789abcdef0123456789abcdef"u8);
            UInt128 hash2 = FlatFileStreamParser.ParseHex("fedcba9876543210fedcba9876543210"u8);

            Assert.True(store.ContainsHash(hash1));
            Assert.True(store.ContainsHash(hash2));
            Assert.False(store.ContainsHash(hash1 + 1));
        }
        finally
        {
            if (File.Exists(tempBin)) File.Delete(tempBin);
        }
    }
}
