using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NBitcoin.Scripting;

namespace NArk.Core.Recovery;

/// <summary>
/// Discovery provider that asks arkd's indexer whether the
/// <see cref="ArkPaymentContract"/> derived from a given HD index has any
/// VTXO recorded against it (spent or unspent). A single VTXO at any state
/// is sufficient evidence the index was used.
/// </summary>
public class IndexerVtxoDiscoveryProvider(
    IClientTransport clientTransport,
    ILogger<IndexerVtxoDiscoveryProvider>? logger = null) : IContractDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "indexer";

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(
        ArkWalletInfo wallet,
        OutputDescriptor userDescriptor,
        int index,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var contract = new ArkPaymentContract(serverInfo.SignerKey, serverInfo.UnilateralExit, userDescriptor);
        var script = contract.GetScriptPubKey().ToHex();

        await foreach (var _ in clientTransport.GetVtxoByScriptsAsSnapshot(
                           new HashSet<string> { script }, cancellationToken))
        {
            // Any VTXO is enough — return immediately to avoid streaming the whole script's history.
            logger?.LogDebug(
                "IndexerVtxoDiscoveryProvider: hit at index {Index} on script {Script}",
                index, script);
            return new DiscoveryResult(true, [contract]);
        }

        return DiscoveryResult.NotFound;
    }
}
