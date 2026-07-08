using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VirtualTxs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NArk.Core.Models;
using NArk.Core.Scripts;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Helpers;

public static class TransactionHelpers
{
    public const int MaxOpReturnOutputs = 1;

    /// <summary>
    /// Utility class for building and constructing Ark transactions
    /// </summary>
    public class ArkTransactionBuilder(
        IClientTransport clientTransport,
        ISafetyService safetyService,
        IWalletProvider walletProvider,
        IIntentStorage intentStorage,
        IVirtualTxStorage? virtualTxStorage = null,
        IEnumerable<ISpendSubmitHandler>? submitHandlers = null)
    {
        private async Task<PSBT> FinalizeCheckpointTx(PSBT checkpointTx, PSBT receivedCheckpointTx, ArkCoin coin,
            CancellationToken cancellationToken)
        {
            // Sign the checkpoint transaction
            var checkpointGtx = receivedCheckpointTx.GetGlobalTransaction();
            var checkpointPrecomputedTransactionData =
                checkpointGtx.PrecomputeTransactionData([coin.TxOut]);

            receivedCheckpointTx.UpdateFrom(checkpointTx);

            var signer = await walletProvider.GetSignerOrThrowAsync(coin.WalletIdentifier, cancellationToken,
                $"Cannot sign checkpoint tx: wallet '{coin.WalletIdentifier}' has no signer (watch-only or remote signer unavailable).");

            await PsbtHelpers.SignAndFillPsbt(signer, coin, receivedCheckpointTx, checkpointPrecomputedTransactionData,
                cancellationToken: cancellationToken);

            return receivedCheckpointTx;
        }

        /// <summary>
        /// Attach the wallet's own signature to a freshly-built checkpoint's input
        /// (before any arkd round-trip). Used by the covenant submit path, where the
        /// emulator co-signer requires user-signed checkpoints up front — unlike the
        /// cooperative arkd flow, which signs checkpoints only after arkd co-signs
        /// (see <see cref="FinalizeCheckpointTx"/>).
        /// </summary>
        private async Task<PSBT> UserSignCheckpoint(PSBT checkpointTx, ArkCoin coin, CancellationToken cancellationToken)
        {
            var precomputed = checkpointTx.GetGlobalTransaction().PrecomputeTransactionData([coin.TxOut]);

            var signer = await walletProvider.GetSignerAsync(coin.WalletIdentifier, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Cannot sign checkpoint tx: wallet '{coin.WalletIdentifier}' has no signer " +
                    "(watch-only wallet, or its remote signer transport is unavailable).");

            await PsbtHelpers.SignAndFillPsbt(signer, coin, checkpointTx, precomputed,
                cancellationToken: cancellationToken);

            return checkpointTx;
        }

        /// <summary>
        /// Constructs an Ark transaction with checkpoint transactions for each input
        /// </summary>
        /// <param name="coins">Collection of coins and their respective signers</param>
        /// <param name="outputs">Output transactions</param>
        /// <param name="serverInfo">Info retrieved from Ark operator</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The Ark transaction and checkpoint transactions with their input witnesses</returns>
        public async Task<(PSBT arkTx, SortedSet<IndexedPSBT> checkpoints)> ConstructArkTransaction(
            IEnumerable<ArkCoin> coins,
            TxOut[] outputs,
            ArkServerInfo serverInfo,
            CancellationToken cancellationToken)
        {
            List<PSBT> checkpoints = [];
            List<ArkCoin> checkpointCoins = [];
            foreach (var coin in coins)
            {
                // Create a checkpoint contract
                var checkpointContract = CreateCheckpointContract(coin, serverInfo.CheckpointTapScript);

                // Build checkpoint transaction
                var checkpoint = CreateArkTxBuilder(serverInfo.Network);
                checkpoint.AddCoin(coin, new CoinOptions() { Sequence = coin.Sequence });
                checkpoint.Send(checkpointContract.GetArkAddress(), coin.Amount);
                checkpoint.SetLockTime(coin.LockTime ?? LockTime.Zero);
                var checkpointTx = checkpoint.BuildPSBT(false, PSBTVersion.PSBTv0);

                //checkpoints MUST have the p2a output at index '1' and NBitcoin tx builder does not assure it, so we hack our way there
                var ctx = checkpointTx.GetGlobalTransaction();
                ctx.Outputs.Add(new TxOut(Money.Zero, Constants.ArkP2A));
                checkpointTx = PSBT.FromTransaction(ctx, serverInfo.Network, PSBTVersion.PSBTv0);
                checkpoint.UpdatePSBT(checkpointTx);

                _ = coin.FillPsbtInput(checkpointTx);
                checkpoints.Add(checkpointTx);

                // Create a checkpoint coin for the Ark transaction
                var txout = checkpointTx.Outputs.Single(output =>
                    output.ScriptPubKey == checkpointContract.GetArkAddress().ScriptPubKey);
                var outpoint = new OutPoint(checkpointTx.GetGlobalTransaction(), txout.Index);

                checkpointCoins.Add(
                    new ArkCoin(
                        coin.WalletIdentifier,
                        checkpointContract,
                        coin.Birth,
                        coin.ExpiresAt,
                        coin.ExpiresAtHeight,
                        outpoint,
                        txout.GetTxOut()!,
                        coin.SignerDescriptor,
                        coin.SpendingScriptBuilder,
                        coin.SpendingConditionWitness,
                        coin.LockTime,
                        coin.Sequence,
                        coin.Swept,
                        coin.Unrolled
                    )
                );
            }

            // Build the Ark transaction that spends from all checkpoint outputs

            var arkTx = CreateArkTxBuilder(serverInfo.Network);
            // arkTx.Send(p2a, Money.Zero);
            // The checkpoint's collaborative leaf preserves the original VTXO leaf's CLTV, so arkd's
            // offchain.buildArkTx re-derives the ark tx's locktime + a non-final input sequence
            // (cltvSequence = 0xFFFFFFFE) from that closure when rebuilding it. We must match exactly or
            // arkd rejects the spend with ARK_TX_MISMATCH (the checkpoint txids already agree; only the
            // ark tx diverges). A non-CLTV path (e.g. a hashlock claim) leaves LockTime null → locktime
            // 0 and final sequences, so this is a no-op there.
            var arkLockTime = LockTime.Zero;
            foreach (var cc in checkpointCoins)
            {
                if (cc.LockTime is not { } lt || lt == LockTime.Zero) continue;
                // Match arkd/ts-sdk buildVirtualTx: absolute locktimes across inputs must share a unit
                // (all block-height or all timestamp) — nLockTime is a single field that can't encode
                // both, so a mixed tx is unrepresentable. Fail loudly rather than emit a tx arkd rejects.
                if (arkLockTime != LockTime.Zero && arkLockTime.IsTimeLock != lt.IsTimeLock)
                    throw new InvalidOperationException(
                        "cannot mix seconds and blocks absolute locktimes across inputs of one ark tx");
                if (lt.Value > arkLockTime.Value)
                    arkLockTime = lt;
            }
            if (arkLockTime != LockTime.Zero)
            {
                arkTx.SetLockTime(arkLockTime);
                foreach (var cc in checkpointCoins)
                {
                    var hasLocktime = cc.LockTime is not null && cc.LockTime != LockTime.Zero;
                    arkTx.AddCoin(cc, new CoinOptions
                    {
                        Sequence = cc.Sequence ?? (hasLocktime ? new Sequence(0xFFFFFFFE) : new Sequence(0xFFFFFFFF)),
                    });
                }
            }
            else
            {
                arkTx.AddCoins(checkpointCoins);
            }

            // Track OP_RETURN outputs to enforce the limit
            // Use server-configured limit, falling back to the const default
            var maxOpReturn = serverInfo.MaxOpReturnOutputs > 0
                ? serverInfo.MaxOpReturnOutputs
                : MaxOpReturnOutputs;

            // First, count any existing OP_RETURN outputs
            int opReturnCount = outputs.Count(o => o.ScriptPubKey.IsUnspendable);

            if (opReturnCount > maxOpReturn)
            {
                throw new InvalidOperationException(
                    $"Transaction already contains {opReturnCount} OP_RETURN outputs, which exceeds the maximum of {maxOpReturn}.");
            }

            foreach (var output in outputs)
            {
                // Check if this is an Ark address output that needs subdust handling
                var scriptPubKey = output.ScriptPubKey;

                // If the output value is below the dust threshold, and it's a P2TR output,
                // convert it to an OP_RETURN output
                if (output.Value < serverInfo.Dust && PayToTaprootTemplate.Instance.CheckScriptPubKey(scriptPubKey))
                {
                    if (opReturnCount >= maxOpReturn)
                    {
                        throw new InvalidOperationException(
                            $"Cannot create more than {maxOpReturn} OP_RETURN outputs per transaction. " +
                            $"Output with value {output.Value} is below dust threshold {serverInfo.Dust}. " +
                            $"Transaction already contains {opReturnCount} OP_RETURN output(s).");
                    }

                    // Extract the taproot pubkey and create an OP_RETURN script
                    var taprootPubKey = PayToTaprootTemplate.Instance.ExtractScriptPubKeyParameters(scriptPubKey);
                    if (taprootPubKey is null)
                        throw new FormatException("BUG: Could not extract Taproot parameters from scriptPubKey");
                    scriptPubKey = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(taprootPubKey.ToBytes()));
                    opReturnCount++;
                }

                arkTx.Send(scriptPubKey, output.Value);
            }

            var tx = arkTx.BuildPSBT(false, PSBTVersion.PSBTv0);
            var gtx = tx.GetGlobalTransaction();
            gtx.Outputs.Add(new TxOut(Money.Zero, Constants.ArkP2A));

            // NBitcoin's TransactionBuilder may reorder inputs (e.g. by amount) even
            // with ShuffleInputs=false. If asset packets are present, their input
            // indices (vin) must match the actual PSBT input order, not the original
            // coin order used when building the packet. Remap if needed.
            var coinList = coins.ToList();
            var inputRemapping = new Dictionary<ushort, ushort>();
            var needsRemap = false;
            for (var origIdx = 0; origIdx < coinList.Count; origIdx++)
            {
                var cpOutpoint = checkpointCoins[origIdx].Outpoint;
                for (var psbtIdx = 0; psbtIdx < gtx.Inputs.Count; psbtIdx++)
                {
                    if (gtx.Inputs[psbtIdx].PrevOut != cpOutpoint) continue;
                    inputRemapping[(ushort)origIdx] = (ushort)psbtIdx;
                    if (psbtIdx != origIdx) needsRemap = true;
                    break;
                }
            }

            if (needsRemap)
            {
                for (var i = 0; i < gtx.Outputs.Count; i++)
                {
                    if (!Assets.Extension.IsExtension(gtx.Outputs[i].ScriptPubKey)) continue;
                    var ext = Assets.Extension.FromScript(gtx.Outputs[i].ScriptPubKey);
                    var packet = ext.GetAssetPacket();
                    if (packet is null) continue;
                    var remappedGroups = packet.Groups.Select(g =>
                        Assets.AssetGroup.Create(
                            g.AssetId, g.ControlAsset,
                            g.Inputs.Select(inp =>
                                Assets.AssetInput.Create(inputRemapping.GetValueOrDefault(inp.Vin, inp.Vin), inp.Amount))
                                .ToList(),
                            g.Outputs, g.Metadata)).ToList();
                    var remappedTxOut = Assets.Packet.Create(remappedGroups).ToTxOut();
                    gtx.Outputs[i].ScriptPubKey = remappedTxOut.ScriptPubKey;
                    gtx.Outputs[i].Value = remappedTxOut.Value;
                    break;
                }
            }

            tx = PSBT.FromTransaction(gtx, serverInfo.Network, PSBTVersion.PSBTv0);
            arkTx.UpdatePSBT(tx);

            //sort the checkpoint coins based on the input index in arkTx

            var sortedCheckpointCoins =
                tx.Inputs.ToDictionary(input => (int)input.Index,
                    input => checkpointCoins.Single(x => x.Outpoint == input.PrevOut));

            // Sign each input in the Ark transaction
            var precomputedTransactionData =
                gtx.PrecomputeTransactionData(sortedCheckpointCoins.OrderBy(x => x.Key).Select(x => x.Value.TxOut)
                    .ToArray());


            foreach (var (_, coin) in sortedCheckpointCoins)
            {
                var signer = await walletProvider.GetSignerOrThrowAsync(coin.WalletIdentifier, cancellationToken,
                    $"Cannot sign Arkade tx checkpoint input: wallet '{coin.WalletIdentifier}' has no signer (watch-only or remote signer unavailable).");
                await PsbtHelpers.SignAndFillPsbt(signer, coin, tx, precomputedTransactionData, cancellationToken: cancellationToken);
            }

            //reorder the checkpoints to match the order of the inputs of the Ark transaction

            return (tx, new SortedSet<IndexedPSBT>(checkpoints.Select(psbt =>
            {
                var output = psbt.Outputs.Single(output => output.ScriptPubKey != Constants.ArkP2A);
                var outpoint = new OutPoint(psbt.GetGlobalTransaction(), output.Index);
                var index = tx.Inputs.FindIndexedInput(outpoint)!.Index;
                return new IndexedPSBT(psbt, (int)index);
            })));
        }

