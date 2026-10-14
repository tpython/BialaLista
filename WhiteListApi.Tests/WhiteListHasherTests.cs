namespace WhiteListApi.Tests;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WhiteListApi.Core;
using Xunit;

public class WhiteListHasherTests
{
    /// <summary>
    /// Oficjalna referencyjna implementacja algorytmu MF:
    /// 1. Pierwsza iteracja: SHA512(Data + Identyfikator + Rachunek/Maska).
    /// 2. Kolejne iteracje: SHA512(tekst szesnastkowy z małych liter poprzedniego hasha).
    /// 3. Obcięcie do pierwszych 16 bajtów (32 znaki hex) i odczyt Big-Endian UInt128.
    /// </summary>
    private static UInt128 ReferenceMfHash(string date, string id, string account, int iterations)
    {
        using var sha = SHA512.Create();
        string input = $"{date}{id}{account}";
        byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(input));

        for (int i = 1; i < iterations; i++)
        {
            string hexLower = Convert.ToHexString(hash).ToLowerInvariant();
            hash = sha.ComputeHash(Encoding.ASCII.GetBytes(hexLower));
        }

        return BinaryPrimitives.ReadUInt128BigEndian(hash);
    }

    [Fact]
    public void ComputeTruncatedHash_MatchesOfficialAlgorithm_ForSingleIteration()
    {
        string date = "20261014";
        string nip = "5260250995";
        string account = "02102010260000140201648043";

        UInt128 expected = ReferenceMfHash(date, nip, account, 1);
        UInt128 actual = WhiteListHasher.ComputeTruncatedHash(date, nip, account, 1);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeTruncatedHash_MatchesOfficialAlgorithm_ForSmallIterations()
    {
        string date = "20261014";
        string nip = "1234563218";
        string account = "61109010140000071219812874";

        for (int it = 2; it <= 5; it++)
        {
            UInt128 expected = ReferenceMfHash(date, nip, account, it);
            UInt128 actual = WhiteListHasher.ComputeTruncatedHash(date, nip, account, it);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ComputeTruncatedHash_MatchesOfficialAlgorithm_For5000Iterations()
    {
        // Oficjalna liczba transformacji Ministerstwa Finansów (5000)
        string date = "20260101";
        string nip = "7010497102";
        string account = "02102010260000140201648043";

        UInt128 expected = ReferenceMfHash(date, nip, account, 5000);
        UInt128 actual = WhiteListHasher.ComputeTruncatedHash(date, nip, account, 5000);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeTruncatedHash_MatchesHexPrefix()
    {
        string date = "20260505";
        string regon = "012584144";
        string account = "02102010260000140201648043";

        using var sha = SHA512.Create();
        string input = $"{date}{regon}{account}";
        byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(input));
        for (int i = 1; i < 10; i++)
        {
            string hexLower = Convert.ToHexString(hash).ToLowerInvariant();
            hash = sha.ComputeHash(Encoding.ASCII.GetBytes(hexLower));
        }

        string fullHex = Convert.ToHexString(hash).ToLowerInvariant();
        string expectedHex32Prefix = fullHex[..32];

        UInt128 actual = WhiteListHasher.ComputeTruncatedHash(date, regon, account, 10);
        string actualHex32 = actual.ToString("x32");

        Assert.Equal(expectedHex32Prefix, actualHex32);
    }

    [Fact]
    public void BytesToLowerHex_FormatsCorrectly()
    {
        byte[] input64 = new byte[64];
        for (int i = 0; i < 64; i++) input64[i] = (byte)i;

        Span<byte> output128 = stackalloc byte[128];
        WhiteListHasher.BytesToLowerHex(input64, output128);

        string expected = Convert.ToHexString(input64).ToLowerInvariant();
        string actual = Encoding.ASCII.GetString(output128);

        Assert.Equal(expected, actual);
    }
}
