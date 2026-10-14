namespace WhiteListApi.Core;

using System.Runtime.CompilerServices;

public static class PolishIdentifiersValidator
{
    private static ReadOnlySpan<byte> NipWeights => [6, 5, 7, 2, 3, 4, 5, 6, 7];
    private static ReadOnlySpan<byte> Regon9Weights => [8, 9, 2, 3, 4, 5, 6, 7];
    private static ReadOnlySpan<byte> Regon14Weights => [2, 4, 8, 5, 0, 9, 7, 3, 6, 1, 2, 4, 8];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidNip(ReadOnlySpan<char> nip)
    {
        if (nip.Length != 10) return false;
        int sum = 0;
        for (int i = 0; i < 9; i++)
        {
            int digit = nip[i] - '0';
            if ((uint)digit > 9) return false;
            sum += digit * NipWeights[i];
        }
        int controlDigit = nip[9] - '0';
        if ((uint)controlDigit > 9) return false;
        int remainder = sum % 11;
        return remainder != 10 && remainder == controlDigit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidRegon(ReadOnlySpan<char> regon)
    {
        if (regon.Length == 9)
            return ValidateRegonWeights(regon, Regon9Weights);
        if (regon.Length == 14)
            return ValidateRegonWeights(regon[..9], Regon9Weights) && ValidateRegonWeights(regon, Regon14Weights);
        return false;
    }

    private static bool ValidateRegonWeights(ReadOnlySpan<char> input, ReadOnlySpan<byte> weights)
    {
        int sum = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            int digit = input[i] - '0';
            if ((uint)digit > 9) return false;
            sum += digit * weights[i];
        }
        int control = (sum % 11) % 10;
        int controlDigit = input[weights.Length] - '0';
        if ((uint)controlDigit > 9) return false;
        return controlDigit == control;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidNrb(ReadOnlySpan<char> nrb)
    {
        if (nrb.Length != 26) return false;
        int remainder = 0;
        for (int i = 2; i < 26; i++)
        {
            int digit = nrb[i] - '0';
            if ((uint)digit > 9) return false;
            remainder = (remainder * 10 + digit) % 97;
        }
        remainder = (remainder * 10000 + 2521) % 97; // PL = 2521
        int d0 = nrb[0] - '0', d1 = nrb[1] - '0';
        if ((uint)d0 > 9 || (uint)d1 > 9) return false;
        remainder = (remainder * 100 + d0 * 10 + d1) % 97;
        return remainder == 1;
    }
}
