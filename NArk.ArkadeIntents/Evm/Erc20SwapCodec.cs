using System.Numerics;
using System.Security.Cryptography;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using Org.BouncyCastle.Crypto.Digests;

namespace NArk.ArkadeIntents.Evm;

/// <summary>The six values identifying one ERC20Swap lock.</summary>
/// <param name="PaymentHash">Lowercase SHA-256 payment hash.</param>
/// <param name="Amount">Positive ERC20 atomic amount.</param>
/// <param name="TokenAddress">ERC20 contract address.</param>
/// <param name="ClaimAddress">Token recipient.</param>
/// <param name="RefundAddress">Expired-lock recipient.</param><param name="TimeoutBlock">EVM refund height.</param>
public sealed record Erc20SwapValues(
    string PaymentHash,
    BigInteger Amount,
    string TokenAddress,
    string ClaimAddress,
    string RefundAddress,
    BigInteger TimeoutBlock);

/// <summary>Minimal ABI codec for the ERC20Swap calls used by an EVM send.</summary>
public static class Erc20SwapCodec
{
    /// <summary>Selector for <c>swaps(bytes32)</c>.</summary>
    public static ReadOnlySpan<byte> SwapsSelector => [0xeb, 0x84, 0xe7, 0xf2];

    /// <summary>Selector for <c>claim(bytes32,uint256,address,address,address,uint256)</c>.</summary>
    public static ReadOnlySpan<byte> ClaimForSelector => [0xbc, 0x58, 0x6b, 0x28];

    /// <summary>Topic zero for <c>Claim(bytes32,bytes32)</c>.</summary>
    public const string ClaimTopic = "0x5664142af3dcfc3dc3de45a43f75c746bd1d8c11170a5037fdf98bdb35775137";

    /// <summary>Topic zero for the canonical ERC20 <c>Transfer(address,address,uint256)</c> event.</summary>
    public const string TransferTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";

    /// <summary>Computes <c>keccak256(abi.encode(...))</c> over the six lock values.</summary>
    /// <param name="values">The exact lock values.</param>
    /// <returns>The 32-byte mapping key.</returns>
    public static byte[] SwapKey(Erc20SwapValues values) => Keccak(EncodeValues(values));

    /// <summary>Builds calldata for <c>swaps(key)</c>.</summary>
    /// <param name="values">The exact lock values.</param>
    /// <returns>ABI calldata.</returns>
    public static byte[] SwapsCall(Erc20SwapValues values)
    {
        var result = new byte[36];
        SwapsSelector.CopyTo(result);
        SwapKey(values).CopyTo(result, 4);
        return result;
    }

    /// <summary>Reads a canonical 32-byte ABI Boolean.</summary>
    /// <param name="result">Raw <c>eth_call</c> result.</param>
    /// <returns>The decoded Boolean.</returns>
    public static bool ReadSwapsResult(ReadOnlySpan<byte> result)
    {
        if (result.Length != 32 || result[..31].IndexOfAnyExcept((byte)0) >= 0 || result[31] > 1)
            throw new ArgumentException("expected a canonical 32-byte ABI bool", nameof(result));
        return result[31] == 1;
    }

    /// <summary>Builds calldata for the permissionless claim overload commonly called claimFor.</summary>
    /// <param name="preimage">The 32-byte preimage.</param>
    /// <param name="values">The exact lock values.</param>
    /// <returns>ABI calldata.</returns>
    public static byte[] ClaimForCall(ReadOnlySpan<byte> preimage, Erc20SwapValues values)
    {
        if (preimage.Length != 32)
            throw new ArgumentException("preimage must be exactly 32 bytes", nameof(preimage));
        var paymentHash = SHA256.HashData(preimage);
        if (!paymentHash.SequenceEqual(Hex32(values.PaymentHash)))
            throw new ArgumentException("preimage does not match the swap payment hash", nameof(preimage));

        var result = new byte[4 + 32 * 6];
        ClaimForSelector.CopyTo(result);
        preimage.CopyTo(result.AsSpan(4, 32));
        EncodeValues(values).AsSpan(32).CopyTo(result.AsSpan(36));
        return result;
    }

    internal static byte[] EncodeValues(Erc20SwapValues values)
    {
        var result = new byte[32 * 6];
        Hex32(values.PaymentHash).CopyTo(result, 0);
        WritePositiveUInt256(values.Amount, result.AsSpan(32, 32), nameof(values.Amount));
        WriteAddress(values.TokenAddress, result.AsSpan(64, 32), nameof(values.TokenAddress));
        WriteAddress(values.ClaimAddress, result.AsSpan(96, 32), nameof(values.ClaimAddress));
        WriteAddress(values.RefundAddress, result.AsSpan(128, 32), nameof(values.RefundAddress));
        WritePositiveUInt256(values.TimeoutBlock, result.AsSpan(160, 32), nameof(values.TimeoutBlock));
        return result;
    }

    internal static byte[] Hex32(string value) =>
        Convert.FromHexString(EvmWire.RequireNonZeroHex32(value, nameof(value)));

    internal static void WriteAddress(string value, Span<byte> destination, string name) =>
        Convert.FromHexString(EvmWire.RequireNonZeroAddress(value, name, lowerCase: true)[2..])
            .CopyTo(destination[12..]);

    internal static void WritePositiveUInt256(BigInteger value, Span<byte> destination, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, "value must be positive");
        WriteUInt256(value, destination, name);
    }

    internal static void WriteUInt256(BigInteger value, Span<byte> destination, string name)
    {
        if (value.Sign < 0 || !value.TryWriteBytes(destination, out var written, true, true) || written > 32)
            throw new ArgumentOutOfRangeException(name, "value must fit an unsigned 256-bit integer");
        if (written < 32)
        {
            destination[..written].CopyTo(destination[(32 - written)..]);
            destination[..(32 - written)].Clear();
        }
    }

    private static byte[] Keccak(ReadOnlySpan<byte> input)
    {
        var digest = new KeccakDigest(256);
        var bytes = input.ToArray();
        digest.BlockUpdate(bytes, 0, bytes.Length);
        var result = new byte[32];
        digest.DoFinal(result, 0);
        return result;
    }
}
