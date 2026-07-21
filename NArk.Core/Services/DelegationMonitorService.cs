using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NArk.Core.Helpers;
using NArk.Core.Assets;
using NArk.Core.Models;
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
    IFeeEstimator feeEstimator,
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
        // Get signing descriptor from the contract's user key
        var signerDescriptor = contract switch
        {
            ArkDelegateContract dc => dc.User,
            _ => throw new InvalidOperationException($"Unsupported contract type for delegation: {contract.Type}")
        };

        var (signer, signerPubKey) = await walletProvider.GetSignerAndPubKeyAsync(walletId, signerDescriptor);

        // Build the intent message. Field names must be snake_case (Messages.RegisterIntentMessage's
        // JsonPropertyName values) to match arkd/Fulmine's Go RegisterMessage struct tags — a
        // camelCase mismatch silently unmarshals to zero values rather than erroring. The delegator
        // schedules its registration task at ValidAt (time.Unix(message.ValidAt, 0) in Fulmine's
        // delegator_service.go) and rejects ValidAt == 0 outright ("invalid valid at"); "now" makes
        // it register immediately, since a past/zero-delay schedule runs right away.
        var intentMessage = JsonSerializer.Serialize(new Messages.RegisterIntentMessage
        {
            Type = "register",
            OnchainOutputsIndexes = [],
            ValidAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ExpireAt = 0,
            CosignersPublicKeys = [Convert.ToHexString(signerPubKey.ToBytes()).ToLowerInvariant()]
        });

        // Build intent proof PSBT (BIP322-style)
        var intentCoin = new ArkCoin(
            walletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            outpoint, vtxo.TxOut, signerDescriptor, intentScriptBuilder,
            null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);

        var intentPsbt = IntentProofHelper.CreateBip322Psbt(intentMessage, serverInfo.Network, intentCoin);

        // CreateBip322Psbt leaves a bare 0-value OP_RETURN placeholder output (the base BIP322
        // proof shape). Replace it with the real send-to-self destination — same script as the
        // original VTXO, minus the operator's intent fee (arkd's INTENT_INSUFFICIENT_FEE rejects
        // a same-amount send-to-self as paying zero fee) — mirroring
        // IntentGenerationService.CreateIntent's Outputs.RemoveAt(0)/AddRange(outputs).
        var destinationAddress = vtxo.TxOut.ScriptPubKey.GetDestinationAddress(serverInfo.Network)
            ?? throw new InvalidOperationException($"Cannot derive destination address for script {vtxo.Script}");
        var destinationOutput = new ArkTxOut(ArkTxOutType.Vtxo, vtxo.TxOut.Value, destinationAddress);
        var fee = await feeEstimator.EstimateFeeAsync([intentCoin], [destinationOutput]);
        var destinationAmount = vtxo.TxOut.Value - Money.Satoshis(fee);

        var proofGtx = intentPsbt.GetGlobalTransaction();
        proofGtx.Outputs.RemoveAt(0);
        proofGtx.Outputs.Add(new TxOut(destinationAmount, vtxo.TxOut.ScriptPubKey));
        intentPsbt = PSBT.FromTransaction(proofGtx, serverInfo.Network).UpdateFrom(intentPsbt);

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

    private static PSBT CreateForfeitTransaction(ArkServerInfo serverInfo, ArkCoin coin)
    {
        // Matches the 2-output shape ArkTransactionBuilder.ConstructForfeitTx produces
        // (payment to the operator's forfeit address + P2A anchor) — the delegator's forfeit
        // validation requires it. Built as a raw transaction rather than through
        // TransactionBuilder.Send()/BuildPSBT(): at delegation time the batch connector
        // doesn't exist yet (the delegator attaches it later when it joins a batch), so the
        // declared forfeit amount (coin.Amount + assumed dust) is intentionally larger than
        // this single input — TransactionBuilder's balance check would reject that.
        var hasLocktime = coin.LockTime is not null && coin.LockTime != LockTime.Zero;
        var vtxoSequence = coin.Sequence
            ?? (hasLocktime ? new Sequence(0xFFFFFFFE) : new Sequence(0xFFFFFFFF));

        var tx = serverInfo.Network.CreateTransaction();
        tx.Version = 3;
        tx.LockTime = coin.LockTime ?? LockTime.Zero;
        tx.Inputs.Add(new TxIn(coin.Outpoint) { Sequence = vtxoSequence });
        tx.Outputs.Add(new TxOut(coin.Amount + serverInfo.Dust, serverInfo.ForfeitAddress));
        tx.Outputs.Add(new TxOut(Money.Zero, Constants.ArkP2A));

        var forfeitTx = PSBT.FromTransaction(tx, serverInfo.Network);
        forfeitTx.Settings.AutomaticUTXOTrimming = false;
        forfeitTx.AddCoins(coin);
        return forfeitTx;
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
