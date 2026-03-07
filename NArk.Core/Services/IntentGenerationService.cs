using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
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
using NArk.Core.Assets;
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
                await RunIntentGenerationCycle(token);

                var pollInterval = options?.Value.PollInterval ?? TimeSpan.FromMinutes(5);
                await Task.Delay(pollInterval, token);
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

    private async Task RunIntentGenerationCycle(CancellationToken token)
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
        {
            var walletId = walletContracts.Key;

            // Check for BatchInProgress intents. A batch resolves in seconds, so if an intent
            // has been stuck in BatchInProgress for over 5 minutes, it's orphaned — cancel it
            // and let the cycle proceed. Otherwise, skip this wallet and wait for the batch to
            // resolve naturally (it will fire another VTXO change event).
            var inProgressIntents = await intentStorage.GetIntents(
                walletIds: [walletId],
                states: [ArkIntentState.BatchInProgress],
                cancellationToken: token);
            if (inProgressIntents.Count > 0)
            {
                var staleThreshold = TimeSpan.FromMinutes(5);
                var staleIntents = inProgressIntents
                    .Where(i => DateTimeOffset.UtcNow - i.UpdatedAt > staleThreshold)
                    .ToList();

                if (staleIntents.Count > 0)
                {
                    foreach (var stale in staleIntents)
                    {
                        // Never cancel an intent that has a commitment tx — the batch succeeded
                        if (stale.CommitmentTransactionId is not null)
                        {
                            logger?.LogInformation("Stale BatchInProgress intent {IntentTxId} has commitment tx {CommitmentTx} — marking as succeeded",
                                stale.IntentTxId, stale.CommitmentTransactionId);
                            await intentStorage.SaveIntent(walletId, stale with
                            {
                                State = ArkIntentState.BatchSucceeded,
                                CancellationReason = null,
                                UpdatedAt = DateTimeOffset.UtcNow
                            }, token);
                            continue;
                        }

                        logger?.LogWarning("Cancelling stuck BatchInProgress intent {IntentTxId} for wallet {WalletId} (stuck for {Duration})",
                            stale.IntentTxId, walletId, DateTimeOffset.UtcNow - stale.UpdatedAt);
                        await intentStorage.SaveIntent(walletId, stale with
                        {
                            State = ArkIntentState.Cancelled,
                            CancellationReason = "Stuck in BatchInProgress — batch session likely lost",
                            UpdatedAt = DateTimeOffset.UtcNow
                        }, token);
                    }
                }

                // If any non-stale BatchInProgress intents remain, skip this wallet
                if (staleIntents.Count < inProgressIntents.Count)
                {
                    logger?.LogDebug("Skipping intent generation for wallet {WalletId} - {Count} intent(s) with batch in progress",
                        walletId, inProgressIntents.Count - staleIntents.Count);
                    continue;
                }
            }

            // Also skip wallets with active WaitingToSubmit or WaitingForBatch intents — they are
            // already being processed and will resolve naturally. Re-generating would cancel them.
            var activeIntents = await intentStorage.GetIntents(
                walletIds: [walletId],
                states: [ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch],
                cancellationToken: token);
            if (activeIntents.Count > 0)
            {
                logger?.LogDebug("Skipping intent generation for wallet {WalletId} - {Count} active intent(s) pending",
                    walletId, activeIntents.Count);
                continue;
            }

            // Exclude VTXOs that were consumed by recently-succeeded batches but haven't
            // been marked as spent yet (VTXO poll may still be in progress). This prevents
            // the race condition where we generate intents using already-spent VTXOs.
            var recentSucceeded = await intentStorage.GetIntents(
                walletIds: [walletId],
                states: [ArkIntentState.BatchSucceeded],
                cancellationToken: token);
            var recentlySpentOutpoints = recentSucceeded
                .SelectMany(i => i.IntentVtxos)
                .ToHashSet();

            var walletVtxos = unspentVtxos
                .Where(v => walletContracts.Any(c => c.Script == v.Script))
                .Where(v => !recentlySpentOutpoints.Contains(new OutPoint(uint256.Parse(v.TransactionId), v.TransactionOutputIndex)))
                .ToArray();

            if (walletVtxos.Length == 0 && recentlySpentOutpoints.Count > 0)
            {
                logger?.LogDebug("Skipping intent generation for wallet {WalletId} - all VTXOs consumed by recent batch (VTXO poll pending)",
                    walletId);
                continue;
            }

            List<ArkCoin> coins = [];

            foreach (var vtxo in walletVtxos)
            {
                try
                {
                    var coin = await coinService.GetCoin(walletContracts.First(entity => entity.Script == vtxo.Script), vtxo, token);
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
                try
                {
                    await GenerateIntentFromSpec(walletContracts.Key, intentSpec, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger?.LogError(ex,
                        "Failed to generate intent for wallet {WalletId} — skipping this wallet for this cycle",
                        walletContracts.Key);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Cancels any active intents that share VTXOs with the specified outpoints.
    /// For intents already submitted to arkd (WaitingForBatch), also deletes them from the server.
    /// This enforces the invariant: no two active intents may share any VTXO.
    /// </summary>
    private async Task CancelOverlappingIntentsAsync(string walletId, OutPoint[] outpoints, CancellationToken token)
    {
        // Active states: intents that are still "in play" and could conflict
        var activeStates = new[] { ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch, ArkIntentState.BatchInProgress };

        var overlappingIntents = await intentStorage.GetIntents(
            walletIds: [walletId],
            containingInputs: outpoints,
            states: activeStates,
            cancellationToken: token);

        if (overlappingIntents.Count == 0)
            return;

        logger?.LogInformation("Cancelling {OverlappingIntentCount} overlapping intents for wallet {WalletId}", overlappingIntents.Count, walletId);

        foreach (var intent in overlappingIntents)
        {
            await using var intentLock =
                await safetyService.LockKeyAsync($"intent::{intent.IntentTxId}", CancellationToken.None);

            var intentAfterLock =
                (await intentStorage.GetIntents(intentTxIds: [intent.IntentTxId], cancellationToken: CancellationToken.None)).FirstOrDefault();

            if (intentAfterLock is null)
            {
                logger?.LogDebug("Intent {IntentTxId} no longer exists, skipping cancellation", intent.IntentTxId);
                continue;
            }

            // Never cancel an intent that has a commitment tx — the batch succeeded
            if (intentAfterLock.CommitmentTransactionId is not null)
            {
                logger?.LogInformation("Skipping cancellation of intent {IntentTxId} — has commitment tx {CommitmentTx}",
                    intentAfterLock.IntentTxId, intentAfterLock.CommitmentTransactionId);
                continue;
            }

            // If the intent has been submitted to arkd, delete it from the server first
            if ( intentAfterLock.IntentId is not null)
            {
                try
                {
                    logger?.LogDebug("Deleting intent {IntentId} from arkd server", intentAfterLock.IntentId);
                    await clientTransport.DeleteIntent(intentAfterLock, token);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(0, ex, "Failed to delete intent {IntentId} from arkd server, continuing with local cancellation", intentAfterLock.IntentId);
                }
            }

            await intentStorage.SaveIntent(intentAfterLock.WalletId,
                intentAfterLock with
                {
                    State = ArkIntentState.Cancelled,
                    CancellationReason = "Superseded by new intent",
                    UpdatedAt = DateTimeOffset.UtcNow
                }, CancellationToken.None);

            logger?.LogDebug("Cancelled overlapping intent {IntentTxId} (was in state {State})", intentAfterLock.IntentTxId, intentAfterLock.State);
        }
    }

    private async Task<string?> GenerateIntentFromSpec(string walletId, ArkIntentSpec intentSpec, CancellationToken token = default)
    {
        logger?.LogDebug("Generating intent from spec for wallet {WalletId} with {CoinCount} coins", walletId, intentSpec.Coins.Length);

        // Lock on wallet level to prevent race conditions between concurrent intent creation.
        // This ensures that the check for overlapping intents and the creation of new intent
        // happen atomically, preventing duplicate intents for the same VTXOs.
        await using var walletLock = await safetyService.LockKeyAsync($"intent-generation::{walletId}", token);

        ArkServerInfo serverInfo = await clientTransport.GetServerInfoAsync(token);
        var outputsSum = intentSpec.Outputs.Sum(o => o.Value);
        var inputsSum = intentSpec.Coins.Sum(c => c.Amount);
        var fee = await feeEstimator.EstimateFeeAsync(intentSpec, token);

        if (inputsSum -outputsSum < fee)
        {
            logger?.LogWarning("Intent generation failed for wallet {WalletId}: fees not properly considered, missing {MissingAmount} sats", walletId, inputsSum + fee - outputsSum);
            throw new InvalidOperationException(
                $"Scheduler is not considering fees properly, missing fees by {inputsSum + fee - outputsSum} sats");
        }

        // Always cancel any overlapping intents - new intent wins
        var inputOutpoints = intentSpec.Coins.Select(c => c.Outpoint).ToArray();
        await CancelOverlappingIntentsAsync(walletId, inputOutpoints, token);

        var addrProvider = await walletProvider.GetAddressProviderAsync(walletId, token)
                           ?? throw new InvalidOperationException("Wallet belonging to the intent was not found!");

        var coinDescriptors = intentSpec.Coins
            .Where(c => c.SignerDescriptor is not null)
            .Select(c => new { c.Outpoint, Descriptor = c.SignerDescriptor!.ToString() })
            .ToArray();
        logger?.LogDebug("Intent coin descriptors for wallet {WalletId}: {CoinDescriptors}",
            walletId, string.Join(", ", coinDescriptors.Select(c => $"{c.Outpoint}={c.Descriptor}")));

        var singingDescriptor =
            coinDescriptors.Length > 0
                ? intentSpec.Coins.First(c => c.SignerDescriptor is not null).SignerDescriptor!
                : await addrProvider.GetNextSigningDescriptor(token);

        logger?.LogDebug("Using signing descriptor for wallet {WalletId}: {SigningDescriptor}",
            walletId, singingDescriptor.ToString());

        // Get the signer's actual compressed pubkey (with correct parity) rather than
        // deriving it from the descriptor, which loses parity through tr() serialization.
        var signer = await walletProvider.GetSignerAsync(walletId, token)
                     ?? throw new InvalidOperationException("Signer not found for wallet");
        var signerPubKey = await signer.GetPubKey(singingDescriptor, token);
        var descriptorPubKey = singingDescriptor.ToPubKey();

        logger?.LogInformation(
            "Intent cosigner key selection for wallet {WalletId}: " +
            "SignerPubKey={SignerPubKey} (from signer.GetPubKey — actual key with correct parity), " +
            "DescriptorPubKey={DescriptorPubKey} (from descriptor.ToPubKey — may have wrong parity from tr() roundtrip), " +
            "ParityMatch={ParityMatch}, " +
            "SigningDescriptor={SigningDescriptor}, " +
            "DescriptorSource={DescriptorSource}",
            walletId,
            Convert.ToHexString(signerPubKey.ToBytes()).ToLowerInvariant(),
            Convert.ToHexString(descriptorPubKey.ToBytes()).ToLowerInvariant(),
            signerPubKey == descriptorPubKey,
            singingDescriptor.ToString(),
            coinDescriptors.Length > 0 ? "from existing coin's SignerDescriptor" : "from GetNextSigningDescriptor");

        var (RegisterTx, Delete, RegisterMessage, DeleteMessage) = await CreateIntents(
            serverInfo.Network,
            new HashSet<ECPubKey>([
                signerPubKey
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
        var maxLockTime = inputs
            .Where(c => c.LockTime is not null)
            .Select(c => (uint)c.LockTime!.Value)
            .DefaultIfEmpty(0U)
            .Max();
        var toSignTx =
            CreatePsbt(
                firstInput.ScriptPubKey,
                network,
                message,
                2U,
                maxLockTime,
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
            logger?.LogDebug(
                "Signing coin: WalletIdentifier={WalletIdentifier}, Outpoint={Outpoint}, " +
                "SignerDescriptor={SignerDescriptor}, ContractType={ContractType}",
                coin.WalletIdentifier, coin.Outpoint,
                coin.SignerDescriptor?.ToString() ?? "(null)",
                coin.Contract?.GetType().Name ?? "(null)");

            var signer = await walletProvider.GetSignerAsync(coin.WalletIdentifier, cancellationToken);
            if (signer is null)
            {
                logger?.LogError("No signer found for wallet {WalletIdentifier}, skipping coin {Outpoint}",
                    coin.WalletIdentifier, coin.Outpoint);
                continue;
            }
            await PsbtHelpers.SignAndFillPsbt(signer, coin, toSignTx, precomputedTransactionData, cancellationToken: cancellationToken);
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
        DateTimeOffset? validAt,
        DateTimeOffset? expireAt,
        IReadOnlyCollection<ArkCoin> inputCoins,
        IReadOnlyCollection<ArkTxOut>? outs = null,
        CancellationToken cancellationToken = default
    )
    {
        var msg = new Messages.RegisterIntentMessage
        {
            Type = "register",
            OnchainOutputsIndexes = outs?.Select((x, i) => (x, i)).Where(o => o.x.Type == ArkTxOutType.Onchain).Select((_, i) => i).ToArray() ?? [],
            ValidAt = validAt?.ToUnixTimeSeconds()??0,
            ExpireAt = expireAt?.ToUnixTimeSeconds()??0,
            CosignersPublicKeys = cosigners.Select(c => c.ToBytes().ToHexStringLower()).ToArray()
        };

        var deleteMsg = new Messages.DeleteIntentMessage()
        {
            Type = "delete",
            ExpireAt = expireAt?.ToUnixTimeSeconds()??0
        };
        var message = JsonSerializer.Serialize(msg);
        var deleteMessage = JsonSerializer.Serialize(deleteMsg);

        // Build asset packet if any input coins carry assets.
        // This OP_RETURN output is appended to the register intent only (not the delete intent).
        // arkd calls LeafTxPacket() on this packet during RegisterIntent to store the
        // batch-leaf-form asset data, which will be embedded in the vtxo tree leaf tx.
        IReadOnlyCollection<TxOut>? registerOutputs = outs?.Cast<TxOut>().ToArray();
        var assetPacketTxOut = BuildIntentAssetPacket(inputCoins, outs);
        if (assetPacketTxOut != null)
        {
            var outputList = registerOutputs?.ToList() ?? [];
            outputList.Add(assetPacketTxOut);
            registerOutputs = outputList;
        }

        return (
            await CreateIntent(message, network, inputCoins.ToArray(), registerOutputs, cancellationToken),
            await CreateIntent(deleteMessage, network, inputCoins.ToArray(), null, cancellationToken),
            message,
            deleteMessage);
    }

    /// <summary>
    /// Builds an asset packet OP_RETURN TxOut for an intent proof PSBT.
    /// Input vin indices are offset by +1 because the BIP322 toSpend reference occupies
    /// input[0] in the intent proof transaction. Asset outputs reference the explicit
    /// output indices from <paramref name="outputs"/>; any remaining asset change goes
    /// to vout=0 (the send-to-self output).
    /// </summary>
    private static TxOut? BuildIntentAssetPacket(
        IReadOnlyCollection<ArkCoin> inputCoins,
        IReadOnlyCollection<ArkTxOut>? outputs)
    {
        var coinList = inputCoins.ToList();

        // Collect asset inputs with +1 vin offset for the BIP322 fake input at index 0
        var assetInputsByAssetId = new Dictionary<string, List<(ushort vin, ulong amount)>>();
        for (var i = 0; i < coinList.Count; i++)
        {
            if (coinList[i].Assets is not { Count: > 0 } assets) continue;
            foreach (var asset in assets)
            {
                if (!assetInputsByAssetId.ContainsKey(asset.AssetId))
                    assetInputsByAssetId[asset.AssetId] = [];
                // +1: intent proof input[0] is the fake toSpend reference
                assetInputsByAssetId[asset.AssetId].Add(((ushort)(i + 1), asset.Amount));
            }
        }

        if (assetInputsByAssetId.Count == 0)
            return null;

        // Collect explicit asset outputs from ArkTxOut.Assets (if any)
        var assetOutputsByAssetId = new Dictionary<string, List<(ushort vout, ulong amount)>>();
        if (outputs != null)
        {
            var outList = outputs.ToList();
            for (var i = 0; i < outList.Count; i++)
            {
                if (outList[i].Assets is not { Count: > 0 } assets) continue;
                foreach (var asset in assets)
                {
                    if (!assetOutputsByAssetId.ContainsKey(asset.AssetId))
                        assetOutputsByAssetId[asset.AssetId] = [];
                    assetOutputsByAssetId[asset.AssetId].Add(((ushort)i, asset.Amount));
                }
            }
        }

        var groups = new List<AssetGroup>();
        foreach (var (assetIdStr, inputs) in assetInputsByAssetId)
        {
            var assetId = AssetId.FromString(assetIdStr);
            var groupInputs = inputs.Select(x => AssetInput.Create(x.vin, x.amount)).ToList();

            var totalIn = inputs.Aggregate(0UL, (sum, x) => sum + x.amount);

            // Start with explicit outputs for this asset
            var groupOutputs = assetOutputsByAssetId.GetValueOrDefault(assetIdStr)?
                .Select(x => AssetOutput.Create(x.vout, x.amount))
                .ToList() ?? [];

            // Assign remaining asset amount to vout=0 (send-to-self output)
            var totalExplicitOut = groupOutputs.Aggregate(0UL, (sum, o) => sum + o.Amount);
            var remaining = totalIn - totalExplicitOut;
            if (remaining > 0)
            {
                var existingIdx = groupOutputs.FindIndex(o => o.Vout == 0);
                if (existingIdx >= 0)
                {
                    var existing = groupOutputs[existingIdx];
                    groupOutputs[existingIdx] = AssetOutput.Create(0, existing.Amount + remaining);
                }
                else
                {
                    groupOutputs.Add(AssetOutput.Create(0, remaining));
                }
            }

            groups.Add(AssetGroup.Create(assetId, null, groupInputs, groupOutputs, []));
        }

        return Packet.Create(groups).ToTxOut();
    }

    public async ValueTask DisposeAsync()
    {
        logger?.LogDebug("Disposing intent generation service");

        await _shutdownCts.CancelAsync();

        if (_generationTask is not null)
            await _generationTask;

        logger?.LogInformation("Intent generation service disposed");
    }

    public async Task<string> GenerateManualIntent(string walletId, ArkIntentSpec spec, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Generating manual intent for wallet {WalletId}", walletId);
        var intentTxId = await GenerateIntentFromSpec(walletId, spec, cancellationToken)
            ?? throw new InvalidOperationException("Intent generation returned null unexpectedly");

        return intentTxId;
    }
}

public interface IIntentGenerationService
{
    Task<string> GenerateManualIntent(string walletId, ArkIntentSpec spec,
        CancellationToken cancellationToken = default);
}