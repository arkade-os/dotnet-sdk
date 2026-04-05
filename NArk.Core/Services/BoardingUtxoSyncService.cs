using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Services;

/// <summary>
/// Synchronizes boarding UTXOs from the Bitcoin blockchain via an <see cref="IBoardingUtxoProvider"/>.
/// Queries on-chain UTXOs for boarding contract addresses and upserts them into VTXO storage.
/// </summary>
public class BoardingUtxoSyncService
{
    private readonly IContractStorage _contractStorage;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IClientTransport _clientTransport;
    private readonly IBoardingUtxoProvider _utxoProvider;
    private readonly ILogger<BoardingUtxoSyncService>? _logger;

    public BoardingUtxoSyncService(
        IContractStorage contractStorage,
        IVtxoStorage vtxoStorage,
        IClientTransport clientTransport,
        IBoardingUtxoProvider utxoProvider,
        ILogger<BoardingUtxoSyncService>? logger = null)
    {
        _contractStorage = contractStorage;
        _vtxoStorage = vtxoStorage;
        _clientTransport = clientTransport;
        _utxoProvider = utxoProvider;
        _logger = logger;
    }

    /// <summary>
    /// Sync all boarding contracts from storage.
    /// </summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var boardingContracts = await _contractStorage.GetContracts(
            contractTypes: [ArkBoardingContract.ContractType],
            cancellationToken: cancellationToken);

        if (boardingContracts.Count == 0)
        {
            _logger?.LogDebug("No boarding contracts found, nothing to sync");
            return;
        }

        await SyncAsync(boardingContracts, cancellationToken);
    }

    /// <summary>
    /// Sync specific boarding contracts.
    /// </summary>
    public async Task SyncAsync(IReadOnlyCollection<ArkContractEntity> contracts, CancellationToken cancellationToken = default)
    {
        if (contracts.Count == 0)
            return;

        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

        if (!serverInfo.BoardingAllowed)
        {
            _logger?.LogDebug("Boarding is disabled by the server (UtxoMaxAmount=0), skipping sync");
            return;
        }

        var network = serverInfo.Network;
        var boardingExitDelay = serverInfo.BoardingExit;

        _logger?.LogInformation("Syncing {Count} boarding contracts", contracts.Count);

        foreach (var contractEntity in contracts)
        {
            try
            {
                await SyncContractAsync(contractEntity, serverInfo, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to sync boarding contract with script {Script}", contractEntity.Script);
            }
        }
    }

    private async Task SyncContractAsync(
        ArkContractEntity contractEntity,
        ArkServerInfo serverInfo,
        CancellationToken cancellationToken)
    {
        // Derive the P2TR address from the stored scriptPubKey hex
        var script = Script.FromHex(contractEntity.Script);
        var address = script.GetDestinationAddress(serverInfo.Network);

        if (address is null)
        {
            _logger?.LogWarning("Could not derive address from script {Script}", contractEntity.Script);
            return;
        }

        var addressStr = address.ToString();
        _logger?.LogDebug("Querying UTXOs at address {Address}", addressStr);

        var utxos = await _utxoProvider.GetUtxosAsync(addressStr, cancellationToken);

        // Get existing VTXOs for this script to detect spent ones
        var existingVtxos = await _vtxoStorage.GetVtxos(
            scripts: [contractEntity.Script],
            cancellationToken: cancellationToken);

        var onchainOutpoints = new HashSet<string>();

        foreach (var utxo in utxos)
        {
            onchainOutpoints.Add($"{utxo.Txid}:{utxo.Vout}");

            // Skip UTXOs outside server-configured boarding bounds
            var utxoAmount = Money.Satoshis(utxo.Amount);
            if (serverInfo.UtxoMinAmount is { } utxoMin && utxoMin > Money.Zero && utxoAmount < utxoMin)
            {
                _logger?.LogDebug("Skipping boarding UTXO {Txid}:{Vout} ({Amount} sats): below minimum {Min}",
                    utxo.Txid, utxo.Vout, utxo.Amount, utxoMin);
                continue;
            }

            if (serverInfo.UtxoMaxAmount is { } utxoMax && utxoAmount > utxoMax)
            {
                _logger?.LogDebug("Skipping boarding UTXO {Txid}:{Vout} ({Amount} sats): above maximum {Max}",
                    utxo.Txid, utxo.Vout, utxo.Amount, utxoMax);
                continue;
            }

            var createdAt = utxo.BlockTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(utxo.BlockTime)
                : DateTimeOffset.UtcNow;

            var expiresAt = utxo.Confirmed ? ComputeExpiresAt(createdAt, serverInfo.BoardingExit) : null;

            var metadata = new Dictionary<string, string>
            {
                ["Confirmed"] = utxo.Confirmed.ToString()
            };

            var arkVtxo = new ArkVtxo(
                Script: contractEntity.Script,
                TransactionId: utxo.Txid,
                TransactionOutputIndex: utxo.Vout,
                Amount: utxo.Amount,
                SpentByTransactionId: null,
                SettledByTransactionId: null,
                Swept: false,
                CreatedAt: createdAt,
                ExpiresAt: expiresAt,
                ExpiresAtHeight: utxo.Confirmed && utxo.BlockHeight > 0
                    ? (uint)(utxo.BlockHeight + GetBlockCount(serverInfo.BoardingExit))
                    : null,
                Unrolled: true,
                Metadata: metadata);

            await _vtxoStorage.UpsertVtxo(arkVtxo, cancellationToken);
            _logger?.LogDebug("Upserted boarding VTXO {Txid}:{Vout} ({Amount} sats, confirmed={Confirmed})",
                utxo.Txid, utxo.Vout, utxo.Amount, utxo.Confirmed);
        }

        // Mark spent: existing unspent VTXOs that are no longer in the provider response
        foreach (var existing in existingVtxos)
        {
            if (existing.IsSpent())
                continue;

            var key = $"{existing.TransactionId}:{existing.TransactionOutputIndex}";
            if (!onchainOutpoints.Contains(key))
            {
                _logger?.LogInformation("Boarding VTXO {Outpoint} no longer onchain, marking as spent", key);
                var spent = existing with { SpentByTransactionId = "onchain-spent" };
                await _vtxoStorage.UpsertVtxo(spent, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Compute expiration time from confirmation time and BIP68 sequence.
    /// For block-based sequences: blocks * 10 minutes (approximate).
    /// For time-based sequences: value * 512 seconds (BIP68 granularity).
    /// </summary>
    internal static DateTimeOffset? ComputeExpiresAt(DateTimeOffset confirmationTime, Sequence sequence)
    {
        if (!sequence.IsRelativeLock)
            return null;

        var seconds = GetRelativeSeconds(sequence);
        return confirmationTime.AddSeconds(seconds);
    }

    internal static long GetRelativeSeconds(Sequence sequence)
    {
        var value = sequence.Value & 0x0000FFFF; // mask out flags

        if ((sequence.Value & (1 << 22)) != 0)
        {
            // Time-based: value * 512 seconds
            return value * 512L;
        }

        // Block-based: value blocks * ~600 seconds (10 min per block)
        return value * 600L;
    }

    internal static uint GetBlockCount(Sequence sequence)
    {
        if ((sequence.Value & (1 << 22)) != 0)
        {
            // Time-based: approximate block count from time
            var seconds = (sequence.Value & 0x0000FFFF) * 512L;
            return (uint)(seconds / 600);
        }

        return sequence.Value & 0x0000FFFF;
    }
}
