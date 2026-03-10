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

        var signerPubKey = await signer.GetPubKey(signerDescriptor);

        // Build the intent message
        var intentMessage = JsonSerializer.Serialize(new
        {
            type = "register",
            cosignersPublicKeys = new[] { Convert.ToHexString(signerPubKey.ToBytes()).ToLowerInvariant() },
            validAt = 0,
            expireAt = 0
        });

        // Build intent proof PSBT (BIP322-style)
        var intentCoin = new ArkCoin(
            walletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            outpoint, vtxo.TxOut, signerDescriptor, intentScriptBuilder,
            null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);

        var intentPsbt = CreateBip322Proof(intentMessage, serverInfo.Network, intentCoin);
        var intentGtx = intentPsbt.GetGlobalTransaction();

        // Clone intent coin for input[0] (BIP322 toSpend reference) — matches IntentGenerationService pattern
        var bip322Coin = new ArkCoin(intentCoin);
        bip322Coin.TxOut = intentPsbt.Inputs[0].GetTxOut()!;
        bip322Coin.Outpoint = intentPsbt.Inputs[0].PrevOut;

        var intentPrecomputed = intentGtx.PrecomputeTransactionData(
            [bip322Coin.TxOut, intentCoin.TxOut]);

        // Reconstruct PSBT from the global tx so both inputs have coin data
        intentPsbt = PSBT.FromTransaction(intentGtx, serverInfo.Network).UpdateFrom(intentPsbt);

        // Sign both inputs: input[0] = BIP322 toSpend ref, input[1] = real VTXO
        await PsbtHelpers.SignAndFillPsbt(signer, bip322Coin, intentPsbt, intentPrecomputed,
            cancellationToken: CancellationToken.None);
        await PsbtHelpers.SignAndFillPsbt(signer, intentCoin, intentPsbt, intentPrecomputed,
            cancellationToken: CancellationToken.None);

        // Build forfeit tx using the delegate path, signed with SIGHASH_ALL|ANYONECANPAY
        var forfeitCoin = new ArkCoin(
            walletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            outpoint, vtxo.TxOut, signerDescriptor, forfeitScriptBuilder,
            null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);

        var forfeitTx = CreateForfeitTransaction(serverInfo.Network, forfeitCoin);
        var forfeitPrecomputed = forfeitTx.GetGlobalTransaction()
            .PrecomputeTransactionData([forfeitCoin.TxOut]);

        await PsbtHelpers.SignAndFillPsbt(signer, forfeitCoin, forfeitTx, forfeitPrecomputed,
            TaprootSigHash.All | TaprootSigHash.AnyoneCanPay, CancellationToken.None);

        await delegatorProvider.DelegateAsync(
            intentMessage,
            intentPsbt.ToBase64(),
            [forfeitTx.ToBase64()]);
    }

    private static PSBT CreateBip322Proof(string message, Network network, ArkCoin coin)
    {
        var messageHash = HashHelpers.CreateTaggedMessageHash("ark-intent-proof-message", message);

        var toSpend = network.CreateTransaction();
        toSpend.Version = 0;
        toSpend.LockTime = 0;
        toSpend.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, 0xFFFFFFFF),
            new Script(OpcodeType.OP_0, Op.GetPushOp(messageHash)))
        {
            Sequence = 0,
            WitScript = WitScript.Empty,
        });
        toSpend.Outputs.Add(new TxOut(Money.Zero, coin.ScriptPubKey));

        var toSign = network.CreateTransaction();
        toSign.Version = 2;
        toSign.LockTime = 0;
        toSign.Inputs.Add(new TxIn(new OutPoint(toSpend.GetHash(), 0)) { Sequence = 0 });
        toSign.Inputs.Add(new TxIn(coin.Outpoint) { Sequence = 0 });
        toSign.Outputs.Add(new TxOut(Money.Zero, new Script(OpcodeType.OP_RETURN)));

        var psbt = PSBT.FromTransaction(toSign, network);
        psbt.Settings.AutomaticUTXOTrimming = false;
        psbt.AddTransactions(toSpend);
        psbt.AddCoins(coin);
        return psbt;
    }

    private static PSBT CreateForfeitTransaction(Network network, ArkCoin coin)
    {
        var tx = network.CreateTransaction();
        tx.Version = 2;
        tx.LockTime = 0;
        tx.Inputs.Add(new TxIn(coin.Outpoint) { Sequence = 0 });
        tx.Outputs.Add(new TxOut(Money.Zero, new Script(OpcodeType.OP_RETURN)));

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
