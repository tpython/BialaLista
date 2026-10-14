namespace WhiteListApi.Tests;

using System.Runtime.InteropServices;
using WhiteListApi.Storage;
using Xunit;

public class WhiteListBinaryStoreTests
{
    [Fact]
    public void ContainsHash_FindsExistingHashes_InSortedBinaryFile()
    {
        int count = 500;
        UInt128[] sortedHashes = new UInt128[count];
        // Tworzymy rosnące wartości UInt128 z odstępami (krok 10)
        for (int i = 0; i < count; i++)
        {
            sortedHashes[i] = ((UInt128)i + 1) * 10;
        }

        string tempPath = Path.GetTempFileName();
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                fs.Write(MemoryMarshal.AsBytes(sortedHashes.AsSpan()));
            }

            using var store = new WhiteListBinaryStore(tempPath, iterations: 5000);
            Assert.Equal(count, store.ElementsCount);

            // Pierwszy element
            Assert.True(store.ContainsHash(sortedHashes[0]));
            // Ostatni element
            Assert.True(store.ContainsHash(sortedHashes[^1]));
            // Środkowe elementy
            Assert.True(store.ContainsHash(sortedHashes[100]));
            Assert.True(store.ContainsHash(sortedHashes[250]));

            // Wartość mniejsza niż minimum (5 < 10)
            Assert.False(store.ContainsHash((UInt128)5));

            // Wartość większa niż maksimum
            Assert.False(store.ContainsHash(sortedHashes[^1] + 10));

            // Wartość pomiędzy istniejącymi elementami (15 leży między 10 a 20)
            Assert.False(store.ContainsHash((UInt128)15));
            Assert.False(store.ContainsHash((UInt128)25));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void ContainsHash_WorksWithSingleElementFile()
    {
        UInt128 single = new UInt128(0x0123456789ABCDEF, 0xFEDCBA9876543210);
        string tempPath = Path.GetTempFileName();
        try
        {
            byte[] bytes = new byte[16];
            MemoryMarshal.Write(bytes, in single);
            File.WriteAllBytes(tempPath, bytes);

            using var store = new WhiteListBinaryStore(tempPath, iterations: 5000);
            Assert.Equal(1, store.ElementsCount);
            Assert.True(store.ContainsHash(single));
            Assert.False(store.ContainsHash(single + 1));
            Assert.False(store.ContainsHash(single - 1));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void ContainsHash_WorksWithEmptyFile()
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempPath, []);

            using var store = new WhiteListBinaryStore(tempPath, iterations: 5000);
            Assert.Equal(0, store.ElementsCount);
            Assert.False(store.ContainsHash((UInt128)12345));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