        /// <summary>
        /// Creates a checkpoint contract based on the input contract type
        /// </summary>
        private static TransactionBuilder CreateArkTxBuilder(Network network)
        {
            var b = network.CreateTransactionBuilder();
            b.SetVersion(3);
            b.SetFeeWeight(0);
            b.DustPrevention = false;
            b.ShuffleInputs = false;
            b.ShuffleOutputs = false;
            return b;
        }

        private ArkContract CreateCheckpointContract(ArkCoin coin, UnilateralPathArkTapScript serverUnrollScript)
        {
            if (coin.Contract.Server is null)
                throw new ArgumentException("Server key is required for checkpoint contract creation");

            var scriptBuilders = new List<ScriptBuilder>
            {
                coin.SpendingScriptBuilder,
                serverUnrollScript
            };

            return new GenericArkContract(coin.Contract.Server, scriptBuilders);
        }

        public async Task SubmitArkTransaction(
            IReadOnlyCollection<ArkCoin> arkCoins,
            PSBT arkTx,
            SortedSet<IndexedPSBT> checkpoints,
            CancellationToken cancellationToken
        )
        {
            var network = arkTx.Network;

            // Covenant spends: hand off to a submit handler (the emulator co-signer,
            // which fronts arkd) instead of submitting to arkd directly. The handler
            // needs the checkpoints already user-signed, then owns co-sign + finalize.
            if (submitHandlers?.FirstOrDefault(h => h.ShouldHandle(arkCoins)) is { } handler)
            {
                var signedCheckpointTxs = new List<PSBT>(checkpoints.Count);
                foreach (var checkpoint in checkpoints)
                {
                    var coin = arkCoins.Single(x => x.Outpoint == checkpoint.Psbt.Inputs.Single().PrevOut);
                    signedCheckpointTxs.Add(await UserSignCheckpoint(checkpoint.Psbt, coin, cancellationToken));
                }

                await handler.SubmitAsync(arkCoins, arkTx, signedCheckpointTxs, cancellationToken);
                return;
            }

            var response = await clientTransport.SubmitTx(arkTx.ToBase64(),
                [.. checkpoints.Select(c => c.Psbt.ToBase64())], cancellationToken);

            // Process the signed checkpoints from the server
            var parsedReceivedCheckpoints = response.SignedCheckpointTxs
                .Select(x => PSBT.Parse(x, network))
                .ToDictionary(psbt => psbt.GetGlobalTransaction().GetHash());

            SortedSet<IndexedPSBT> signedCheckpoints = [];
            foreach (var signedCheckpoint in checkpoints)
            {
                var coin = arkCoins.Single(x => x.Outpoint == signedCheckpoint.Psbt.Inputs.Single().PrevOut);
                var psbt = await FinalizeCheckpointTx(signedCheckpoint.Psbt,
                    parsedReceivedCheckpoints[signedCheckpoint.Psbt.GetGlobalTransaction().GetHash()], coin,
                    cancellationToken);
                signedCheckpoints.Add(signedCheckpoint with { Psbt = psbt });
            }

            await clientTransport.FinalizeTx(response.ArkTxId, [.. signedCheckpoints.Select(x => x.Psbt.ToBase64())],
                cancellationToken: cancellationToken);

            // Capture the signed off-chain txs we just produced, keyed by txid,
            // so a later unilateral exit of the resulting preconfirmed VTXO
            // broadcasts them straight from storage. The
            // auto-fetch that builds the exit branch preserves these signed rows
            // (see VirtualTxService.FetchAndStoreBranchAsync), and they are
            // pruned on spend like any other virtual-tx row.
            if (virtualTxStorage is not null)
            {
                var captured = new List<VirtualTx>
                {
                    new(arkTx.GetGlobalTransaction().GetHash().ToString(), arkTx.ToBase64(), null, ChainedTxType.Ark),
                };
                captured.AddRange(signedCheckpoints.Select(c => new VirtualTx(
                    c.Psbt.GetGlobalTransaction().GetHash().ToString(), c.Psbt.ToBase64(), null, ChainedTxType.Checkpoint)));

                await virtualTxStorage.UpsertVirtualTxsAsync(captured, cancellationToken);
            }
        }


