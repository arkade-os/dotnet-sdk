using System.Numerics;
using System.Text;
using Nethereum.ABI;
using Nethereum.ABI.FunctionEncoding;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Signer;
using Nethereum.Util;
using NArk.Swaps.Evm.Contracts.Router;

namespace NArk.Swaps.Evm;

/// <summary>
/// Builds and signs the Permit2 "witness transfer" EIP-712 digest that
/// <c>Router.sol</c>'s <c>executeAndLockERC20WithPermit2</c> verifies via
/// <c>ISignatureTransfer.permitWitnessTransferFrom</c> — the mechanism that lets us authorize
/// pulling an arbitrary ERC20 (e.g. USDT) into the Router with a signature instead of a
/// per-call on-chain <c>approve()</c> to the Router (a one-time <c>approve(PERMIT2, amount)</c>
/// from the owner is still required — Permit2 moves funds via its own allowance).
///
/// The exact struct/typehash composition is derived from <c>Contracts/Sol/Router.sol</c>'s own
/// <c>permit2Transfer</c>/<c>TYPEHASH_EXECUTE_LOCK_ERC20</c>/<c>TYPESTRING_EXECUTE_LOCK_ERC20</c>,
/// cross-checked field-for-field against BoltzExchange/boltz-core's own Foundry test helper for
/// it (<c>contracts/test/RouterTestBase.sol</c>'s <c>signPermit2WitnessTransfer</c> — a helper
/// Boltz's own passing tests already exercise against real Permit2 bytecode). This is
/// fund-critical: a wrong typehash/field order/padding produces a signature Permit2 will simply
/// reject (fails safe, doesn't lose funds), but correctness here should be trusted only because
/// <c>RouterDexHopTests.cs</c> exercises this against a real deployed Router + real Permit2
/// (via Anvil's <c>anvil_setCode</c>) end-to-end — not because it's merely unit-tested in
/// isolation.
/// </summary>
public static class Permit2Signer
{
    /// <summary>
    /// Permit2's own constant (Uniswap/permit2's <c>PermitHash.sol</c>) — identical on every
    /// chain/deployment, independent of which Router/token is involved.
    /// </summary>
    private const string TokenPermissionsTypeString = "TokenPermissions(address token,uint256 amount)";

    /// <summary>
    /// Permit2's own witness-transfer typehash stub (<c>PermitHash.sol</c>'s
    /// <c>_PERMIT_TRANSFER_FROM_WITNESS_TYPEHASH_STUB</c>) — concatenated with the caller-supplied
    /// witness type string to form the full <c>PermitWitnessTransferFrom</c> typehash Permit2
    /// recomputes on-chain. Confirmed against boltz-core's own
    /// <c>RouterTestBase.signPermit2WitnessTransfer</c>, which uses the identical literal.
    /// </summary>
    private const string PermitWitnessTransferFromTypehashStub =
        "PermitWitnessTransferFrom(TokenPermissions permitted,address spender,uint256 nonce,uint256 deadline,";

    /// <summary>
    /// <c>keccak256(abi.encode(calls))</c> — matches <c>executeAndLockERC20WithPermit2</c>'s own
    /// inline-assembly computation of <c>callsHash</c>, which packs the same bytes plain
    /// <c>abi.encode</c> would for a single dynamic-array argument.
    /// </summary>
    public static byte[] ComputeCallsHash(IReadOnlyList<Call> calls)
    {
        var encoded = new ParametersEncoder().EncodeParametersFromTypeAttributes(
            typeof(CallsOnlyParameter), new CallsOnlyParameter { Calls = calls.ToList() });
        return Sha3Keccack.Current.CalculateHash(encoded);
    }

    /// <summary>
    /// <c>witness = keccak256(abi.encode(TYPEHASH_EXECUTE_LOCK_ERC20, preimageHash, tokenAddress,
    /// claimAddress, refundAddress, timelock, callsHash))</c> — reproduces
    /// <c>Router.sol</c>'s <c>permit2Transfer</c> assembly block as plain <c>abi.encode</c> (every
    /// field here is a static 32-byte word, so the two are byte-identical).
    /// </summary>
    public static byte[] ComputeWitness(
        byte[] typehashExecuteLockErc20, byte[] preimageHash, string tokenAddress, string claimAddress,
        string refundAddress, BigInteger timelock, byte[] callsHash) =>
        new ABIEncode().GetSha3ABIEncoded(
            new ABIValue("bytes32", typehashExecuteLockErc20),
            new ABIValue("bytes32", preimageHash),
            new ABIValue("address", tokenAddress),
            new ABIValue("address", claimAddress),
            new ABIValue("address", refundAddress),
            new ABIValue("uint256", timelock),
            new ABIValue("bytes32", callsHash));

