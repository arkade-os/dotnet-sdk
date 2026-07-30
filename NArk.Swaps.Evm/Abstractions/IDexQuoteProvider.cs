using System.Numerics;
using NArk.Swaps.Evm.Contracts.Router;
using NArk.Swaps.Evm.Dex;

namespace NArk.Swaps.Evm;

/// <summary>
/// Builds the <see cref="Call"/> array + slippage-protected minimum output for a DEX hop between
/// two ERC20 tokens, to be executed inside Router's <c>executeAndLockERC20WithPermit2</c>/
/// <c>claimERC20Execute</c> — see <see cref="DEXSwapService"/>.
///
/// Deliberately an interface, not baked into <see cref="DEXSwapService"/> directly: the
/// Permit2/Router signing and calldata-execution mechanics (the fund-critical part, verified
/// live in <c>RouterDexHopTests.cs</c>) are entirely independent of which DEX actually performs
/// the swap — Router's <c>Call[]</c> are arbitrary target+calldata, so swapping in a different
/// quote/routing implementation later (or a mock for tests) never touches the signing code.
/// </summary>
// Production implementation: NArk.Swaps.Evm.Dex.BoltzDexQuoteProvider, backed by Boltz's own
// /v2/quote/{currency}/in+/encode endpoints (no separate DEX SDK/on-chain Quoter needed — see
// that class's doc comment). RouterDexHopTests.cs's MockERC20Dex-backed test double remains for
// proving the Router/Permit2 plumbing without a network round-trip.
// TODO: still not wired into DI (EvmSwapServiceCollectionExtensions) — needs a decision on
// Router contract address config and Web3 instance sharing with EvmChainClient first.
public interface IDexQuoteProvider
{
    Task<DexSwapQuote> GetSwapCallsAsync(
        string tokenIn, string tokenOut, BigInteger amountIn, CancellationToken ct = default);
}
