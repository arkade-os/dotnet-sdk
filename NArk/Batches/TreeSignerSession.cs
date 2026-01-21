using NArk.Abstractions.Helpers;
using NArk.Abstractions.Wallets;
using NArk.Extensions;
using NArk.Helpers;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Batches;

/// <summary>
/// Tree signer session implementation using IArkadeWalletSigner
/// </summary>
public class TreeSignerSession
{
    private Dictionary<uint256, (MusigPrivNonce secNonce, MusigPubNonce pubNonce)>? _myNonces;
    private Dictionary<uint256, MusigContext>? _musigContexts;
    private readonly IWalletProvider _walletProvider;
    private readonly TxTree _graph;
    private readonly uint256? _tapsciptMerkleRoot;
    private readonly OutputDescriptor _descriptor;
    private readonly Money _rootSharedOutputAmount;

    public TreeSignerSession(IWalletProvider walletProvider, TxTree tree, uint256? tapsciptMerkleRoot, OutputDescriptor descriptor, Money rootInputAmount)
    {
        _walletProvider = walletProvider;
        _graph = tree;
        _tapsciptMerkleRoot = tapsciptMerkleRoot;
        _descriptor = descriptor;
        _rootSharedOutputAmount = rootInputAmount;
    }

    private async Task CreateMusigContexts(CancellationToken cancellationToken = default)
    {
        if (_musigContexts != null)
            throw new InvalidOperationException("musig contexts already created");
        _musigContexts = new Dictionary<uint256, MusigContext>();
        var myPubKey = _descriptor.ToPubKey();
        foreach (var g in _graph)
        {
            var tx = g.Root.GetGlobalTransaction();

            // Extract cosigner keys for this transaction
            var cosignerKeys = g.Root.Inputs[0].GetArkFieldsCosigners()
                .OrderBy(data => data.Index)
                .Select(data => data.Key)
                .ToArray();

            if (cosignerKeys.All(key => key != myPubKey))
            {
                continue;
            }

            // Get prevout information and calculate sighash for this transaction
            var (prevoutAmount, prevoutScript) = GetPrevOutput(g, _graph);
            var execData = new TaprootExecutionData(0) { SigHash = TaprootSigHash.Default };
            var prevoutArray = new[] { new TxOut(Money.Satoshis(prevoutAmount), prevoutScript) };
            var sighash = tx.GetSignatureHashTaproot(prevoutArray, execData);

            // Create MUSIG context with the actual sighash that will be signed
            var musigContext = new MusigContext(cosignerKeys, sighash.ToBytes(), myPubKey);

            if (_tapsciptMerkleRoot is not null)
            {
                var taprootHash =
                    HashHelpers.CreateTaggedMessageHash("TapTweak",
                        musigContext.AggregatePubKey.ToXOnlyPubKey().ToBytes(),
                        _tapsciptMerkleRoot.ToBytes());
                musigContext.Tweak(taprootHash);
            }

            _musigContexts[tx.GetHash()] = musigContext;
        }
    }

    public async Task<Dictionary<uint256, MusigPubNonce>> GetNoncesAsync(CancellationToken cancellationToken = default)
    {
        _myNonces ??= await GenerateNoncesAsync(cancellationToken);

        return _myNonces.ToDictionary(pair => pair.Key, pair => pair.Value.pubNonce);
    }

    public void VerifyAggregatedNonces(Dictionary<uint256, MusigPubNonce> expectedAggregateNonces,
        CancellationToken cancellationToken = default)
    {
        if (_musigContexts is null)
            throw new InvalidOperationException("musig contexts not created");

        if (_myNonces is null)
            throw new InvalidOperationException("nonces not generated");

        if (_musigContexts.Any(musigContext => !expectedAggregateNonces[musigContext.Key].ToBytes().SequenceEqual(musigContext.Value.AggregateNonce!.ToBytes())))
            throw new InvalidOperationException("aggregated nonces do not match");
    }

    public async Task<Dictionary<uint256, MusigPartialSignature>> SignAsync(CancellationToken cancellationToken = default)
    {
        if (_graph == null)
            throw new InvalidOperationException("missing vtxo graph");
        if (_myNonces == null)
            throw new InvalidOperationException("nonces not generated");

        var sigs = new Dictionary<uint256, MusigPartialSignature>();
        foreach (var g in _graph)
        {
            var txid = g.Root.GetGlobalTransaction().GetHash();
            var sig = await SignPartialAsync(g, cancellationToken);
            sigs[txid] = sig;
        }

        return sigs;
    }

