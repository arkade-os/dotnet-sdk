using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Services;

/// <summary>
/// Synchronizes boarding UTXOs from the Bitcoin blockchain via Esplora API.
/// Queries onchain UTXOs for boarding contract addresses and upserts them into VTXO storage.
/// </summary>
public class BoardingUtxoSyncService
{
    private readonly IContractStorage _contractStorage;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IClientTransport _clientTransport;
    private readonly HttpClient _httpClient;
    private readonly ILogger<BoardingUtxoSyncService>? _logger;

    public BoardingUtxoSyncService(
        IContractStorage contractStorage,
        IVtxoStorage vtxoStorage,
        IClientTransport clientTransport,
        HttpClient httpClient,
        ILogger<BoardingUtxoSyncService>? logger = null)
    {
        _contractStorage = contractStorage;
        _vtxoStorage = vtxoStorage;
        _clientTransport = clientTransport;
        _httpClient = httpClient;
        _logger = logger;
    }

    public BoardingUtxoSyncService(
        IContractStorage contractStorage,
        IVtxoStorage vtxoStorage,
        IClientTransport clientTransport,
        Uri esploraBaseUri,
        ILogger<BoardingUtxoSyncService>? logger = null)
        : this(contractStorage, vtxoStorage, clientTransport, new HttpClient { BaseAddress = esploraBaseUri }, logger)
    {
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        var network = serverInfo.Network;
        var boardingExitDelay = serverInfo.BoardingExit;

        var boardingContracts = await _contractStorage.GetContracts(
            contractTypes: [ArkBoardingContract.ContractType],
            cancellationToken: cancellationToken);

        if (boardingContracts.Count == 0)
        {
            _logger?.LogDebug("No boarding contracts found, nothing to sync");
            return;
        }

        _logger?.LogInformation("Syncing {Count} boarding contracts", boardingContracts.Count);

        foreach (var contractEntity in boardingContracts)
        {
            try
            {
                await SyncContractAsync(contractEntity, network, boardingExitDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to sync boarding contract with script {Script}", contractEntity.Script);
            }
        }
    }

    private async Task SyncContractAsync(
        ArkContractEntity contractEntity,
        Network network,
        Sequence boardingExitDelay,
        CancellationToken cancellationToken)
    {
        // Derive the P2TR address from the stored scriptPubKey hex
        var script = Script.FromHex(contractEntity.Script);
        var address = script.GetDestinationAddress(network);

        if (address is null)
        {
            _logger?.LogWarning("Could not derive address from script {Script}", contractEntity.Script);
            return;
        }

        var addressStr = address.ToString();
        _logger?.LogDebug("Querying Esplora for UTXOs at address {Address}", addressStr);

        // Query Esplora for UTXOs
        var utxos = await _httpClient.GetFromJsonAsync<EsploraUtxo[]>(
            $"address/{addressStr}/utxo", cancellationToken);

        if (utxos is null)
        {
            _logger?.LogWarning("Esplora returned null for address {Address}", addressStr);
            return;
        }

        // Get existing VTXOs for this script to detect spent ones
        var existingVtxos = await _vtxoStorage.GetVtxos(
            scripts: [contractEntity.Script],
            cancellationToken: cancellationToken);

        var onchainOutpoints = new HashSet<string>();

        foreach (var utxo in utxos)
        {
            // Skip unconfirmed UTXOs
            if (utxo.Status is not { Confirmed: true })
            {
                _logger?.LogDebug("Skipping unconfirmed UTXO {Txid}:{Vout}", utxo.Txid, utxo.Vout);
                continue;
            }

            onchainOutpoints.Add($"{utxo.Txid}:{utxo.Vout}");

            var createdAt = utxo.Status.BlockTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(utxo.Status.BlockTime)
                : DateTimeOffset.UtcNow;

            var expiresAt = ComputeExpiresAt(createdAt, boardingExitDelay);

            var arkVtxo = new ArkVtxo(
                Script: contractEntity.Script,
                TransactionId: utxo.Txid,
                TransactionOutputIndex: (uint)utxo.Vout,
                Amount: (ulong)utxo.Value,
                SpentByTransactionId: null,
                SettledByTransactionId: null,
                Swept: false,
                CreatedAt: createdAt,
                ExpiresAt: expiresAt,
                ExpiresAtHeight: utxo.Status.BlockHeight > 0
                    ? (uint)(utxo.Status.BlockHeight + GetBlockCount(boardingExitDelay))
                    : null,
                Unrolled: true);

            await _vtxoStorage.UpsertVtxo(arkVtxo, cancellationToken);
            _logger?.LogDebug("Upserted boarding VTXO {Txid}:{Vout} ({Amount} sats)",
                utxo.Txid, utxo.Vout, utxo.Value);
        }

        // Mark spent: existing unspent VTXOs that are no longer in the Esplora response
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

        if (sequence.IsRBF)
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

    internal class EsploraUtxo
    {
        [JsonPropertyName("txid")]
        public string Txid { get; set; } = string.Empty;

        [JsonPropertyName("vout")]
        public int Vout { get; set; }

        [JsonPropertyName("value")]
        public long Value { get; set; }

        [JsonPropertyName("status")]
        public EsploraUtxoStatus? Status { get; set; }
    }

    internal class EsploraUtxoStatus
    {
        [JsonPropertyName("confirmed")]
        public bool Confirmed { get; set; }

        [JsonPropertyName("block_height")]
        public long BlockHeight { get; set; }

        [JsonPropertyName("block_time")]
        public long BlockTime { get; set; }
    }
}
