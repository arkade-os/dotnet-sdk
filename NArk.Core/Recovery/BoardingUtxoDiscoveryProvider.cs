using Microsoft.Extensions.Logging;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Services;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NBitcoin.Scripting;

namespace NArk.Core.Recovery;

/// <summary>
/// Discovery provider that asks the on-chain side (NBXplorer / Esplora,
/// abstracted by <see cref="IBoardingUtxoProvider"/>) whether the
/// <see cref="ArkBoardingContract"/> derived from a given HD index ever
/// received a UTXO. Boarding contracts are one-shot funding entry points
/// onto the Ark — a historical hit at any index is unambiguous evidence
/// of usage.
/// </summary>
/// <remarks>
/// <see cref="IBoardingUtxoProvider"/> is optional in the SDK (the plugin
/// provides one via NBXplorer). When no implementation is registered this
/// provider is also not registered, so HD recovery still works without
/// on-chain probing — just without boarding-address detection.
/// </remarks>
public class BoardingUtxoDiscoveryProvider(
    IBoardingUtxoProvider utxoProvider,
    IClientTransport clientTransport,
    ILogger<BoardingUtxoDiscoveryProvider>? logger = null) : IContractDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "boarding";

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(
        ArkWalletInfo wallet,
        OutputDescriptor userDescriptor,
        int index,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var contract = new ArkBoardingContract(serverInfo.SignerKey, serverInfo.BoardingExit, userDescriptor);
        var address = contract.GetOnchainAddress(serverInfo.Network).ToString();

        var utxos = await utxoProvider.GetUtxosAsync(address, cancellationToken);
        if (utxos.Count == 0) return DiscoveryResult.NotFound;

        logger?.LogDebug(
            "BoardingUtxoDiscoveryProvider: hit at index {Index} on address {Address} ({Count} UTXO(s))",
            index, address, utxos.Count);
        return new DiscoveryResult(true, [contract]);
    }
}