        public async Task<PSBT> ConstructAndSubmitArkTransaction(
            IReadOnlyCollection<ArkCoin> arkCoins,
            ArkTxOut[] arkOutputs,
            CancellationToken cancellationToken,
            TxOut? extensionOutput = null)
        {
            if (arkOutputs.Any(o => o.Type is not ArkTxOutType.Vtxo))
                throw new InvalidOperationException();
            var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);

            foreach (var coin in arkCoins)
            {
                if (!await safetyService.TryLockByTimeAsync($"vtxo::{coin.Outpoint}",
                        TimeSpan.FromMinutes(1)))
                {
                    throw new AlreadyLockedVtxoException(
                        "VTXO is temporarily locked for another spend request, to prevent double-spend attempt try again later");
                }
            }

            TxOut[] allOutputs = extensionOutput is not null
                ? [.. arkOutputs, extensionOutput]
                : [.. arkOutputs];

            var (arkTx, checkpoints) =
                await ConstructArkTransaction(arkCoins, allOutputs, serverInfo, cancellationToken);
            await SubmitArkTransaction(arkCoins, arkTx, checkpoints, cancellationToken);

            foreach (var spentCoins in arkCoins.GroupBy(c => c.WalletIdentifier))
            {
                var intents =
                    await intentStorage.GetIntents(
                        walletIds: [spentCoins.Key],
                        containingInputs: [.. spentCoins.Select(c => c.Outpoint)],
                        states: [ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch],
                        cancellationToken: CancellationToken.None
                    );
                foreach (var intent in intents)
                {
                    await using var intentLock =
                        await safetyService.LockKeyAsync($"intent::{intent.IntentTxId}", CancellationToken.None);
                    var intentAfterLock =
                        (await intentStorage.GetIntents(intentTxIds: [intent.IntentTxId], cancellationToken: CancellationToken.None)).FirstOrDefault()
                        ?? throw new Exception("Should not happen, intent disappeared from storage mid-action");
                    await intentStorage.SaveIntent(intentAfterLock.WalletId,
                        intentAfterLock with
                        {
                            State = ArkIntentState.Cancelled,
                            CancellationReason = "Cancelled — inputs spent by another transaction",
                            UpdatedAt = DateTimeOffset.UtcNow
                        }, CancellationToken.None);
                }
            }

