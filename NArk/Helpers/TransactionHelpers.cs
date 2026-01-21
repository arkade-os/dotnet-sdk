using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Models;
using NArk.Scripts;
using NArk.Transport;
using NBitcoin;

namespace NArk.Helpers;

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
        IIntentStorage intentStorage)
    {
        private async Task<PSBT> FinalizeCheckpointTx(PSBT checkpointTx, PSBT receivedCheckpointTx, ArkCoin coin,
            CancellationToken cancellationToken)
        {
            // Sign the checkpoint transaction
            var checkpointGtx = receivedCheckpointTx.GetGlobalTransaction();
            var checkpointPrecomputedTransactionData =
                checkpointGtx.PrecomputeTransactionData([coin.TxOut]);

            receivedCheckpointTx.UpdateFrom(checkpointTx);

            var signer = await walletProvider.GetSignerAsync(coin.WalletIdentifier, cancellationToken);

            await PsbtHelpers.SignAndFillPsbt(signer!, coin, receivedCheckpointTx, checkpointPrecomputedTransactionData,
                cancellationToken: cancellationToken);

            return receivedCheckpointTx;
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
            var p2A = Script.FromHex("51024e73"); // Standard Ark protocol marker

            List<PSBT> checkpoints = [];
            List<ArkCoin> checkpointCoins = [];
            foreach (var coin in coins)
            {
                // Create a checkpoint contract
                var checkpointContract = CreateCheckpointContract(coin, serverInfo.CheckpointTapScript);

                // Build checkpoint transaction
                var checkpoint = serverInfo.Network.CreateTransactionBuilder();
                checkpoint.SetVersion(3);
                checkpoint.SetFeeWeight(0);
                checkpoint.AddCoin(coin, new CoinOptions()
                {
                    Sequence = coin.Sequence
                });
                checkpoint.DustPrevention = false;
                checkpoint.Send(checkpointContract.GetArkAddress(), coin.Amount);
                checkpoint.SetLockTime(coin.LockTime ?? LockTime.Zero);
                var checkpointTx = checkpoint.BuildPSBT(false, PSBTVersion.PSBTv0);

                //checkpoints MUST have the p2a output at index '1' and NBitcoin tx builder does not assure it, so we hack our way there
                var ctx = checkpointTx.GetGlobalTransaction();
                ctx.Outputs.Add(new TxOut(Money.Zero, p2A));
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
                        coin.Swept
                    )
                );
            }

            // Build the Ark transaction that spends from all checkpoint outputs

            var arkTx = serverInfo.Network.CreateTransactionBuilder();
            arkTx.SetVersion(3);
            arkTx.SetFeeWeight(0);
            arkTx.DustPrevention = false;
            // arkTx.Send(p2a, Money.Zero);
            arkTx.AddCoins(checkpointCoins);

            // Track OP_RETURN outputs to enforce the limit
            // First, count any existing OP_RETURN outputs
            int opReturnCount = outputs.Count(o => o.ScriptPubKey.IsUnspendable);

            if (opReturnCount > MaxOpReturnOutputs)
            {
                throw new InvalidOperationException(
                    $"Transaction already contains {opReturnCount} OP_RETURN outputs, which exceeds the maximum of {MaxOpReturnOutputs}.");
            }

            foreach (var output in outputs)
            {
                // Check if this is an Ark address output that needs subdust handling
                var scriptPubKey = output.ScriptPubKey;

                // If the output value is below the dust threshold, and it's a P2TR output,
                // convert it to an OP_RETURN output
                if (output.Value < serverInfo.Dust && PayToTaprootTemplate.Instance.CheckScriptPubKey(scriptPubKey))
                {
                    if (opReturnCount >= MaxOpReturnOutputs)
                    {
                        throw new InvalidOperationException(
                            $"Cannot create more than {MaxOpReturnOutputs} OP_RETURN outputs per transaction. " +
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
            gtx.Outputs.Add(new TxOut(Money.Zero, p2A));
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
                var signer = await walletProvider.GetSignerAsync(coin.WalletIdentifier, cancellationToken);
                await PsbtHelpers.SignAndFillPsbt(signer!, coin, tx, precomputedTransactionData, cancellationToken: cancellationToken);
            }

            //reorder the checkpoints to match the order of the inputs of the Ark transaction

            return (tx, new SortedSet<IndexedPSBT>(checkpoints.Select(psbt =>
            {
                var output = psbt.Outputs.Single(output => output.ScriptPubKey != p2A);
                var outpoint = new OutPoint(psbt.GetGlobalTransaction(), output.Index);
                var index = tx.Inputs.FindIndexedInput(outpoint)!.Index;
                return new IndexedPSBT(psbt, (int)index);
            })));
        }

        /// <summary>
        /// Creates a checkpoint contract based on the input contract type
        /// </summary>
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
        }


        public async Task<uint256> ConstructAndSubmitArkTransaction(
            IReadOnlyCollection<ArkCoin> arkCoins,
            ArkTxOut[] arkOutputs,
            CancellationToken cancellationToken)
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

            var (arkTx, checkpoints) =
                await ConstructArkTransaction(arkCoins, [.. arkOutputs], serverInfo, cancellationToken);
            await SubmitArkTransaction(arkCoins, arkTx, checkpoints, cancellationToken);

            foreach (var spentCoins in arkCoins.GroupBy(c => c.WalletIdentifier))
            {
                var intents =
                    await intentStorage.GetIntentsByInputs(
                        spentCoins.Key,
                        [.. spentCoins.Select(c => c.Outpoint)],
                        true,
                        CancellationToken.None
                    );
                foreach (var intent in intents)
                {
                    await using var intentLock =
                        await safetyService.LockKeyAsync($"intent::{intent.InternalId}", CancellationToken.None);
                    var intentAfterLock =
                        await intentStorage.GetIntentByInternalId(intent.InternalId, CancellationToken.None)
                        ?? throw new Exception("Should not happen, intent disappeared from storage mid-action");
                    await intentStorage.SaveIntent(intentAfterLock.WalletId,
                        intentAfterLock with { State = ArkIntentState.Cancelled }, CancellationToken.None);
                }
            }

            return arkTx.GetGlobalTransaction().GetHash();
        }

        public async Task<PSBT> ConstructForfeitTx(ArkServerInfo arkServerInfo, ArkCoin coin, Coin? connector,
            IDestination forfeitDestination, CancellationToken cancellationToken = default)
        {
            var p2A = Script.FromHex("51024e73"); // Standard Ark protocol marker

            // Determine sighash based on whether we have a connector
            // Without connector: ANYONECANPAY|ALL (allows adding connector later)
            // With connector: DEFAULT (signs all inputs)
            var sighash = connector is null
                ? TaprootSigHash.AnyoneCanPay | TaprootSigHash.All
                : TaprootSigHash.Default;

            // Build forfeit transaction
            var txBuilder = arkServerInfo.Network.CreateTransactionBuilder();
            txBuilder.SetVersion(3);
            txBuilder.SetFeeWeight(0);
            txBuilder.DustPrevention = false;
            txBuilder.ShuffleInputs = false;
            txBuilder.ShuffleOutputs = false;
            txBuilder.SetLockTime(coin.LockTime ?? LockTime.Zero);

            // Add VTXO input
            txBuilder.AddCoin(coin, new CoinOptions()
            {
                Sequence = coin.Sequence
            });

            // Add connector input if provided
            if (connector is not null)
            {
                txBuilder.AddCoin(connector);
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

            var signer = await walletProvider.GetSignerAsync(coin.WalletIdentifier, cancellationToken);

            await PsbtHelpers.SignAndFillPsbt(signer!, coin, forfeitTx, precomputedTransactionData, sighash, cancellationToken);

            return forfeitTx;
        }
    }
}