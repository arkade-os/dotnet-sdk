using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.Restore;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NBitcoin.Crypto;
using NBitcoin.Scripting;

namespace NArk.Swaps.Evm.Recovery;

/// <summary>
/// Discovery provider that asks Boltz whether the user pubkey at a given HD derivation index
/// ever participated in a chain swap for this provider's EVM pair
/// (<see cref="EvmSwapOptions.PairCurrency"/> ↔ <c>ARK</c>).
/// </summary>
/// <remarks>
/// Mirrors <c>NArk.Swaps.Recovery.BoltzSwapDiscoveryProvider</c>'s shape, but can't reuse it or
/// <c>SwapsManagementService.RestoreSwaps</c> directly — those only recognize the ARK/BTC pair
/// (the only chain-swap currency NArk.Swaps itself knows about). Instead this calls
/// <see cref="BoltzClient.RestoreSwapsAsync(string,CancellationToken)"/> directly, filters to
/// entries matching this provider's own pair, and reconstructs the Ark leg via
/// <see cref="SwapsManagementService.ReconstructChainVhtlcContract"/> — the currency-agnostic
/// building block exposed for exactly this purpose.
/// <para>
/// Restored swaps get their preimage back too: both directions derive it deterministically from
/// the wallet's own key material via <see cref="SwapPreimageDerivation"/>, so
/// <see cref="RederivePreimageMetadataAsync"/> can reproduce it here from the same descriptor
/// Boltz matched the swap against — no manual enrichment needed before the sweeper can claim.
/// Swaps created by an older build (or by a watch-only wallet) still carry a random preimage;
/// those are detected by the preimage-hash check and left without one rather than being given a
/// wrong value.
/// </para>
/// </remarks>
public class EvmChainSwapDiscoveryProvider(
    BoltzClient boltzClient,
    IClientTransport clientTransport,
    IWalletProvider walletProvider,
    ISwapStorage swapStorage,
    IContractService contractService,
    IOptions<EvmSwapOptions> options,
    ILogger<EvmChainSwapDiscoveryProvider>? logger = null) : IContractDiscoveryProvider
{
    private readonly EvmSwapOptions _options = options.Value;

    /// <inheritdoc />
    public string Name => "evm";

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(
        ArkWalletInfo wallet,
        OutputDescriptor userDescriptor,
        int index,
        CancellationToken cancellationToken = default)
    {
        var extracted = userDescriptor.Extract();
        var pubKeyHex = Convert.ToHexString(
            extracted.PubKey?.ToBytes() ?? extracted.XOnlyPubKey.ToBytes()).ToLowerInvariant();

        var restored = await boltzClient.RestoreSwapsAsync(pubKeyHex, cancellationToken);

        var ours = restored.Where(r => r.IsChainSwap &&
            ((r.From == "ARK" && r.To == _options.PairCurrency) ||
             (r.From == _options.PairCurrency && r.To == "ARK"))).ToArray();

        if (ours.Length == 0)
            return DiscoveryResult.NotFound;

        var existingIds = (await swapStorage.GetSwaps(
                walletIds: [wallet.Id], swapIds: ours.Select(r => r.Id).ToArray(),
                cancellationToken: cancellationToken))
            .Select(s => s.SwapId)
            .ToHashSet();

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var found = existingIds.Count > 0;

        foreach (var restoredSwap in ours.Where(r => !existingIds.Contains(r.Id)))
        {
            // ChainEvmToArk (From == our pair): we claim the Ark leg (ClaimDetails).
            // ChainArkToEvm (From == "ARK"): we lock the Ark leg ourselves (RefundDetails).
            var weAreReceiver = restoredSwap.From == _options.PairCurrency;
            var swapType = weAreReceiver ? ArkSwapType.ChainEvmToArk : ArkSwapType.ChainArkToEvm;
            var route = weAreReceiver
                ? new SwapRoute(SwapAsset.ArbitrumTbtc, SwapAsset.ArkBtc)
                : new SwapRoute(SwapAsset.ArkBtc, SwapAsset.ArbitrumTbtc);

            var contract = SwapsManagementService.ReconstructChainVhtlcContract(
                restoredSwap, weAreReceiver, serverInfo, [userDescriptor]);
            if (contract == null)
            {
                logger?.LogWarning(
                    "EvmChainSwapDiscoveryProvider: swap {SwapId} matched pair {From}->{To} but its " +
                    "Ark-leg VHTLC couldn't be reconstructed — skipping",
                    restoredSwap.Id, restoredSwap.From, restoredSwap.To);
                continue;
            }

            var utxoDetails = (weAreReceiver ? restoredSwap.ClaimDetails : restoredSwap.RefundDetails) as UtxoSwapDetails;
            var contractScript = contract.GetArkAddress().ScriptPubKey.ToHex();

            var swap = new ArkSwap(
                SwapId: restoredSwap.Id,
                WalletId: wallet.Id,
                SwapType: swapType,
                Invoice: "",
                ExpectedAmount: utxoDetails?.Amount ?? 0,
                ContractScript: contractScript,
                Address: utxoDetails?.LockupAddress ?? "",
                Status: BoltzSwapStatus.ToArkSwapStatus(restoredSwap.Status) ?? ArkSwapStatus.Pending,
                FailReason: null,
                CreatedAt: DateTimeOffset.FromUnixTimeSeconds(restoredSwap.CreatedAt),
                UpdatedAt: DateTimeOffset.UtcNow,
                Hash: restoredSwap.PreimageHash ?? ""
            )
            {
                ProviderId = EvmChainSwapProvider.Id,
                Route = route,
                Metadata = await RederivePreimageMetadataAsync(
                    wallet, userDescriptor, restoredSwap.PreimageHash, restoredSwap.Id, cancellationToken),
            };

            await contractService.ImportContract(
                wallet.Id,
                contract,
                ContractActivityState.AwaitingFundsBeforeDeactivate,
                metadata: new Dictionary<string, string> { ["Source"] = $"swap:{restoredSwap.Id}" },
                cancellationToken: cancellationToken);
            await swapStorage.SaveSwap(wallet.Id, swap, cancellationToken);

            logger?.LogInformation(
                "EvmChainSwapDiscoveryProvider: restored chain swap {SwapId} ({SwapType}) at index {Index}",
                restoredSwap.Id, swapType, index);
            found = true;
        }

        // Mirrors BoltzSwapDiscoveryProvider: contracts are already imported above with rich
        // Source=swap:<id> metadata, so return Used=true with no Contracts to avoid double-imports.
        return found ? new DiscoveryResult(true, []) : DiscoveryResult.NotFound;
    }

    /// <summary>
    /// Re-derives the swap's preimage from the wallet's own key material and returns it as swap
    /// metadata, or an empty dictionary when it can't be reproduced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both directions derive their preimage from whichever descriptor is ours on the Arkade leg
    /// (refund descriptor for <c>ChainArkToEvm</c>, claim descriptor for <c>ChainEvmToArk</c>) —
    /// and that is exactly the descriptor Boltz matched this swap against, so
    /// <paramref name="userDescriptor"/> reproduces it. <see cref="SwapPreimageDerivation"/>
    /// anchors on the canonical x-only key rather than the descriptor string, so a reconstructed
    /// bare descriptor derives the same value as the original signing descriptor.
    /// </para>
    /// <para>
    /// The result is checked against the preimage hash Boltz reported before being stored. A
    /// mismatch means this swap was created with a random preimage (pre-derivation SDK build, or
    /// a watch-only wallet) — storing the derived value anyway would hand the sweeper a preimage
    /// that fails on-chain and look like a successful recovery.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<string, string>> RederivePreimageMetadataAsync(
        ArkWalletInfo wallet, OutputDescriptor userDescriptor, string? reportedPreimageHash, string swapId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(reportedPreimageHash))
            return [];

        try
        {
            var preimage = await SwapPreimageDerivation.DeriveAsync(
                walletProvider, wallet.Id, userDescriptor, index: 0, cancellationToken);

            var derivedHash = Convert.ToHexString(Hashes.SHA256(preimage)).ToLowerInvariant();
            if (!string.Equals(derivedHash, reportedPreimageHash, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning(
                    "EvmChainSwapDiscoveryProvider: swap {SwapId} restored, but the re-derived preimage hashes to " +
                    "{Derived} while Boltz reports {Reported} — created with a random preimage, so it cannot be " +
                    "recovered from the seed alone",
                    swapId, derivedHash, reportedPreimageHash);
                return [];
            }

            logger?.LogInformation(
                "EvmChainSwapDiscoveryProvider: re-derived preimage for restored swap {SwapId}", swapId);
            return new Dictionary<string, string>
            {
                [SwapMetadata.Preimage] = Convert.ToHexString(preimage).ToLowerInvariant(),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex,
                "EvmChainSwapDiscoveryProvider: failed to re-derive the preimage for restored swap {SwapId}", swapId);
            return [];
        }
    }
}
