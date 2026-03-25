using System.Text.Json;
using NArk.Abstractions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Wallets;
using NBitcoin;

namespace NArk.Core.Helpers;

/// <summary>
/// Creates BIP-322-style proofs for intent operations (registration, deletion, and querying).
/// </summary>
public static class IntentProofHelper
{
    /// <summary>
    /// Creates a signed BIP-322 proof that can be used with <see cref="Transport.IClientTransport.GetIntentsByProofAsync"/>
    /// to retrieve intents registered by the owner of the given coin.
    /// </summary>
    /// <param name="coin">The coin whose script ownership is being proved</param>
    /// <param name="signer">Wallet signer for the coin</param>
    /// <param name="network">Bitcoin network</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple of (base64-encoded PSBT proof, JSON message string)</returns>
    public static async Task<(string Proof, string Message)> CreateIntentOwnershipProofAsync(
        ArkCoin coin,
        IArkadeWalletSigner signer,
        Network network,
        CancellationToken cancellationToken = default)
    {
        var message = JsonSerializer.Serialize(new
        {
            type = "register",
            cosigners_public_keys = Array.Empty<string>(),
            valid_at = 0,
            expire_at = 0
        });

        var psbt = CreateBip322Psbt(message, network, coin);
        await SignBip322Proof(psbt, coin, signer, network, cancellationToken);

        return (psbt.ToBase64(), message);
    }

    /// <summary>
    /// Creates the unsigned BIP-322-style PSBT structure (toSpend → toSign) for a single coin.
    /// Reused by delegation and intent proof flows.
    /// </summary>
    internal static PSBT CreateBip322Psbt(string message, Network network, ArkCoin coin)
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

    /// <summary>
    /// Signs both inputs of a BIP-322 proof PSBT (input[0] = toSpend reference, input[1] = real coin).
    /// </summary>
    internal static async Task SignBip322Proof(PSBT psbt, ArkCoin coin, IArkadeWalletSigner signer,
        Network network, CancellationToken cancellationToken = default)
    {
        var gtx = psbt.GetGlobalTransaction();

        // Clone coin for input[0] (BIP322 toSpend reference)
        var bip322Coin = new ArkCoin(coin);
        bip322Coin.TxOut = psbt.Inputs[0].GetTxOut()!;
        bip322Coin.Outpoint = psbt.Inputs[0].PrevOut;

        var precomputed = gtx.PrecomputeTransactionData([bip322Coin.TxOut, coin.TxOut]);

        // Reconstruct PSBT so both inputs have coin data
        psbt = PSBT.FromTransaction(gtx, network).UpdateFrom(psbt);

        await PsbtHelpers.SignAndFillPsbt(signer, bip322Coin, psbt, precomputed,
            cancellationToken: cancellationToken);
        await PsbtHelpers.SignAndFillPsbt(signer, coin, psbt, precomputed,
            cancellationToken: cancellationToken);
    }
}
