namespace WhiteListApi.Tests;

using WhiteListApi.Core;
using Xunit;

public class PolishIdentifiersValidatorTests
{
    [Theory]
    [InlineData("5260250995")] // Ministerstwo Finansów
    [InlineData("1234563218")]
    [InlineData("5252344078")]
    [InlineData("7010497106")]
    public void IsValidNip_ReturnsTrue_ForValidNip(string nip)
    {
        Assert.True(PolishIdentifiersValidator.IsValidNip(nip));
    }

    [Theory]
    [InlineData("5260250994")] // Błędna cyfra kontrolna
    [InlineData("1234563219")] // Błędna cyfra kontrolna
    [InlineData("12345")]      // Za krótki
    [InlineData("12345678901")]// Za długi
    [InlineData("526025099A")] // Zawiera literę
    [InlineData("ABCDEFGHIJ")] // Same litery
    [InlineData("")]           // Pusty
    public void IsValidNip_ReturnsFalse_ForInvalidNip(string nip)
    {
        Assert.False(PolishIdentifiersValidator.IsValidNip(nip));
    }

    [Fact]
    public void IsValidNip_ReturnsFalse_WhenRemainderIs10()
    {
        // Przypadek gdy suma ważona % 11 == 10 (taki NIP wg polskich przepisów jest niepoprawny)
        // Dla "0200000000": 0*6 + 2*5 = 10; 10 % 11 == 10
        Assert.False(PolishIdentifiersValidator.IsValidNip("0200000000"));
    }

    [Theory]
    [InlineData("012584144")]
    [InlineData("015217423")]
    [InlineData("140232495")]
    public void IsValidRegon_ReturnsTrue_ForValid9DigitRegon(string regon)
    {
        Assert.True(PolishIdentifiersValidator.IsValidRegon(regon));
    }

    [Theory]
    [InlineData("012584145")]  // Błędna cyfra kontrolna
    [InlineData("01258414")]   // Za krótki (8 cyfr)
    [InlineData("0125841440")] // Za długi (10 cyfr)
    [InlineData("01258414A")]  // Zawiera literę
    [InlineData("")]           // Pusty
    public void IsValidRegon_ReturnsFalse_ForInvalid9DigitRegon(string regon)
    {
        Assert.False(PolishIdentifiersValidator.IsValidRegon(regon));
    }

    [Theory]
    [InlineData("01258414400010")]
    [InlineData("14023249500024")]
    public void IsValidRegon_ReturnsTrue_ForValid14DigitRegon(string regon)
    {
        Assert.True(PolishIdentifiersValidator.IsValidRegon(regon));
    }

    [Theory]
    [InlineData("01258414500010")] // Niepoprawna pierwsza część 9-cyfrowa
    [InlineData("01258414400011")] // Błędna 14. cyfra kontrolna
    [InlineData("0125841440001")]  // Za krótki (13 cyfr)
    [InlineData("012584144000101")]// Za długi (15 cyfr)
    [InlineData("0125841440001A")] // Zawiera literę
    public void IsValidRegon_ReturnsFalse_ForInvalid14DigitRegon(string regon)
    {
        Assert.False(PolishIdentifiersValidator.IsValidRegon(regon));
    }

    [Theory]
    [InlineData("61109010140000071219812874")] // Santander
    [InlineData("94102010260000140201648043")] // PKO BP
    [InlineData("52105010251000002234479901")] // ING
    public void IsValidNrb_ReturnsTrue_ForValidNrb(string nrb)
    {
        Assert.True(PolishIdentifiersValidator.IsValidNrb(nrb));
    }

    [Theory]
    [InlineData("93102010260000140201648043")] // Błędna suma kontrolna (zmieniono cyfrę)
    [InlineData("9410201026000014020164804")]  // Za krótki (25 cyfr)
    [InlineData("941020102600001402016480433")]// Za długi (27 cyfr)
    [InlineData("9410201026000014020164804A")] // Zawiera literę
    [InlineData("PL94102010260000140201648043")] // Z prefiksem PL (NRB ma dokładnie 26 cyfr)
    [InlineData("")]                            // Pusty
    public void IsValidNrb_ReturnsFalse_ForInvalidNrb(string nrb)
    {
        Assert.False(PolishIdentifiersValidator.IsValidNrb(nrb));
    }
}
