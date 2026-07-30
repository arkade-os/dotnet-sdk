using Nethereum.Signer;
using NArk.Core.Contracts;
using NArk.Swaps.Boltz.Models.Swaps.Chain;

namespace NArk.Swaps.Evm;

/// <summary>
/// Result of creating an EVM-leg Boltz Chain Swap (<c>ARK &lt;-&gt; EvmArbitrum</c>).
/// Sibling of <c>NArk.Swaps.Boltz.Models.ChainSwapResult</c> (which is typed around an
/// NBitcoin <c>Key</c> and Bitcoin Taproot swap-tree fields) — this one carries an EVM
/// key instead, since there's no Taproot script/tree on the EVM side: the HTLC logic lives
/// in the deployed <c>ERC20Swap</c> contract, not in a reconstructed spending script.
/// </summary>
public record EvmChainSwapResult(
    ChainResponse Swap,
    byte[] Preimage,
    byte[] PreimageHash,
    EthECKey EphemeralEvmKey,
    VHTLCContract? Contract = null);
