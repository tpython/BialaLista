namespace WhiteListApi.Tests;

using System.Runtime.InteropServices;
using WhiteListApi.Core;
using WhiteListApi.Storage;
using Xunit;

public class VirtualAccountMaskMatcherTests
{
    [Fact]
    public void TryApplyMask_AppliesXAndYCorrectly()
    {
        // 26-znakowy rachunek
        string account = "94102010260000140201648043";
        // Maska: pierwsze 6 znaków stałe, potem 10 znaków Y (kopiowane z konta), potem 10 znaków X
        string mask = "941020YYYYYYYYYYXXXXXXXXXX";

        Span<char> output = stackalloc char[26];
        bool success = VirtualAccountMaskMatcher.TryApplyMask(account.AsSpan(), mask.AsSpan(), output);

        Assert.True(success);
        string result = new string(output);

        // Pierwsze 6 znaków: zgodne z maską i kontem (941020)
        Assert.Equal("941020", result[..6]);
        // Kolejne 10 znaków (indeksy 6..15): skopiowane z account (1026000014)
        Assert.Equal("1026000014", result.Substring(6, 10));
        // Ostatnie 10 znaków (indeksy 16..25): zamaskowane literami X
        Assert.Equal("XXXXXXXXXX", result.Substring(16, 10));
    }

    [Fact]
    public void TryApplyMask_ReturnsFalse_WhenFixedDigitsDoNotMatch()
    {
        string account = "94102010260000140201648043";
        // Maska dla innego banku (1050 zamiast 1020)
        string mask = "941050YYYYYYYYYYXXXXXXXXXX";

        Span<char> output = stackalloc char[26];
        bool success = VirtualAccountMaskMatcher.TryApplyMask(account.AsSpan(), mask.AsSpan(), output);

        Assert.False(success);
    }

    [Fact]
    public void TryApplyMask_ReturnsFalse_WhenLengthsAreInvalid()
    {
        Span<char> output = stackalloc char[26];
        Assert.False(VirtualAccountMaskMatcher.TryApplyMask("123", "941020YYYYYYYYYYXXXXXXXXXX", output));
        Assert.False(VirtualAccountMaskMatcher.TryApplyMask("94102010260000140201648043", "123", output));
    }

    [Fact]
    public void GetBankPrefixKey_ExtractsBankCodeCorrectly()
    {
        // W NRB pozycje 2..5 (indeksy 2, 3, 4, 5) to 4-cyfrowy identyfikator banku
        string account = "94102010260000140201648043";
        uint key = VirtualAccountMaskMatcher.GetBankPrefixKey(account.AsSpan());

        uint expected = ((uint)'1' << 24) | ((uint)'0' << 16) | ((uint)'2' << 8) | (uint)'0';
        Assert.Equal(expected, key);
    }

    [Fact]
    public void TryMatchAnyMask_MatchesVirtualAccountAgainstBinaryStore()
    {
        string date = "20261014";
        string nip = "5260250995";
        string realAccount = "94102010260000140201648043";
        string mask = "9410201026YYYYYYXXXXXXXXXX";

        // Obliczamy zamaskowane konto:
        Span<char> maskedAccount = stackalloc char[26];
        VirtualAccountMaskMatcher.TryApplyMask(realAccount.AsSpan(), mask.AsSpan(), maskedAccount);
        Assert.Equal("9410201026000014XXXXXXXXXX", new string(maskedAccount));

        // Obliczamy oczekiwany hash dla konta z maską (tak jak jest zapisany w pliku płaskim MF)
        UInt128 expectedHash = WhiteListHasher.ComputeTruncatedHash(date, nip, maskedAccount, iterations: 10);

        // Tworzymy tymczasowy plik binarny ze skrótem
        string tempBin = Path.GetTempFileName();
        try
        {
            byte[] bytes = new byte[16];
            MemoryMarshal.Write(bytes, in expectedHash);
            File.WriteAllBytes(tempBin, bytes);

            using var store = new WhiteListBinaryStore(tempBin, iterations: 10);
            var matcher = new VirtualAccountMaskMatcher([mask]);

            // 1. Prawdziwe konto wirtualne powinno zostać dopasowane przez maskę
            bool matched = matcher.TryMatchAnyMask(date, nip, realAccount, store);
            Assert.True(matched);

            // 2. Inny NIP nie powinien zostać dopasowany
            bool matchedOtherNip = matcher.TryMatchAnyMask(date, "1111111111", realAccount, store);
            Assert.False(matchedOtherNip);

            // 3. Inne konto (np. z innym kodem banku) nie powinno zostać dopasowane
            string otherAccount = "61109010140000071219812874";
            bool matchedOtherAccount = matcher.TryMatchAnyMask(date, nip, otherAccount, store);
            Assert.False(matchedOtherAccount);
        }
        finally
        {
            if (File.Exists(tempBin)) File.Delete(tempBin);
        }
    }
}