    private async Task<Dictionary<uint256, (MusigPrivNonce secNonce, MusigPubNonce pubNonce)>> GenerateNoncesAsync(CancellationToken cancellationToken = default)
    {
        if (_musigContexts == null)
            await CreateMusigContexts(cancellationToken);

        if (_myNonces != null)
            throw new InvalidOperationException("nonces already generated");

        var walletIdentifier = OutputDescriptorHelpers.Extract(_descriptor).WalletId;
        var signer = await _walletProvider.GetSignerAsync(walletIdentifier, cancellationToken);

        var res = new Dictionary<uint256, (MusigPrivNonce secNonce, MusigPubNonce pubNonce)>();
        foreach (var (txid, musigContext) in _musigContexts!)
        {
            // Generate nonce tied to this specific context
            var nonce = await signer!.GenerateNonces(_descriptor, musigContext, cancellationToken);
            res[txid] = (nonce, nonce.CreatePubNonce());
        }

        return res;
    }

    private async Task<MusigPartialSignature> SignPartialAsync(TxTree g, CancellationToken cancellationToken)
    {

        if (_myNonces == null || _musigContexts == null)
            throw new InvalidOperationException("session not properly initialized");

        var txid = g.Root.GetGlobalTransaction().GetHash();

        if (!_myNonces.TryGetValue(txid, out var myNonce))
            throw new InvalidOperationException("missing private nonce");

        if (!_musigContexts.TryGetValue(txid, out var musigContext))
            throw new InvalidOperationException("missing musig context");

        if (musigContext.AggregateNonce is null)
            throw new InvalidOperationException("missing aggregate nonce");

        var walletIdentifier = OutputDescriptorHelpers.Extract(_descriptor).WalletId;
        var signer = await _walletProvider.GetSignerAsync(walletIdentifier, cancellationToken);

        // Use the wallet signer to create a MUSIG2 partial signature
        // The context already has the correct sighash from nonce generation
        var partialSig = await signer!.SignMusig(_descriptor, musigContext, myNonce.secNonce, cancellationToken);

        return partialSig;
    }

    /// <summary>
    /// Gets the previous output information for a transaction in the tree
    /// Matches TypeScript getPrevOutput function (lines 215-250)
    /// </summary>
    private (long amount, Script script) GetPrevOutput(TxTree g, TxTree rootGraph)
    {
        // Extract cosigner keys and aggregate with taproot tweak to get final key
        var cosignerKeys = g.Root.Inputs[0].GetArkFieldsCosigners()
            .OrderBy(data => data.Index)
            .Select(data => data.Key)
            .ToArray();

        // Aggregate keys with taproot tweak (matches TypeScript lines 125-127)
        var aggregatedKey = ECPubKey.MusigAggregate(cosignerKeys);
        if (_tapsciptMerkleRoot == null)
            throw new InvalidOperationException("script root not set");


        // Generate P2TR script from final key (matches TypeScript line 222)
        var taprootFinalKey =
            TaprootFullPubKey.Create(new TaprootInternalPubKey(aggregatedKey.ToXOnlyPubKey().ToBytes()), _tapsciptMerkleRoot);

        var txid = g.Root.GetGlobalTransaction().GetHash();

        // If this is the root transaction, return shared output amount (matches TypeScript lines 227-232)
        if (txid == rootGraph.Root.GetGlobalTransaction().GetHash())
        {
            return (_rootSharedOutputAmount, taprootFinalKey.ScriptPubKey);
        }

        // Find parent transaction (matches TypeScript lines 234-242)
        var tx = g.Root.GetGlobalTransaction();
        var parentInput = tx.Inputs[0];
        var parentTxid = parentInput.PrevOut.Hash;

        var parent = rootGraph.Find(parentTxid);
        if (parent == null)
            throw new InvalidOperationException($"parent tx not found: {parentTxid}");

        var parentOutput = parent.Root.GetGlobalTransaction().Outputs[(int)parentInput.PrevOut.N];
        if (parentOutput == null)
            throw new InvalidOperationException("parent output not found");

        return (parentOutput.Value.Satoshi, taprootFinalKey.ScriptPubKey);
    }

    public Task AggregateNonces(uint256 txid, MusigPubNonce[] nonces, CancellationToken cancellationToken)
    {
        if (_musigContexts == null)
            throw new InvalidOperationException("musig contexts not created");

        if (!_musigContexts.TryGetValue(txid, out var musigContext))
            throw new InvalidOperationException("missing musig context");

        if (_myNonces is null || !_myNonces.TryGetValue(txid, out var myNonce))
            throw new InvalidOperationException("missing private nonce");

        if (!nonces.Any(nonce => nonce.ToBytes().SequenceEqual(myNonce.pubNonce.ToBytes())))
        {
            throw new InvalidOperationException("missing my nonce");
        }

        musigContext.ProcessNonces(nonces);

        return Task.CompletedTask;
    }
}