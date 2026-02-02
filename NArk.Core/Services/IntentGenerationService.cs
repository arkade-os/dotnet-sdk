using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;

using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Helpers;
using NArk.Core.Models;
using NArk.Core.Models.Options;
using NArk.Core.Transport;
using NArk.Core.Extensions;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Core.Services;

public class IntentGenerationService(
    IClientTransport clientTransport,
    IFeeEstimator feeEstimator,
    ICoinService coinService,
    IWalletProvider walletProvider,
    IIntentStorage intentStorage,
    ISafetyService safetyService,
    IContractStorage contractStorage,
    IVtxoStorage vtxoStorage,
    IIntentScheduler intentScheduler,
    IOptions<IntentGenerationServiceOptions>? options = null,
    ILogger<IntentGenerationService>? logger = null
) : IIntentGenerationService, IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _generationTask;
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        logger?.LogInformation("Starting intent generation service");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        _generationTask = DoGenerationLoop(multiToken.Token);
        return Task.CompletedTask;
    }

    private async Task DoGenerationLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var unspentVtxos =
                    await vtxoStorage.GetVtxos(
                        includeSpent: false,
                        cancellationToken: token);
                var scriptsWithUnspentVtxos = unspentVtxos.Select(v => v.Script).ToHashSet();
                var contracts =
                    (await contractStorage.GetContracts(scripts: scriptsWithUnspentVtxos.ToArray(), cancellationToken: token))
                    .GroupBy(c => c.WalletIdentifier);

                
                
                foreach (var walletContracts in contracts)
                {;
                    var walletVtxos =
                        unspentVtxos.Where(v => walletContracts.Any(c => c.Script == v.Script)).ToArray();

                    List<ArkCoin> coins = [];

                    foreach (var vtxo in walletVtxos)
                    {
                        try
                        {
                            var coin = await coinService.GetCoin(walletContracts.Single(entity => entity.Script == vtxo.Script), vtxo, token);
                            coins.Add(coin);
                        }
                        catch (AdditionalInformationRequiredException ex)
                        {
                            logger?.LogDebug(0, ex, "Skipping vtxo {TxId}:{Index} - requires additional information (likely VHTLC contract)", vtxo.TransactionId, vtxo.TransactionOutputIndex);
                        }
                    }

                    var intentSpecs =
                        await intentScheduler.GetIntentsToSubmit([.. coins], token);

                    foreach (var intentSpec in intentSpecs)
                    {
                        await GenerateIntentFromSpec(walletContracts.Key, intentSpec, false, token);
                    }
                }

                await Task.Delay(options?.Value.PollInterval ?? TimeSpan.FromMinutes(5), token);
            }
        }
        catch (OperationCanceledException)
        {
            logger?.LogDebug("Intent generation loop cancelled");
        }
        catch (Exception ex)
        {
            logger?.LogError(0, ex, "Intent generation loop failed with unexpected error");
        }
    }

    private async Task<string?> GenerateIntentFromSpec(string walletId, ArkIntentSpec intentSpec, bool force = false, CancellationToken token = default)
    {
        logger?.LogDebug("Generating intent from spec for wallet {WalletId} with {CoinCount} coins", walletId, intentSpec.Coins.Length);
        ArkServerInfo serverInfo = await clientTransport.GetServerInfoAsync(token);
        var outputsSum = intentSpec.Outputs.Sum(o => o.Value);
        var inputsSum = intentSpec.Coins.Sum(c => c.Amount);
        var fee = await feeEstimator.EstimateFeeAsync(intentSpec, token);

        if (outputsSum - inputsSum < fee)
        {
            logger?.LogWarning("Intent generation failed for wallet {WalletId}: fees not properly considered, missing {MissingAmount} sats", walletId, inputsSum + fee - outputsSum);
            throw new InvalidOperationException(
                $"Scheduler is not considering fees properly, missing fees by {inputsSum + fee - outputsSum} sats");
        }

        var overlappingIntents =
            await intentStorage.GetIntents(
                walletIds: [walletId],
                containingInputs: [.. intentSpec.Coins.Select(c => c.Outpoint)],
                states: [ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch],
                cancellationToken: token);
        if (overlappingIntents.Count != 0)
        {
            if (!force)
            {
                logger?.LogDebug("Intent generation skipped for wallet {WalletId}: overlapping intents exist", walletId);
                return null;
            }
            else
            {
                logger?.LogWarning("Forcing intent generation for wallet {WalletId}: cancelling {OverlappingIntentCount} overlapping intents", walletId, overlappingIntents.Count);
                foreach (var intent in overlappingIntents)
                {
                    await using var intentLock =
                        await safetyService.LockKeyAsync($"intent::{intent.IntentTxId}", CancellationToken.None);
                    var intentAfterLock =
                        (await intentStorage.GetIntents(intentTxIds: [intent.IntentTxId], cancellationToken: CancellationToken.None)).FirstOrDefault()
                        ?? throw new Exception("Should not happen, intent disappeared from storage mid-action");
                    await intentStorage.SaveIntent(intentAfterLock.WalletId,
                        intentAfterLock with { State = ArkIntentState.Cancelled }, CancellationToken.None);
                }
            }

        }

        var addrProvider = await walletProvider.GetAddressProviderAsync(walletId, token)
                           ?? throw new InvalidOperationException("Wallet belonging to the intent was not found!");
        var singingDescriptor =
            intentSpec
                .Coins
                .Where(c => c.SignerDescriptor is not null)
                .Select(c => c.SignerDescriptor)
                .FirstOrDefault() ??
            await addrProvider.GetNextSigningDescriptor(token);

        var (RegisterTx, Delete, RegisterMessage, DeleteMessage) = await CreateIntents(
            serverInfo.Network,
            new HashSet<ECPubKey>([
                singingDescriptor.ToPubKey()
            ]),
            intentSpec.ValidFrom,
            intentSpec.ValidUntil,
            intentSpec.Coins,
            intentSpec.Outputs,
            token
        );

        var intentTxId = RegisterTx.GetGlobalTransaction().GetHash().ToString();
        await intentStorage.SaveIntent(walletId,
            new ArkIntent(intentTxId, null, walletId, ArkIntentState.WaitingToSubmit,
                intentSpec.ValidFrom, intentSpec.ValidUntil, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                RegisterTx.ToBase64(), RegisterMessage, Delete.ToBase64(),
                DeleteMessage, null, null, null,
                [.. intentSpec.Coins.Select(c => c.Outpoint)],
                singingDescriptor.ToString()), token);

        logger?.LogInformation("Generated intent {IntentTxId} for wallet {WalletId}", intentTxId, walletId);
        return intentTxId;
    }

    private async Task<PSBT> CreateIntent(string message, Network network, ArkCoin[] inputs,
        IReadOnlyCollection<TxOut>? outputs, CancellationToken cancellationToken = default)
    {
        var firstInput = inputs.First();
        var toSignTx =
            CreatePsbt(
                firstInput.ScriptPubKey,
                network,
                message,
                2U,
                0U,
                0U,
                [.. inputs]
            );

        var toSignGTx = toSignTx.GetGlobalTransaction();
        if (outputs is not null && outputs.Count != 0)
        {
            toSignGTx.Outputs.RemoveAt(0);
            toSignGTx.Outputs.AddRange(outputs);
        }

        inputs = [new ArkCoin(firstInput), .. inputs];
        inputs[0].TxOut = toSignTx.Inputs[0].GetTxOut();
        inputs[0].Outpoint = toSignTx.Inputs[0].PrevOut;

        var precomputedTransactionData = toSignGTx.PrecomputeTransactionData(inputs.Select(i => i.TxOut).ToArray());

        toSignTx = PSBT.FromTransaction(toSignGTx, network).UpdateFrom(toSignTx);

        foreach (var coin in inputs)
        {
            var signer = await walletProvider.GetSignerAsync(coin.WalletIdentifier, cancellationToken);
            await PsbtHelpers.SignAndFillPsbt(signer!, coin, toSignTx, precomputedTransactionData, cancellationToken: cancellationToken);
        }

        return toSignTx;
    }

    private static PSBT CreatePsbt(
        Script pkScript,
        Network network,
        string message,
        uint version = 0, uint lockTime = 0, uint sequence = 0, Coin[]? fundProofOutputs = null)
    {
        var messageHash = HashHelpers.CreateTaggedMessageHash("ark-intent-proof-message", message);

        var toSpend = network.CreateTransaction();
        toSpend.Version = 0;
        toSpend.LockTime = 0;
        toSpend.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, 0xFFFFFFFF), new Script(OpcodeType.OP_0, Op.GetPushOp(messageHash)))
        {
            Sequence = 0,
            WitScript = WitScript.Empty,
        });
        toSpend.Outputs.Add(new TxOut(Money.Zero, pkScript));
        var toSpendTxId = toSpend.GetHash();
        var toSign = network.CreateTransaction();
        toSign.Version = version;
        toSign.LockTime = lockTime;
        toSign.Inputs.Add(new TxIn(new OutPoint(toSpendTxId, 0))
        {
            Sequence = sequence
        });

        fundProofOutputs ??= [];

        foreach (var input in fundProofOutputs)
        {
            toSign.Inputs.Add(new TxIn(input.Outpoint, Script.Empty)
            {
                Sequence = sequence,
            });
        }
        toSign.Outputs.Add(new TxOut(Money.Zero, new Script(OpcodeType.OP_RETURN)));
        var psbt = PSBT.FromTransaction(toSign, network);
        psbt.Settings.AutomaticUTXOTrimming = false;
        psbt.AddTransactions(toSpend);
        psbt.AddCoins(fundProofOutputs.Cast<ICoin>().ToArray());
        return psbt;
    }

    private async Task<(PSBT RegisterTx, PSBT Delete, string RegisterMessage, string DeleteMessage)> CreateIntents(
        Network network,
        IReadOnlySet<ECPubKey> cosigners,
        DateTimeOffset validAt,
        DateTimeOffset expireAt,
        IReadOnlyCollection<ArkCoin> inputCoins,
        IReadOnlyCollection<ArkTxOut>? outs = null,
        CancellationToken cancellationToken = default
    )
    {
        var msg = new Messages.RegisterIntentMessage
        {
            Type = "register",
            OnchainOutputsIndexes = outs?.Select((x, i) => (x, i)).Where(o => o.x.Type == ArkTxOutType.Onchain).Select((_, i) => i).ToArray() ?? [],
            ValidAt = validAt.ToUnixTimeSeconds(),
            ExpireAt = expireAt.ToUnixTimeSeconds(),
            CosignersPublicKeys = cosigners.Select(c => c.ToBytes().ToHexStringLower()).ToArray()
        };

        var deleteMsg = new Messages.DeleteIntentMessage()
        {
            Type = "delete",
            ExpireAt = expireAt.ToUnixTimeSeconds()
        };
        var message = JsonSerializer.Serialize(msg);
        var deleteMessage = JsonSerializer.Serialize(deleteMsg);

        return (
            await CreateIntent(message, network, inputCoins.ToArray(), outs?.Cast<TxOut>().ToArray(), cancellationToken),
            await CreateIntent(deleteMessage, network, inputCoins.ToArray(), null, cancellationToken),
            message,
            deleteMessage);
    }

    public async ValueTask DisposeAsync()
    {
        logger?.LogDebug("Disposing intent generation service");
        await _shutdownCts.CancelAsync();

        if (_generationTask is not null)
            await _generationTask;

        logger?.LogInformation("Intent generation service disposed");
    }

    public async Task<string> GenerateManualIntent(string walletId, ArkIntentSpec spec, bool force = false, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Generating manual intent for wallet {WalletId}", walletId);
        var intentTxId = await GenerateIntentFromSpec(walletId, spec, force, cancellationToken);

        if (intentTxId is null)
        {
            logger?.LogWarning("Manual intent generation failed for wallet {WalletId}: pending intents exist", walletId);
            throw new InvalidOperationException("Could not create intent, pending intents exist");
        }

        return intentTxId;
    }
}

public interface IIntentGenerationService
{
    Task<string> GenerateManualIntent(string walletId, ArkIntentSpec spec,
        bool force = false, CancellationToken cancellationToken = default);
}