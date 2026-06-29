using System.Net.Http.Json;
using System.Text.Json;
using NArk.Abstractions.Extensions;
using NArk.Core;
using NArk.Core.Extensions;
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
        var network = NetworkExtensions.ResolveArkNetwork(networkStr);

        var checkpointTapscript = GetString(json, "checkpoint_tapscript");
        var serverUnrollScript = UnilateralPathArkTapScript.Parse(checkpointTapscript);

        var signerPubkey = GetString(json, "signer_pubkey");
        var forfeitPubkey = GetString(json, "forfeit_pubkey");
        var fPubKey = forfeitPubkey.ToECXOnlyPubKey();

        var deprecatedSigners = new Dictionary<NBitcoin.Secp256k1.ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance);
        if (json.TryGetPropInvariantCase("deprecated_signers", out var ds) && ds.ValueKind == JsonValueKind.Array)
        {
            foreach (var signer in ds.EnumerateArray())
            {
                var pk = GetString(signer, "pubkey").ToECXOnlyPubKey();
                var cutoff = GetInt64(signer, "cutoff_date");
                deprecatedSigners[pk] = cutoff;
            }
        }

        // Fees envelope is optional and nested; tolerate either property spelling.
        json.TryGetPropInvariantCase("fees", out var fees);
        JsonElement intentFee = default;
        if (fees.ValueKind == JsonValueKind.Object)
            fees.TryGetPropInvariantCase("intent_fee", out intentFee);

        var digest = json.TryGetPropInvariantCase("digest", out var digestEl) ? digestEl.GetString() ?? "" : "";

        var result = new ArkServerInfo(
            Dust: Money.Satoshis(GetInt64(json, "dust")),
            SignerKey: PubKeyExtensions.ParseOutputDescriptor(signerPubkey, network),
            DeprecatedSigners: deprecatedSigners,
            Network: network,
            UnilateralExit: ParseSequence(GetInt64(json, "unilateral_exit_delay")),
            BoardingExit: ParseSequence(GetInt64(json, "boarding_exit_delay")),
            ForfeitAddress: BitcoinAddress.Create(GetString(json, "forfeit_address"), network),
            ForfeitPubKey: fPubKey,
            CheckpointTapScript: serverUnrollScript,
            Digest: digest,
            FeeTerms: new ArkOperatorFeeTerms(
                TxFeeRate: GetJsonStringOrZero(fees, "tx_fee_rate"),
                IntentOffchainOutput: GetJsonStringOrZero(intentFee, "offchain_output"),
                IntentOnchainOutput: GetJsonStringOrZero(intentFee, "onchain_output"),
                IntentOffchainInput: GetJsonStringOrZero(intentFee, "offchain_input"),
                IntentOnchainInput: GetJsonStringOrZero(intentFee, "onchain_input")
            ),
            MaxTxWeight: GetInt64Optional(json, "max_tx_weight"),
            MaxOpReturnOutputs: (int)GetInt64Optional(json, "max_op_return_outputs"),
            VtxoMinAmount: Money.Satoshis(GetInt64Optional(json, "vtxo_min_amount")),
            VtxoMaxAmount: ParseAmountLimit(GetInt64Optional(json, "vtxo_max_amount", -1)),
            UtxoMinAmount: Money.Satoshis(GetInt64Optional(json, "utxo_min_amount")),
            UtxoMaxAmount: ParseAmountLimit(GetInt64Optional(json, "utxo_max_amount", -1))
        );
        _digestHolder.Digest = result.Digest;
        return result;
    }

    /// <summary>
    /// Gets a string property by its snake_case name (camelCase spelling handled by the
    /// shared <see cref="JsonExtensions.GetPropInvariantCase"/>).
    /// </summary>
    private static string GetString(JsonElement el, string snakeCase)
        => el.GetPropInvariantCase(snakeCase).GetString()!;

    /// <summary>
    /// Gets an int64, handling both numeric and string-encoded values (proto3 JSON
    /// encodes int64 as a string to avoid JS precision loss).
    /// </summary>
    private static long GetInt64(JsonElement el, string snakeCase)
    {
        var v = el.GetPropInvariantCase(snakeCase);
        return v.ValueKind == JsonValueKind.String ? long.Parse(v.GetString()!) : v.GetInt64();
    }

    /// <summary>
    /// Gets an optional int64, returning <paramref name="defaultValue"/> if the property doesn't exist.
    /// </summary>
    private static long GetInt64Optional(JsonElement el, string snakeCase, long defaultValue = 0)
    {
        if (el.TryGetPropInvariantCase(snakeCase, out var v))
            return v.ValueKind == JsonValueKind.String ? long.Parse(v.GetString()!) : v.GetInt64();
        return defaultValue;
    }

    private static string GetJsonStringOrZero(JsonElement el, string snakeCase)
    {
        if (el.ValueKind != JsonValueKind.Object) return "0.0";
        if (!el.TryGetPropInvariantCase(snakeCase, out var val)) return "0.0";
        var s = val.GetString();
        return string.IsNullOrWhiteSpace(s) ? "0.0" : s;
    }

    private static Money ParseAmountLimit(long value)
    {
        return value < 0 ? Money.Coins(21_000_000m) : Money.Satoshis(value);
    }
}