            return arkTx;
        }

        public async Task<PSBT> ConstructForfeitTx(ArkServerInfo arkServerInfo, ArkCoin coin, Coin? connector,
            IDestination forfeitDestination, CancellationToken cancellationToken = default)
        {
            var p2A = Constants.ArkP2A;

            // Determine sighash based on whether we have a connector
            // Without connector: ANYONECANPAY|ALL (allows adding connector later)
            // With connector: DEFAULT (signs all inputs)
            var sighash = connector is null
                ? TaprootSigHash.AnyoneCanPay | TaprootSigHash.All
                : TaprootSigHash.Default;

            // Build forfeit transaction
            var txBuilder = CreateArkTxBuilder(arkServerInfo.Network);
            txBuilder.SetLockTime(coin.LockTime ?? LockTime.Zero);

            // Sequences must match arkd's tree.BuildForfeitTx exactly, or the txid
            // diverges and the forfeit is rejected (INVALID_FORFEIT_TXS). arkd uses
            // 0xFFFFFFFE on the VTXO input when the forfeit closure has a CLTV
            // (non-final, so nLockTime applies) and 0xFFFFFFFF otherwise.
            var hasLocktime = coin.LockTime is not null && coin.LockTime != LockTime.Zero;
            var vtxoSequence = coin.Sequence
                ?? (hasLocktime ? new Sequence(0xFFFFFFFE) : new Sequence(0xFFFFFFFF));
            txBuilder.AddCoin(coin, new CoinOptions()
            {
                Sequence = vtxoSequence
            });

            // Connector is always final (0xFFFFFFFF), set explicitly: with a non-zero
            // nLockTime and no explicit sequence, NBitcoin defaults inputs to
            // Sequence.FeeSnipping (0xFFFFFFFE), which would skew
            // the txid here.
            if (connector is not null)
            {
                txBuilder.AddCoin(connector, new CoinOptions()
                {
                    Sequence = new Sequence(0xFFFFFFFF)
                });
            }

            // Calculate total input amount based on connector + input OR assumed connector amount (dust)
            var totalInput = coin.Amount + (connector?.Amount ?? arkServerInfo.Dust);

            // Send to forfeit destination (operator's forfeit address)
            txBuilder.Send(forfeitDestination, totalInput);

            // Add P2A output
            var forfeitTx = txBuilder.BuildPSBT(false, PSBTVersion.PSBTv0);
            var gtx = forfeitTx.GetGlobalTransaction();
            gtx.Outputs.Add(new TxOut(Money.Zero, p2A));
            forfeitTx = PSBT.FromTransaction(gtx, arkServerInfo.Network, PSBTVersion.PSBTv0);
            txBuilder.UpdatePSBT(forfeitTx);

            // Sign the VTXO input with the appropriate sighash
            var coins = connector is not null
                ? new[] { coin.TxOut, connector.TxOut }
                : new[] { coin.TxOut };

            //sort the checkpoint coins based on the input index in arkTx
            var sortedCheckpointCoins =
                forfeitTx
                    .Inputs
                    .ToDictionary(input => (int)input.Index,
                        input => coins.Single(x => x.ScriptPubKey == input.GetTxOut()?.ScriptPubKey));

            // Sign each input in the Ark transaction
            var precomputedTransactionData =
                gtx.PrecomputeTransactionData(sortedCheckpointCoins.OrderBy(x => x.Key).Select(x => x.Value).ToArray());

            var signer = await walletProvider.GetSignerOrThrowAsync(coin.WalletIdentifier, cancellationToken,
                $"Cannot sign forfeit tx: wallet '{coin.WalletIdentifier}' has no signer. Watch-only wallets cannot participate in batches that demand a forfeit.");

            await PsbtHelpers.SignAndFillPsbt(signer, coin, forfeitTx, precomputedTransactionData, sighash, cancellationToken);

            return forfeitTx;
        }
    }
    
    /// <summary>
    /// Builds a version-2 transaction that spends a single P2TR output via a script-path
    /// unilateral exit (CSV timelock). Fee is deducted from the output amount.
    /// </summary>
    public static Transaction BuildCsvSpendTransaction(
        OutPoint vtxoOutpoint,
        TxOut vtxoTxOut,
        BitcoinAddress destinationAddress,
        Sequence csvDelay,
        TapScript tapScript,
        ControlBlock controlBlock,
        FeeRate feeRate,
        Network network)
    {
        var tx = Transaction.Create(network);
        tx.Version = 2;

        var input = tx.Inputs.Add(vtxoOutpoint);
        input.Sequence = csvDelay;

        // P2TR script-path spend: ~64 sig + script + control block ≈ 150-200 wu
        var witnessSize = 64 + tapScript.Script.Length + controlBlock.ToBytes().Length + 10;
        var txBaseSize = 10 + 41 + 43; // version + input + output overhead
        var vsize = txBaseSize + (witnessSize + 3) / 4;
        var fee = feeRate.GetFee(vsize);

        var outputAmount = vtxoTxOut.Value - fee;
        if (outputAmount <= Money.Zero)
            throw new InvalidOperationException(
                $"VTXO amount ({vtxoTxOut.Value}) is too small to cover fees ({fee})");

        tx.Outputs.Add(new TxOut(outputAmount, destinationAddress));
        return tx;
    }
}