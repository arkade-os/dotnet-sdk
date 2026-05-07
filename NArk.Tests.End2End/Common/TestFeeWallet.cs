using System.Globalization;
using System.Text.Json;
using NArk.Abstractions.Blockchain;
using NBitcoin;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Test-side <see cref="IFeeWallet"/> backed by a freshly-generated P2TR
/// key funded via <c>bitcoin-cli sendtoaddress</c>. Provides the on-chain
/// UTXO + signing key the unilateral-exit broadcaster needs to wrap each
/// virtual tx in a 1p1c CPFP package via <c>submitpackage</c> — without
/// it, tree-tx broadcasts hit TRUC-violation because their parent has a
/// 0-sat P2A anchor and no fee.
/// </summary>
internal sealed class TestFeeWallet : IFeeWallet
{
    private readonly Script _scriptPubKey;
    private readonly string _address;
    private readonly List<FeeCoin> _availableCoins = [];

    private TestFeeWallet(Script scriptPubKey, string address)
    {
        _scriptPubKey = scriptPubKey;
        _address = address;
    }

    public string Address => _address;

    /// <summary>
    /// Generates a key, derives a regtest P2TR (BIP-86 keypath-only) address,
    /// faucets it with <paramref name="fundAmountBtc"/> via
    /// <c>bitcoin-cli sendtoaddress</c>, mines a block, then resolves the
    /// resulting UTXO via <c>getrawtransaction</c> so we have an OutPoint +
    /// value to hand back via <see cref="SelectFeeUtxoAsync"/>.
    ///
    /// BIP-86 (no script tree) is what <c>P2ACpfpBuilder.BuildCpfpChild</c>
    /// signs against — it calls <c>SignTaprootKeySpend</c> with the default
    /// (null) merkle root, producing the BIP-86 output-key tweak. Deriving
    /// the address via <c>ScriptPubKeyType.TaprootBIP86</c> matches.
    /// </summary>
    public static async Task<TestFeeWallet> CreateFundedAsync(
        decimal fundAmountBtc = 0.01m,
        CancellationToken ct = default)
    {
        var key = new Key();
        var scriptPubKey = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var address = scriptPubKey.GetDestinationAddress(Network.RegTest)
            ?? throw new InvalidOperationException("TestFeeWallet: failed to derive P2TR address from BIP86 scriptPubKey");
        var wallet = new TestFeeWallet(scriptPubKey, address.ToString());

        var btcAmount = fundAmountBtc.ToString("0.########", CultureInfo.InvariantCulture);
        var fundTxid = (await DockerHelper.Exec(
            "bitcoin", ["bitcoin-cli", "-rpcwallet=", "sendtoaddress", wallet.Address, btcAmount], ct)).Trim();
        if (string.IsNullOrEmpty(fundTxid))
            throw new InvalidOperationException("TestFeeWallet: bitcoin-cli sendtoaddress returned empty txid");

        // Mine 1 block so the UTXO is confirmed (CPFP child needs a
        // confirmed-or-mempool parent input; confirmed is simpler).
        await DockerHelper.MineBlocks(1, ct);

        // Resolve the vout in the funding tx that pays our address.
        var rawTx = (await DockerHelper.Exec(
            "bitcoin", ["bitcoin-cli", "-rpcwallet=", "getrawtransaction", fundTxid, "1"], ct)).Trim();
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

        wallet._availableCoins.Add(new FeeCoin(
            new OutPoint(uint256.Parse(fundTxid), (uint)matchedVout),
            new TxOut(amount, wallet._scriptPubKey),
            key));
        return wallet;
    }

    public Task<FeeCoin?> SelectFeeUtxoAsync(Money minAmount, CancellationToken cancellationToken = default)
    {
        // Trivial selection: first coin >= minAmount. Tests use a single
        // funding round, so there's at most a couple of coins to choose from.
        FeeCoin? pick = null;
        for (var i = 0; i < _availableCoins.Count; i++)
        {
            if (_availableCoins[i].TxOut.Value >= minAmount)
            {
                pick = _availableCoins[i];
                _availableCoins.RemoveAt(i);
                break;
            }
        }
        return Task.FromResult(pick);
    }

    public Task<Script> GetChangeScriptAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_scriptPubKey);
}
