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

        var networkStr = GetString(json, "network");
        var network = Network.GetNetwork(networkStr) ?? (networkStr == "bitcoin" ? Network.Main
            : throw new InvalidOperationException("Ark server advertises unknown network"));

        var checkpointTapscript = GetString(json, "checkpoint_tapscript", "checkpointTapscript");
        var serverUnrollScript = UnilateralPathArkTapScript.Parse(checkpointTapscript);

        var signerPubkey = GetString(json, "signer_pubkey", "signerPubkey");
        var forfeitPubkey = GetString(json, "forfeit_pubkey", "forfeitPubkey");
        var fPubKey = forfeitPubkey.ToECXOnlyPubKey();

        var deprecatedSigners = new Dictionary<NBitcoin.Secp256k1.ECXOnlyPubKey, long>();
        if (TryGetProp(json, "deprecated_signers", "deprecatedSigners", out var ds) && ds.ValueKind == JsonValueKind.Array)
        {
            foreach (var signer in ds.EnumerateArray())
            {
                var pk = GetString(signer, "pubkey").ToECXOnlyPubKey();
                var cutoff = GetInt64(signer, "cutoff_date", "cutoffDate");
                deprecatedSigners[pk] = cutoff;
            }
        }

        // Parse fees — handle both snake_case and camelCase
        JsonElement fees = default;
        TryGetProp(json, "fees", "fees", out fees);
        JsonElement intentFee = default;
        if (fees.ValueKind == JsonValueKind.Object)
            TryGetProp(fees, "intent_fee", "intentFee", out intentFee);

        return new ArkServerInfo(
            Dust: Money.Satoshis(GetInt64(json, "dust")),
            SignerKey: PubKeyExtensions.ParseOutputDescriptor(signerPubkey, network),
            DeprecatedSigners: deprecatedSigners,
            Network: network,
            UnilateralExit: ParseSequence(GetInt64(json, "unilateral_exit_delay", "unilateralExitDelay")),
            BoardingExit: ParseSequence(GetInt64(json, "boarding_exit_delay", "boardingExitDelay")),
            ForfeitAddress: BitcoinAddress.Create(GetString(json, "forfeit_address", "forfeitAddress"), network),
            ForfeitPubKey: fPubKey,
            CheckpointTapScript: serverUnrollScript,
            FeeTerms: new ArkOperatorFeeTerms(
                TxFeeRate: GetJsonStringOrZero(fees, "tx_fee_rate", "txFeeRate"),
                IntentOffchainOutput: GetJsonStringOrZero(intentFee, "offchain_output", "offchainOutput"),
                IntentOnchainOutput: GetJsonStringOrZero(intentFee, "onchain_output", "onchainOutput"),
                IntentOffchainInput: GetJsonStringOrZero(intentFee, "offchain_input", "offchainInput"),
                IntentOnchainInput: GetJsonStringOrZero(intentFee, "onchain_input", "onchainInput")
            ),
            MaxTxWeight: GetInt64Optional(json, "max_tx_weight", "maxTxWeight"),
            MaxOpReturnOutputs: (int)GetInt64Optional(json, "max_op_return_outputs", "maxOpReturnOutputs"),
            VtxoMinAmount: Money.Satoshis(GetInt64Optional(json, "vtxo_min_amount", "vtxoMinAmount")),
            VtxoMaxAmount: ParseAmountLimit(GetInt64Optional(json, "vtxo_max_amount", "vtxoMaxAmount", -1)),
            UtxoMinAmount: Money.Satoshis(GetInt64Optional(json, "utxo_min_amount", "utxoMinAmount")),
            UtxoMaxAmount: ParseAmountLimit(GetInt64Optional(json, "utxo_max_amount", "utxoMaxAmount", -1))
        );
    }

    /// <summary>
    /// Gets a string property, checking snake_case then camelCase.
    /// </summary>
    private static string GetString(JsonElement el, string snakeCase, string? camelCase = null)
    {
        if (el.TryGetProperty(snakeCase, out var v)) return v.GetString()!;
        if (camelCase is not null && el.TryGetProperty(camelCase, out v)) return v.GetString()!;
        return el.GetProperty(snakeCase).GetString()!; // throw with original name
    }

    /// <summary>
    /// Gets an int64, handling both numeric and string-encoded values (proto3 JSON),
    /// checking snake_case then camelCase property names.
    /// </summary>
    private static long GetInt64(JsonElement el, string snakeCase, string? camelCase = null)
    {
        JsonElement v;
        if (!el.TryGetProperty(snakeCase, out v))
        {
            if (camelCase is null || !el.TryGetProperty(camelCase, out v))
                v = el.GetProperty(snakeCase); // throw with original name
        }
        return v.ValueKind == JsonValueKind.String ? long.Parse(v.GetString()!) : v.GetInt64();
    }

    /// <summary>
    /// Gets an optional int64, returning defaultValue if the property doesn't exist.
    /// </summary>
    private static long GetInt64Optional(JsonElement el, string snakeCase, string? camelCase = null, long defaultValue = 0)
    {
        if (el.TryGetProperty(snakeCase, out var v) || (camelCase is not null && el.TryGetProperty(camelCase, out v)))
        {
            return v.ValueKind == JsonValueKind.String ? long.Parse(v.GetString()!) : v.GetInt64();
        }
        return defaultValue;
    }

    private static string GetJsonStringOrZero(JsonElement el, string snakeCase, string? camelCase = null)
    {
        if (el.ValueKind != JsonValueKind.Object) return "0.0";
        JsonElement val;
        if (!el.TryGetProperty(snakeCase, out val) && (camelCase is null || !el.TryGetProperty(camelCase, out val)))
            return "0.0";
        var s = val.GetString();
        return string.IsNullOrWhiteSpace(s) ? "0.0" : s;
    }

    private static Money ParseAmountLimit(long value)
    {
        return value < 0 ? Money.Coins(21_000_000m) : Money.Satoshis(value);
    }
}
