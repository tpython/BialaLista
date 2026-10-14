namespace WhiteListApi.Storage;

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using WhiteListApi.Core;

public sealed class VirtualAccountMaskMatcher
{
    private readonly FrozenDictionary<uint, string[]> _masksByBankPrefix;

    public VirtualAccountMaskMatcher(IEnumerable<string> rawMasks)
    {
        var grouped = new Dictionary<uint, List<string>>();
        foreach (var mask in rawMasks)
        {
            if (mask.Length != 26) continue;
            uint bankKey = GetBankPrefixKey(mask.AsSpan());
            if (!grouped.TryGetValue(bankKey, out var list))
            {
                list = [];
                grouped[bankKey] = list;
            }
            list.Add(mask);
        }
        _masksByBankPrefix = grouped.ToFrozenDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());
    }

    public int TotalMasksCount => _masksByBankPrefix.Values.Sum(v => v.Length);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryMatchAnyMask(
        ReadOnlySpan<char> dateYyyyMmDd,
        ReadOnlySpan<char> identifier,
        ReadOnlySpan<char> bankAccount,
        WhiteListBinaryStore store)
    {
        uint bankKey = GetBankPrefixKey(bankAccount);
        if (!_masksByBankPrefix.TryGetValue(bankKey, out string[]? candidateMasks)) return false;

        Span<char> maskedAccount = stackalloc char[26];
        foreach (string mask in candidateMasks)
        {
            if (TryApplyMask(bankAccount, mask.AsSpan(), maskedAccount))
            {
                UInt128 hash = WhiteListHasher.ComputeTruncatedHash(dateYyyyMmDd, identifier, maskedAccount, store.Iterations);
                if (store.ContainsHash(hash)) return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryApplyMask(ReadOnlySpan<char> account, ReadOnlySpan<char> mask, Span<char> outputMaskedAccount)
    {
        if (account.Length != 26 || mask.Length != 26 || outputMaskedAccount.Length < 26)
            return false;

        for (int i = 0; i < 26; i++)
        {
            char m = mask[i], a = account[i];
            if (m is 'X' or 'x') outputMaskedAccount[i] = 'X';
            else if (m is 'Y' or 'y') outputMaskedAccount[i] = a;
            else
            {
                if (m != a) return false;
                outputMaskedAccount[i] = a;
            }
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetBankPrefixKey(ReadOnlySpan<char> accountOrMask)
    {
        if (accountOrMask.Length < 6) return 0;
        return ((uint)accountOrMask[2] << 24) | ((uint)accountOrMask[3] << 16) | ((uint)accountOrMask[4] << 8) | (uint)accountOrMask[5];
    }
}
