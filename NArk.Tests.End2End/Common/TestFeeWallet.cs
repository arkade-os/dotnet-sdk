using System.Globalization;
using System.Text.Json;
using NArk.Abstractions.Blockchain;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Test-side <see cref="IFeeWallet"/> backed by a freshly-generated P2TR
/// key funded via <c>bitcoin-cli sendtoaddress</c>. Provides the on-chain
/// UTXO + signing capability the unilateral-exit broadcaster needs to wrap
/// each virtual tx in a 1p1c CPFP package via <c>submitpackage</c> — without
/// it, tree-tx broadcasts hit TRUC-violation because their parent has a
/// 0-sat P2A anchor and no fee.
/// <para>
/// Signing happens via the <see cref="IFeeWallet.SignFeeUtxoAsync"/> callback —
/// the wallet keeps the <see cref="Key"/> internal and only emits
/// <see cref="SecpSchnorrSignature"/>s. Production implementations can hide
/// the key behind a hardware wallet, HSM, BTCPay-managed signer, etc.
/// </para>
/// </summary>
internal sealed class TestFeeWallet : IFeeWallet
{
    private readonly Script _scriptPubKey;
    private readonly string _address;
    private readonly Key _signingKey;
    private readonly Dictionary<OutPoint, Coin> _availableUtxos = new();

    private TestFeeWallet(Script scriptPubKey, string address, Key signingKey)
    {
        _scriptPubKey = scriptPubKey;
        _address = address;
        _signingKey = signingKey;
    }

    public string Address => _address;

    /// <summary>
    /// Generates a key, derives a regtest P2TR (BIP-86 keypath-only) address,
    /// faucets it with <paramref name="fundAmountBtc"/> via
    /// <c>bitcoin-cli sendtoaddress</c>, mines a block, then resolves the
    /// resulting UTXO via <c>getrawtransaction</c> so we have an OutPoint +
    /// value to hand back via <see cref="SelectFeeUtxoAsync"/>.
    /// </summary>
    public static async Task<TestFeeWallet> CreateFundedAsync(
        decimal fundAmountBtc = 0.01m,
        CancellationToken ct = default)
    {
        var key = new Key();
        var scriptPubKey = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var address = scriptPubKey.GetDestinationAddress(Network.RegTest)
            ?? throw new InvalidOperationException("TestFeeWallet: failed to derive P2TR address from BIP86 scriptPubKey");
        var wallet = new TestFeeWallet(scriptPubKey, address.ToString(), key);

        var fundTxid = await DockerHelper.BitcoinSendToAddress(
            wallet.Address, Money.FromUnit(fundAmountBtc, MoneyUnit.BTC), ct);
        if (string.IsNullOrEmpty(fundTxid))
            throw new InvalidOperationException("TestFeeWallet: bitcoin-cli sendtoaddress returned empty txid");

        await DockerHelper.MineBlocks(1, ct);

        var rawTx = await DockerHelper.BitcoinCli(
            ["getrawtransaction", fundTxid, "1"], ct);
        var doc = JsonDocument.Parse(rawTx);
        var matchedVout = -1;
        Money? amount = null;
        foreach (var vout in doc.RootElement.GetProperty("vout").EnumerateArray())
        {
            var spk = vout.GetProperty("scriptPubKey");
            if (spk.TryGetProperty("address", out var addr)
                && addr.GetString() == wallet.Address)
            {
                matchedVout = vout.GetProperty("n").GetInt32();
                amount = Money.Coins(vout.GetProperty("value").GetDecimal());
                break;
            }
        }
        if (matchedVout < 0 || amount is null)
            throw new InvalidOperationException(
                $"TestFeeWallet: couldn't find vout paying {wallet.Address} in tx {fundTxid}");

        var outpoint = new OutPoint(uint256.Parse(fundTxid), (uint)matchedVout);
        wallet._availableUtxos[outpoint] = new Coin(outpoint, new TxOut(amount, wallet._scriptPubKey));
        return wallet;
    }

    public Task<ICoin?> SelectFeeUtxoAsync(Money minAmount, CancellationToken cancellationToken = default)
    {
        // Trivial selection: first UTXO >= minAmount. Reserved UTXOs stay in
        // the dictionary so SignFeeUtxoAsync can validate the outpoint belongs
        // to this wallet, but are flagged as no longer selectable.
        foreach (var kvp in _availableUtxos)
        {
            if (kvp.Value.TxOut.Value >= minAmount)
                return Task.FromResult<ICoin?>(kvp.Value);
        }
        return Task.FromResult<ICoin?>(null);
    }

    public Task<SecpSchnorrSignature> SignFeeUtxoAsync(
        OutPoint feeOutpoint,
        uint256 sighash,
        TaprootSigHash sighashType,
        CancellationToken cancellationToken = default)
    {
        if (!_availableUtxos.ContainsKey(feeOutpoint))
            throw new InvalidOperationException(
                $"TestFeeWallet: asked to sign for outpoint {feeOutpoint} which wasn't issued by this wallet");

        // NBitcoin returns its own TaprootSignature wrapper; pull the raw
        // 64-byte Schnorr signature out of it for the IFeeWallet contract.
        var taprootSig = _signingKey.SignTaprootKeySpend(sighash, sighashType);
        var sig = SecpSchnorrSignature.TryCreate(taprootSig.SchnorrSignature.ToBytes(), out var parsed)
            ? parsed!
            : throw new InvalidOperationException("TestFeeWallet: failed to re-parse signature bytes");
        return Task.FromResult(sig);
    }

    public Task<Script> GetChangeScriptAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_scriptPubKey);
}
