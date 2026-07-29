using System.Numerics;
using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;

namespace NArk.Swaps.Evm;

/// <summary>
/// Builds and signs the EIP-712 "cooperative claim" signature <c>ERC20Swap.sol</c>'s
/// <c>claim(preimage, amount, tokenAddress, refundAddress, timelock, v, r, s)</c> overload
/// verifies — this is what <c>Router.claimERC20Execute</c> needs (via its <c>Erc20Claim</c>
/// struct) to claim a Boltz tBTC lockup on our behalf and atomically swap+sweep the proceeds to
/// a different asset (e.g. USDT) in one transaction, since Router itself becomes <c>msg.sender</c>
/// as seen by <c>ERC20Swap</c> and so cannot use the plain preimage-only claim overload (that one
/// hardcodes <c>msg.sender</c> as the claim destination).
///
/// A distinct EIP-712 scheme from <see cref="Permit2Signer"/> — ERC20Swap's own domain
/// (name="ERC20Swap", version="6") and a flat <c>Claim</c> struct, no witness/type-string
/// composition. Derived from <c>Contracts/Sol/ERC20Swap.sol</c>'s <c>TYPEHASH_CLAIM</c>/claim()
/// body, cross-checked against BoltzExchange/boltz-core's own Foundry test helper
/// (<c>contracts/test/RouterTestBase.sol</c>'s <c>signErc20Claim</c>/<c>SigUtils.hashErc20SwapClaim</c>
/// — a helper Boltz's own passing tests already exercise against real ERC20Swap+Router bytecode).
/// Same fund-critical caveat as <see cref="Permit2Signer"/> applies: trust this only because
/// <c>RouterDexHopTests.cs</c> verifies it end-to-end against a real deployed contract, not
/// because it's merely unit-tested in isolation.
/// </summary>
public static class Erc20SwapClaimSigner
{
    /// <summary>
    /// Signs the claim authorization: <c>destination</c> is the address <c>ERC20Swap</c> will see
    /// as <c>msg.sender</c> when the claim executes — for <c>Router.claimERC20Execute</c> this is
    /// the Router contract's own address, NOT our EOA, since the Router is what actually calls
    /// <c>ERC20Swap.claim</c> on our behalf.
    /// </summary>
    public static (byte[] R, byte[] S, byte V) Sign(
        EthECKey claimKey, byte[] erc20SwapDomainSeparator, byte[] typehashClaim,
        byte[] preimage, BigInteger amount, string tokenAddress, string refundAddress,
        BigInteger timelock, string destination)
    {
        var structHash = new ABIEncode().GetSha3ABIEncoded(
            new ABIValue("bytes32", typehashClaim),
            new ABIValue("bytes32", preimage),
            new ABIValue("uint256", amount),
            new ABIValue("address", tokenAddress),
            new ABIValue("address", refundAddress),
            new ABIValue("uint256", timelock),
            new ABIValue("address", destination));

        var digestInput = new byte[2 + 32 + 32];
        digestInput[0] = 0x19;
        digestInput[1] = 0x01;
        Buffer.BlockCopy(erc20SwapDomainSeparator, 0, digestInput, 2, 32);
        Buffer.BlockCopy(structHash, 0, digestInput, 34, 32);
        var digest = Sha3Keccack.Current.CalculateHash(digestInput);

        var signature = claimKey.SignAndCalculateV(digest);
        return (PadLeft32(signature.R), PadLeft32(signature.S), signature.V[0]);
    }

    /// <summary>Same 1-in-256-per-component leading-zero-truncation concern as
    /// <see cref="Permit2Signer"/>'s identical helper — see its doc comment.</summary>
    private static byte[] PadLeft32(byte[] value)
    {
        if (value.Length == 32) return value;
        if (value.Length > 32)
            throw new ArgumentException($"value is {value.Length} bytes, expected at most 32", nameof(value));

        var padded = new byte[32];
        Buffer.BlockCopy(value, 0, padded, 32 - value.Length, value.Length);
        return padded;
    }
}
