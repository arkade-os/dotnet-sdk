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
/// One known gap carried over from swap creation: <see cref="EvmChainSwapProvider.CreateEvmToArkSwapAsync"/>
/// generates a random (not deterministic) preimage, so a <c>ChainEvmToArk</c> swap restored here
/// gets its Ark-leg contract/metadata back, but never its preimage — that was only ever in
/// storage, and storage is exactly what a restore is recovering from. Until preimage generation
/// there becomes deterministic (mirroring the reverse/ChainBtcToArk scheme), a restored
/// <c>ChainEvmToArk</c> swap needs manual preimage enrichment before the sweeper can claim it.
/// </para>
/// </remarks>
public class EvmChainSwapDiscoveryProvider(
    BoltzClient boltzClient,
    IClientTransport clientTransport,
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
}
