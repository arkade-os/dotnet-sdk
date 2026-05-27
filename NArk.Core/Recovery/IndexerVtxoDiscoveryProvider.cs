using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NBitcoin.Scripting;

namespace NArk.Core.Recovery;

/// <summary>
/// Discovery provider that asks arkd's indexer whether any VTXO (spent or unspent)
/// is recorded against the contracts derivable from a given HD index. A single VTXO
/// at any state is sufficient evidence the index was used.
/// <para>
/// To recover funds locked under <b>legacy</b> script formats, this derives and
/// probes more than the current default contract — mirroring the canonical
/// <c>arkade-os/ts-sdk</c> restore. For each index it builds, for <b>every</b>
/// signer in <c>{ current SignerKey } ∪ DeprecatedSigners</c> (server-key rotation
/// leaves old funds under a different script):
/// </para>
/// <list type="bullet">
///   <item><see cref="ArkPaymentContract"/> (the default VTXO script), and</item>
///   <item><see cref="ArkDelegateContract"/> (the delegate VTXO script) for each
///   configured delegate descriptor (<see cref="RecoveryDelegateConfig"/>), if any.</item>
/// </list>
/// The exit delay is invariant across signers; only the server key changes. Every
/// candidate whose script the indexer reports a VTXO for is returned so the
/// orchestrator persists it.
/// </summary>
public class IndexerVtxoDiscoveryProvider(
    IClientTransport clientTransport,
    RecoveryDelegateConfig? delegateConfig = null,
    ILogger<IndexerVtxoDiscoveryProvider>? logger = null) : IContractDiscoveryProvider
{
    // ArkServerInfo is invariant for the wallet-recovery use case (signer keys,
    // exit delays, network — all server-side config that doesn't change between
    // probes). Cache the successful RESULT (not a Task) and reuse it for every
    // subsequent index — saves N round-trips per scan. We deliberately do NOT
    // cache a Lazy<Task>: a Lazy would permanently publish a *faulted* task if
    // the first fetch fails (e.g. arkd restarts mid-scan), poisoning every later
    // probe into a silent zero-result recovery. Caching the result lets a
    // transient failure be retried on the next index.
    private ArkServerInfo? _serverInfo;
    private readonly SemaphoreSlim _serverInfoLock = new(1, 1);

    private readonly IReadOnlyList<OutputDescriptor> _delegates = delegateConfig?.Delegates ?? [];

    private async Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken)
    {
        if (_serverInfo is { } cached)
            return cached;
        await _serverInfoLock.WaitAsync(cancellationToken);
        try
        {
            return _serverInfo ??= await clientTransport.GetServerInfoAsync(cancellationToken);
        }
        finally
        {
            _serverInfoLock.Release();
        }
    }

    /// <inheritdoc />
    public string Name => "indexer";

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(
        ArkWalletInfo wallet,
        OutputDescriptor userDescriptor,
        int index,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await GetServerInfoAsync(cancellationToken);

        // Build the full candidate set: default (+ delegate) contracts against the
        // current signer and every deprecated signer. Keyed by scriptPubKey hex so
        // a returned VTXO maps back to the exact contract that owns it.
        var byScript = BuildCandidates(serverInfo, userDescriptor);

        var matched = new Dictionary<string, ArkContract>(StringComparer.OrdinalIgnoreCase);
        await foreach (var vtxo in clientTransport.GetVtxoByScriptsAsSnapshot(
                           byScript.Keys.ToHashSet(), cancellationToken))
        {
            if (byScript.TryGetValue(vtxo.Script, out var contract) && matched.TryAdd(vtxo.Script, contract))
            {
                logger?.LogDebug(
                    "IndexerVtxoDiscoveryProvider: hit at index {Index} on script {Script} ({Type})",
                    index, vtxo.Script, contract.Type);
                // Stop early once every candidate has a hit — nothing more to learn.
                if (matched.Count == byScript.Count)
                    break;
            }
        }

        return matched.Count == 0
            ? DiscoveryResult.NotFound
            : new DiscoveryResult(true, matched.Values.ToList());
    }

    private Dictionary<string, ArkContract> BuildCandidates(
        ArkServerInfo serverInfo, OutputDescriptor userDescriptor)
    {
        // Current signer first, then every deprecated signer (server-key rotation).
        // DeprecatedSigners maps key -> cutoff timestamp; only the key matters here,
        // the exit delay is the server-wide UnilateralExit.
        var signers = new List<OutputDescriptor> { serverInfo.SignerKey };
        foreach (var deprecated in serverInfo.DeprecatedSigners.Keys)
            signers.Add(deprecated.ToOutputDescriptor(serverInfo.Network));

        var byScript = new Dictionary<string, ArkContract>(StringComparer.OrdinalIgnoreCase);
        foreach (var signer in signers)
        {
            AddCandidate(byScript, new ArkPaymentContract(signer, serverInfo.UnilateralExit, userDescriptor));
            foreach (var delegateDescriptor in _delegates)
                AddCandidate(byScript, new ArkDelegateContract(
                    signer, serverInfo.UnilateralExit, userDescriptor, delegateDescriptor));
        }

        return byScript;
    }

    private static void AddCandidate(Dictionary<string, ArkContract> byScript, ArkContract contract)
        => byScript.TryAdd(contract.GetScriptPubKey().ToHex(), contract);
}
