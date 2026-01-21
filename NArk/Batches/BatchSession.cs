using System.Text.Json;
using NArk.Abstractions;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Intents;

using NArk.Abstractions.Wallets;
using NArk.Extensions;
using NArk.Helpers;
using NArk.Models;
using NArk.Scripts;
using NArk.Transport;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Batches;

/// <summary>
/// Handles participation in a batch settlement round for a specific intent
/// </summary>
public class BatchSession(
    IClientTransport clientTransport,
    IWalletProvider walletProvider,
    TransactionHelpers.ArkTransactionBuilder arkTransactionBuilder,
    Network network,
    ArkIntent arkIntent,
    ArkCoin[] ins,
    BatchStartedEvent batchStartedEvent)
{
    private readonly OutputDescriptor _outputDescriptor = OutputDescriptor.Parse(arkIntent.SignerDescriptor, network);
    private readonly string _batchId = batchStartedEvent.Id;
    private readonly Messages.RegisterIntentMessage? _intentParameters =
        JsonSerializer.Deserialize<Messages.RegisterIntentMessage>(arkIntent.RegisterProofMessage);
    private TreeSignerSession? _signingSession;
    private readonly List<TxTreeNode> _vtxoChunks = [];
    private readonly List<TxTreeNode> _connectorsChunks = [];
    private uint256? _sweepTapTreeRoot;

    /// <summary>
    /// Initialize the batch session (call this before processing events)
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Get operator terms to build a sweep tap tree
        var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
        var sweepTapScript = new UnilateralPathArkTapScript(batchStartedEvent.BatchExpiry, new NofNMultisigTapScript([terms.ForfeitPubKey])); ;
        _sweepTapTreeRoot = sweepTapScript.Build().LeafHash;
    }

    /// <summary>
    /// Process a single event from the event stream
    /// </summary>
    /// <returns>True if the batch session is complete, false otherwise</returns>
    public async Task<bool> ProcessEventAsync(BatchEvent eventResponse, CancellationToken cancellationToken = default)
    {
        if (IsComplete)
            return true;

        if (_sweepTapTreeRoot == null)
            throw new InvalidOperationException("Batch session not initialized. Call InitializeAsync first.");

        try
        {
            switch (eventResponse)
            {
                case TreeTxEvent treeTx:
                    HandleTreeTxEvent(treeTx, _vtxoChunks, _connectorsChunks);
                    break;
                case TreeSigningStartedEvent treeSigningStartedEvent:
                    if (_vtxoChunks.Count > 0)
                    {
                        _signingSession = await HandleTreeSigningStartedAsync(
                            treeSigningStartedEvent,
                            _sweepTapTreeRoot,
                            _vtxoChunks,
                            cancellationToken);
                    }
                    break;

                case TreeNoncesEvent treeNoncesEvent:
                    if (_signingSession != null)
                    {
                        var val = treeNoncesEvent.Nonces.Values.Select(s =>
                            new MusigPubNonce(Encoders.Hex.DecodeData(s)));
                        var txid = uint256.Parse(treeNoncesEvent.TxId)!;
                        await _signingSession.AggregateNonces(txid, val.ToArray(), cancellationToken);
                    }

                    break;
                case TreeNoncesAggregatedEvent treeNoncesAggregatedEvent:
                    if (_signingSession != null)
                    {
                        await HandleAggregatedTreeNoncesEventAsync(
                            treeNoncesAggregatedEvent,
                            _signingSession,
                            cancellationToken);
                    }
                    break;

                case BatchFinalizationEvent batchFinalizationEvent:
                    await HandleBatchFinalizationAsync(
                        batchFinalizationEvent,
                        _connectorsChunks,
                        cancellationToken);
                    break;

                case BatchFinalizedEvent batchFinalizedEvent:
                    if (batchFinalizedEvent.Id == _batchId)
                    {
                        IsComplete = true;
                        return true;
                    }
                    break;

                case BatchFailedEvent batchFailedEvent:
                    if (batchFailedEvent.Id == _batchId)
                    {
                        IsComplete = true;
                        throw new InvalidOperationException($"Batch failed: {batchFailedEvent.Reason}");
                    }
                    break;
            }

            return false;
        }
        catch
        {
            IsComplete = true;
            throw;
        }
    }

    /// <summary>
    /// Whether the batch session has completed (successfully or with failure)
    /// </summary>
    public bool IsComplete { get; private set; }

    private async Task<TreeSignerSession> HandleTreeSigningStartedAsync(
        TreeSigningStartedEvent signingEvent,
        uint256 sweepTapTreeRoot,
        List<TxTreeNode> vtxoChunks,
        CancellationToken cancellationToken)
    {

        // Build VTXO tree from chunks
        var vtxoGraph = TxTree.Create(vtxoChunks);

        // Validate the tree
        var commitmentTx = PSBT.Parse(signingEvent.UnsignedCommitmentTx, network);
        TreeValidator.ValidateVtxoTxGraph(vtxoGraph, commitmentTx, sweepTapTreeRoot);

        // Validate that all intent outputs exist in the correct locations
        ValidateIntentOutputs(vtxoGraph, commitmentTx);

        // Get shared output amount
        var sharedOutput = commitmentTx.Outputs[0];
        if (sharedOutput?.Value == null)
            throw new InvalidOperationException("Shared output not found in commitment transaction");


        // Create a signing session
        var session = new TreeSignerSession(walletProvider, vtxoGraph, sweepTapTreeRoot, _outputDescriptor, sharedOutput.Value);

        // Generate and submit nonces
        var nonces = await session.GetNoncesAsync(cancellationToken);
        var pubKey = OutputDescriptorHelpers.Extract(_outputDescriptor).PubKey;

        var request = new SubmitTreeNoncesRequest(
            signingEvent.Id,
            pubKey!.ToBytes().ToHexStringLower(),
            nonces.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.ToBytes().ToHexStringLower())
        );

        await clientTransport.SubmitTreeNoncesAsync(
            request,
            cancellationToken: cancellationToken);

        return session;
    }

    private async Task HandleAggregatedTreeNoncesEventAsync(
        TreeNoncesAggregatedEvent aggregatedEvent,
        TreeSignerSession session,
        CancellationToken cancellationToken)
    {
        // Process nonces in the session
        session.VerifyAggregatedNonces(
            aggregatedEvent.TreeNonces.ToDictionary(pair => uint256.Parse(pair.Key), pair => new MusigPubNonce(Encoders.Hex.DecodeData(pair.Value))), cancellationToken);

        // Sign and submit signatures
        var signatures = await session.SignAsync(cancellationToken);

        var pubKey = OutputDescriptorHelpers.Extract(_outputDescriptor).PubKey!;

        await clientTransport.SubmitTreeSignaturesRequest(
            new SubmitTreeSignaturesRequest(_batchId, pubKey.ToBytes().ToHexStringLower(),
                signatures.ToDictionary(pair => pair.Key.ToString(),
                    pair => pair.Value.ToBytes().ToHexStringLower())), cancellationToken);
    }

    private async Task HandleBatchFinalizationAsync(
        BatchFinalizationEvent finalizationEvent,
        List<TxTreeNode> connectorsChunks,
        CancellationToken cancellationToken)
    {
        // Build and validate connectors graph if present
        TxTree? connectorsGraph = null;
        if (connectorsChunks.Count > 0)
        {
            connectorsGraph = TxTree.Create(connectorsChunks);
            var commitmentPsbt = PSBT.Parse(finalizationEvent.CommitmentTx, network);
            TreeValidator.ValidateConnectorsTxGraph(commitmentPsbt, connectorsGraph);
        }

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var signedForfeits = new List<string>();

        // Get connector leaves for forfeit transactions
        var connectorsLeaves = connectorsGraph?.Leaves().ToList() ?? [];
        int connectorIndex = 0;

        foreach (var vtxoCoin in ins)
        {
            // Skip swept coins - they don't need forfeit transactions
            if (!vtxoCoin.RequiresForfeit())
            {
                continue;
            }

            // Check if we have enough connectors
            if (connectorsLeaves.Count == 0)
            {
                throw new InvalidOperationException("Connectors not received from operator");
            }

            if (connectorIndex >= connectorsLeaves.Count)
            {
                throw new InvalidOperationException(
                    $"Not enough connectors received. Need at least {connectorIndex + 1}, got {connectorsLeaves.Count}");
            }

            // Get the next connector leaf
            var connectorLeaf = connectorsLeaves[connectorIndex];
            var connectorOutput = connectorLeaf.Outputs.FirstOrDefault();

            if (connectorOutput == null)
            {
                throw new InvalidOperationException($"Connector leaf at index {connectorIndex} has no outputs");
            }

            // Create connector coin from the leaf transaction
            var connectorTxId = connectorLeaf.GetGlobalTransaction().GetHash()!;
            var connectorCoin = new Coin(
                new OutPoint(connectorTxId, 0),
                connectorLeaf.Outputs[0].GetTxOut());

            connectorIndex++;

            // Construct and sign forfeit transaction

            var forfeitTx = await arkTransactionBuilder.ConstructForfeitTx(
                serverInfo,
                vtxoCoin,
                connectorCoin,
                serverInfo.ForfeitAddress,
                cancellationToken);

            signedForfeits.Add(forfeitTx.ToBase64());
        }

        // Submit all signed forfeit transactions
        if (signedForfeits.Count > 0)
        {
            await clientTransport.SubmitSignedForfeitTxsAsync(
                new SubmitSignedForfeitTxsRequest(signedForfeits.ToArray()), cancellationToken);
        }
    }

    /// <summary>
    /// Validates that all outputs specified in the intent exist in the correct locations:
    /// - Onchain outputs must exist in the commitment transaction
    /// - Offchain outputs (VTXOs) must exist as leaves in the VTXO tree
    /// </summary>
    private void ValidateIntentOutputs(TxTree vtxoGraph, PSBT commitmentTx)
    {
        if (_intentParameters == null)
        {
            return;
        }

        // Parse the intent to get the outputs
        var intentOutputs = ParseIntentOutputs();
        if (intentOutputs.Count == 0)
        {
            return;
        }

        var onchainIndexes = new HashSet<int>(_intentParameters.OnchainOutputsIndexes ?? []);

        // Get all VTXO leaf outputs for validation
        var vtxoLeaves = vtxoGraph.Leaves().ToList();
        var vtxoLeafOutputs = vtxoLeaves
            .SelectMany(leaf => leaf.GetGlobalTransaction().Outputs
                .Select((output, idx) => new { Output = output, Tx = leaf, Index = idx }))
            .ToList();

        for (int i = 0; i < intentOutputs.Count; i++)
        {
            var output = intentOutputs[i];
            var isOnchain = onchainIndexes.Contains(i);

            if (isOnchain)
            {
                // Validate onchain output exists in commitment transaction
                var found = commitmentTx.Outputs.Any(txOut =>
                    txOut.ScriptPubKey == output.ScriptPubKey &&
                    txOut.Value == output.Value);

                if (!found)
                {
                    throw new InvalidOperationException(
                        $"Onchain output {i} not found in commitment transaction. " +
                        $"Expected: {output.Value} sats to {output.ScriptPubKey}");
                }
            }
            else
            {
                // Validate offchain output exists as a leaf in the VTXO tree
                var found = vtxoLeafOutputs.Any(leafOutput =>
                    leafOutput.Output.ScriptPubKey == output.ScriptPubKey &&
                    leafOutput.Output.Value == output.Value);

                if (!found)
                {
                    throw new InvalidOperationException(
                        $"Offchain output {i} not found in VTXO tree leaves. " +
                        $"Expected: {output.Value} sats to {output.ScriptPubKey}");
                }
            }
        }
    }

    /// <summary>
    /// Parses the intent outputs from the register proof transaction.
    /// The outputs are embedded directly in the BIP322 signature transaction
    /// (deviation from standard BIP322 to declare outputs).
    /// </summary>
    private List<TxOut> ParseIntentOutputs()
    {
        try
        {
            var registerProof = PSBT.Parse(arkIntent.RegisterProof, network);

            return registerProof.GetGlobalTransaction().Outputs;
        }
        catch
        {
            return [];
        }
    }

    private void HandleTreeTxEvent(TreeTxEvent treeTxEvent, List<TxTreeNode> vtxoChunks, List<TxTreeNode> connectorsChunks)
    {
        var txNode = new TxTreeNode(
            PSBT.Parse(treeTxEvent.Tx, network),
            treeTxEvent.Children.ToDictionary(kvp => (int)kvp.Key, kvp => uint256.Parse(kvp.Value))
        );

        if (treeTxEvent.BatchIndex == 0)
        {
            vtxoChunks.Add(txNode);
        }
        else if (treeTxEvent.BatchIndex == 1)
        {
            connectorsChunks.Add(txNode);
        }
    }
}