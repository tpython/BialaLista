namespace WhiteListApi.Core;

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

public static class WhiteListHasher
{
    private static ReadOnlySpan<byte> HexAlphabet => "0123456789abcdef"u8;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static UInt128 ComputeTruncatedHash(
        ReadOnlySpan<char> dateYyyyMmDd,
        ReadOnlySpan<char> identifier,
        ReadOnlySpan<char> accountOrMask,
        int iterations = 5000)
    {
        Span<byte> initialInput = stackalloc byte[128];
        int pos = Encoding.ASCII.GetBytes(dateYyyyMmDd, initialInput);
        pos += Encoding.ASCII.GetBytes(identifier, initialInput[pos..]);
        pos += Encoding.ASCII.GetBytes(accountOrMask, initialInput[pos..]);

        Span<byte> hashBytes = stackalloc byte[64];
        Span<byte> hexBuffer = stackalloc byte[128];

        SHA512.HashData(initialInput[..pos], hashBytes);

        for (int i = 1; i < iterations; i++)
        {
            BytesToLowerHex(hashBytes, hexBuffer);
            SHA512.HashData(hexBuffer, hashBytes);
        }

        return BinaryPrimitives.ReadUInt128BigEndian(hashBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void BytesToLowerHex(ReadOnlySpan<byte> source64, Span<byte> dest128)
    {
        ReadOnlySpan<byte> hex = HexAlphabet;
        for (int i = 0; i < 64; i++)
        {
            byte b = source64[i];
            dest128[i * 2] = hex[b >> 4];
            dest128[(i * 2) + 1] = hex[b & 0x0F];
        }
    }
}
