using System.Net.Http.Json;
using System.Text.Json;
using NArk.Core;
using NArk.Core.Scripts;
using NArk.Core.Transport.Extensions;
using NBitcoin;

namespace NArk.Transport.RestClient;

public partial class RestClientTransport
{
    private static Sequence ParseSequence(long val)
    {
        return val >= 512 ? new Sequence(TimeSpan.FromSeconds(val)) : new Sequence((int)val);
    }

    public async Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        var json = await _http.GetFromJsonAsync<JsonElement>("/v1/info", JsonOpts, cancellationToken);

        var networkStr = json.GetProperty("network").GetString()!;
        var network = Network.GetNetwork(networkStr) ?? (networkStr == "bitcoin" ? Network.Main
            : throw new InvalidOperationException("Ark server advertises unknown network"));

        var checkpointTapscript = json.GetProperty("checkpoint_tapscript").GetString()!;
        var serverUnrollScript = UnilateralPathArkTapScript.Parse(checkpointTapscript);

        var signerPubkey = json.GetProperty("signer_pubkey").GetString()!;
        var forfeitPubkey = json.GetProperty("forfeit_pubkey").GetString()!;
        var fPubKey = forfeitPubkey.ToECXOnlyPubKey();

        var deprecatedSigners = new Dictionary<NBitcoin.Secp256k1.ECXOnlyPubKey, long>();
        if (json.TryGetProperty("deprecated_signers", out var ds) && ds.ValueKind == JsonValueKind.Array)
        {
            foreach (var signer in ds.EnumerateArray())
            {
                var pk = signer.GetProperty("pubkey").GetString()!.ToECXOnlyPubKey();
                var cutoff = signer.GetProperty("cutoff_date").GetInt64();
                deprecatedSigners[pk] = cutoff;
            }
        }

        // Parse fees
        var fees = json.TryGetProperty("fees", out var feesEl) ? feesEl : default;
        var intentFee = fees.ValueKind == JsonValueKind.Object && fees.TryGetProperty("intent_fee", out var ifEl) ? ifEl : default;

        return new ArkServerInfo(
            Dust: Money.Satoshis(json.GetProperty("dust").GetInt64()),
            SignerKey: PubKeyExtensions.ParseOutputDescriptor(signerPubkey, network),
            DeprecatedSigners: deprecatedSigners,
            Network: network,
            UnilateralExit: ParseSequence(json.GetProperty("unilateral_exit_delay").GetInt64()),
            BoardingExit: ParseSequence(json.GetProperty("boarding_exit_delay").GetInt64()),
            ForfeitAddress: BitcoinAddress.Create(json.GetProperty("forfeit_address").GetString()!, network),
            ForfeitPubKey: fPubKey,
            CheckpointTapScript: serverUnrollScript,
            FeeTerms: new ArkOperatorFeeTerms(
                TxFeeRate: GetJsonStringOrZero(fees, "tx_fee_rate"),
                IntentOffchainOutput: GetJsonStringOrZero(intentFee, "offchain_output"),
                IntentOnchainOutput: GetJsonStringOrZero(intentFee, "onchain_output"),
                IntentOffchainInput: GetJsonStringOrZero(intentFee, "offchain_input"),
                IntentOnchainInput: GetJsonStringOrZero(intentFee, "onchain_input")
            ),
            MaxTxWeight: json.TryGetProperty("max_tx_weight", out var mtw) ? mtw.GetInt64() : 0,
            MaxOpReturnOutputs: json.TryGetProperty("max_op_return_outputs", out var mor) ? (int)mor.GetInt64() : 0,
            VtxoMinAmount: Money.Satoshis(GetJsonInt64OrDefault(json, "vtxo_min_amount")),
            VtxoMaxAmount: ParseAmountLimit(GetJsonInt64OrDefault(json, "vtxo_max_amount", -1)),
            UtxoMinAmount: Money.Satoshis(GetJsonInt64OrDefault(json, "utxo_min_amount")),
            UtxoMaxAmount: ParseAmountLimit(GetJsonInt64OrDefault(json, "utxo_max_amount", -1))
        );
    }

    private static string GetJsonStringOrZero(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return "0.0";
        if (!el.TryGetProperty(prop, out var val)) return "0.0";
        var s = val.GetString();
        return string.IsNullOrWhiteSpace(s) ? "0.0" : s;
    }

    private static long GetJsonInt64OrDefault(JsonElement el, string prop, long defaultValue = 0)
    {
        return el.TryGetProperty(prop, out var val) ? val.GetInt64() : defaultValue;
    }

    private static Money ParseAmountLimit(long value)
    {
        return value < 0 ? Money.Coins(21_000_000m) : Money.Satoshis(value);
    }
}
