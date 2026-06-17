using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Helpers;
using NArk.Core.Assets;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Core.Services;

/// <summary>
/// Hosted service that monitors VTXO changes and automatically delegates
/// new VTXOs at delegate contracts to the configured delegator service.
/// </summary>
public class DelegationMonitorService(
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IEnumerable<IDelegationTransformer> transformers,
    IDelegatorProvider delegatorProvider,
    IWalletProvider walletProvider,
    IClientTransport clientTransport,
    ILogger<DelegationMonitorService>? logger = null) : IHostedService, IDisposable
{
    private readonly HashSet<OutPoint> _delegatedOutpoints = new();
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private ECPubKey? _delegatePubkey;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        vtxoStorage.VtxosChanged += OnVtxosChanged;
        logger?.LogInformation("DelegationMonitorService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        vtxoStorage.VtxosChanged -= OnVtxosChanged;
        logger?.LogInformation("DelegationMonitorService stopped");
        return Task.CompletedTask;
    }

    private async void OnVtxosChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            if (vtxo.IsSpent())
                return;

            await ProcessVtxoAsync(vtxo);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error processing VTXO {Outpoint} for delegation",
                $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}");
        }
    }

    private async Task ProcessVtxoAsync(ArkVtxo vtxo)
    {
        await _processingLock.WaitAsync();
        try
        {
            var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), vtxo.TransactionOutputIndex);

            if (_delegatedOutpoints.Contains(outpoint))
                return;

            var contracts = await contractStorage.GetContracts(scripts: [vtxo.Script]);
            var contract = contracts.FirstOrDefault();
            if (contract is null)
                return;

            var walletId = contract.WalletIdentifier;
            using var _walletScope = logger?.BeginScope(("WalletId", walletId));
            var serverInfo = await clientTransport.GetServerInfoAsync();
            var parsed = ArkContractParser.Parse(contract.Type, contract.AdditionalData, serverInfo.Network);
            if (parsed is null)
                return;

            var delegatePubkey = await GetDelegatePubkeyAsync();

            IDelegationTransformer? matchingTransformer = null;
            foreach (var transformer in transformers)
            {
                if (await transformer.CanDelegate(walletId, parsed, delegatePubkey))
                {
                    matchingTransformer = transformer;
                    break;
                }
            }

            if (matchingTransformer is null)
                return;

            logger?.LogInformation("Delegating VTXO {Outpoint} from wallet {WalletId}", outpoint, walletId);

            var (intentScript, forfeitScript) = matchingTransformer.GetDelegationScriptBuilders(parsed);
            await BuildAndSendDelegationAsync(walletId, parsed, vtxo, outpoint, intentScript, forfeitScript, serverInfo);

            _delegatedOutpoints.Add(outpoint);
            logger?.LogInformation("Successfully delegated VTXO {Outpoint}", outpoint);
        }
        finally
        {
            _processingLock.Release();
        }
    }

    private async Task BuildAndSendDelegationAsync(
        string walletId,
        ArkContract contract,
        ArkVtxo vtxo,
        OutPoint outpoint,
        ScriptBuilder intentScriptBuilder,
        ScriptBuilder forfeitScriptBuilder,
        ArkServerInfo serverInfo)
    {
        var signer = await walletProvider.GetSignerAsync(walletId)
            ?? throw new InvalidOperationException($"No signer for wallet {walletId}");

        // Get signing descriptor from the contract's user key
        var signerDescriptor = contract switch
        {
            Core.Contracts.ArkDelegateContract dc => dc.User,
            _ => throw new InvalidOperationException($"Unsupported contract type for delegation: {contract.Type}")
        };

        // The DELEGATOR cosigns the refreshed VTXO tree on the owner's behalf (the owner is offline by
        // the time of the batch), so the delegator's key — not the owner's — must be the declared
        // cosigner. Otherwise the batch tree signing would require the absent owner.
        var delegatePubkey = await GetDelegatePubkeyAsync();

        // Build the intent message
        var intentMessage = JsonSerializer.Serialize(new
        {
            type = "register",
            cosignersPublicKeys = new[] { Convert.ToHexString(delegatePubkey.ToBytes()).ToLowerInvariant() },
            validAt = 0,
            expireAt = 0
        });

        // Build intent proof PSBT (BIP322-style)
        var intentCoin = new ArkCoin(
            walletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            outpoint, vtxo.TxOut, signerDescriptor, intentScriptBuilder,
            null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);

        var intentPsbt = IntentProofHelper.CreateBip322Psbt(intentMessage, serverInfo.Network, intentCoin);

        // Build asset packet if the VTXO carries assets — delegation is send-to-self (vout=0)
        if (vtxo.Assets is { Count: > 0 } vtxoAssets)
        {
            var assetInputs = vtxoAssets
                .Select(a => (a.AssetId, vin: (ushort)1, a.Amount))
                .ToList();
            var assetPacketTxOut = AssetPacketBuilder.Build(assetInputs, null, changeVout: 0);
            if (assetPacketTxOut is not null)
            {
                var gtx = intentPsbt.GetGlobalTransaction();
                gtx.Outputs.Add(assetPacketTxOut);
                intentPsbt = PSBT.FromTransaction(gtx, serverInfo.Network).UpdateFrom(intentPsbt);
            }
        }

        intentPsbt = await IntentProofHelper.SignBip322Proof(intentPsbt, intentCoin, signer, serverInfo.Network);

        // Build forfeit tx using the delegate path, signed with SIGHASH_ALL|ANYONECANPAY
        var forfeitCoin = new ArkCoin(
            walletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            outpoint, vtxo.TxOut, signerDescriptor, forfeitScriptBuilder,
            null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);

        var forfeitTx = CreateForfeitTransaction(serverInfo, forfeitCoin);
        var forfeitPrecomputed = forfeitTx.GetGlobalTransaction()
            .PrecomputeTransactionData([forfeitCoin.TxOut]);

        await PsbtHelpers.SignAndFillPsbt(signer, forfeitCoin, forfeitTx, forfeitPrecomputed,
            TaprootSigHash.All | TaprootSigHash.AnyoneCanPay, CancellationToken.None);

        await delegatorProvider.DelegateAsync(
            intentMessage,
            intentPsbt.ToBase64(),
            [forfeitTx.ToBase64()]);
    }

    // Builds the partial forfeit the delegator co-signs and later submits in a batch: a TRUC (v3) tx
    // spending the VTXO to the operator's forfeit address (VTXO amount + Dust) plus a P2A anchor, with a
    // single input. The delegator appends the connector input at batch time — signing the VTXO input
    // with ANYONECANPAY|ALL (done by the caller) keeps this signature valid when that happens.
    private static PSBT CreateForfeitTransaction(ArkServerInfo serverInfo, ArkCoin coin)
    {
        var network = serverInfo.Network;
        var tx = network.CreateTransaction();
        tx.Version = 3;
        tx.LockTime = coin.LockTime ?? LockTime.Zero;
        tx.Inputs.Add(new TxIn(coin.Outpoint) { Sequence = coin.Sequence ?? Sequence.Final });
        tx.Outputs.Add(new TxOut(coin.Amount + serverInfo.Dust, serverInfo.ForfeitAddress.ScriptPubKey));
        tx.Outputs.Add(new TxOut(Money.Zero, Script.FromHex("51024e73")));

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Settings.AutomaticUTXOTrimming = false;
        psbt.AddCoins(coin);
        return psbt;
    }

    private async Task<ECPubKey> GetDelegatePubkeyAsync()
    {
        if (_delegatePubkey is not null)
            return _delegatePubkey;

        var info = await delegatorProvider.GetDelegatorInfoAsync();
        _delegatePubkey = ECPubKey.Create(Convert.FromHexString(info.Pubkey));
        return _delegatePubkey;
    }

    public void Dispose()
    {
        vtxoStorage.VtxosChanged -= OnVtxosChanged;
        _processingLock.Dispose();
    }
}