    /// <summary>
    /// Signs the full Permit2 witness-transfer digest with <paramref name="ownerKey"/>, matching
    /// <c>RouterTestBase.signPermit2WitnessTransfer</c> exactly: Permit2's own EIP-712 domain
    /// (<paramref name="permit2DomainSeparator"/> — NOT Router's own domain), the
    /// <c>TokenPermissions</c> struct hash, and the composed
    /// <c>PermitWitnessTransferFrom</c>+witness struct hash. <paramref name="spenderRouterAddress"/>
    /// is the Router contract address — Permit2's "spender" for this transfer is always the
    /// contract that calls <c>permitWitnessTransferFrom</c>, which is the Router itself.
    /// </summary>
    public static byte[] Sign(
        EthECKey ownerKey, byte[] permit2DomainSeparator, string typestringExecuteLockErc20,
        string permittedToken, BigInteger permittedAmount, string spenderRouterAddress,
        BigInteger nonce, BigInteger deadline, byte[] witness)
    {
        var typehash = Sha3Keccack.Current.CalculateHash(
            Encoding.UTF8.GetBytes(PermitWitnessTransferFromTypehashStub + typestringExecuteLockErc20));

        var tokenPermissionsTypehash = Sha3Keccack.Current.CalculateHash(
            Encoding.UTF8.GetBytes(TokenPermissionsTypeString));
        var tokenPermissionsHash = new ABIEncode().GetSha3ABIEncoded(
            new ABIValue("bytes32", tokenPermissionsTypehash),
            new ABIValue("address", permittedToken),
            new ABIValue("uint256", permittedAmount));

        var structHash = new ABIEncode().GetSha3ABIEncoded(
            new ABIValue("bytes32", typehash),
            new ABIValue("bytes32", tokenPermissionsHash),
            new ABIValue("address", spenderRouterAddress),
            new ABIValue("uint256", nonce),
            new ABIValue("uint256", deadline),
            new ABIValue("bytes32", witness));

        // "\x19\x01" || domainSeparator || structHash — plain byte concatenation
        // (abi.encodePacked of already fixed-length byte sequences), not abi.encode.
        var digestInput = new byte[2 + 32 + 32];
        digestInput[0] = 0x19;
        digestInput[1] = 0x01;
        Buffer.BlockCopy(permit2DomainSeparator, 0, digestInput, 2, 32);
        Buffer.BlockCopy(structHash, 0, digestInput, 34, 32);
        var digest = Sha3Keccack.Current.CalculateHash(digestInput);

        // Plain-message v (27/28), not the EIP-155 chain-id-adjusted v used for signing
        // transactions — Permit2's ecrecover expects the former.
        var signature = ownerKey.SignAndCalculateV(digest);

        var packed = new byte[65];
        Buffer.BlockCopy(PadLeft32(signature.R), 0, packed, 0, 32);
        Buffer.BlockCopy(PadLeft32(signature.S), 0, packed, 32, 32);
        packed[64] = signature.V[0];
        return packed;
    }

    /// <summary>
    /// BouncyCastle's <c>BigInteger.ToByteArrayUnsigned()</c> (what <see cref="EthECDSASignature"/>'s
    /// R/S accessors return) omits leading zero bytes — roughly a 1-in-256 chance per component —
    /// so R/S must be explicitly left-padded to 32 bytes before packing into the 65-byte
    /// signature, or the signature is malformed for those rare values.
    /// </summary>
    private static byte[] PadLeft32(byte[] value)
    {
        if (value.Length == 32) return value;
        if (value.Length > 32)
            throw new ArgumentException($"value is {value.Length} bytes, expected at most 32", nameof(value));

        var padded = new byte[32];
        Buffer.BlockCopy(value, 0, padded, 32 - value.Length, value.Length);
        return padded;
    }

    private class CallsOnlyParameter
    {
        [Parameter("tuple[]", "calls", 1)]
        public List<Call> Calls { get; set; } = [];
    }
}
