using Cel;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Fees;

/// <summary>
/// Evaluates the Arkade operator's CEL intent-fee programs to predict the fee arkd will
/// charge for an intent.
/// </summary>
/// <remarks>
/// This is a client-side mirror of arkd's <c>arkFeeManager.ComputeIntentFees</c>: every
/// variable binding, the offchain/onchain input split, and the rounding must match, because
/// arkd rejects an intent whose inputs minus outputs fall short of its own number
/// (<c>INTENT_INSUFFICIENT_FEE</c>) and silently keeps any excess.
/// <para><paramref name="blockchain"/> is no longer read — input types are classified the way
/// arkd classifies them, on the swept flag alone, so no chain-time lookup is needed. The
/// parameter stays for source compatibility with callers that construct this directly.</para>
/// </remarks>
public class DefaultFeeEstimator(IClientTransport clientTransport, IBitcoinBlockchain blockchain) : IFeeEstimator
{
    private readonly ICelEnvironment _celEnvironment = CreateCelEnvironment();

    /// <summary>
    /// Builds the CEL environment arkd's programs are written against. arkd's celenv registers a
    /// <c>now()</c> overload returning the current unix time in seconds (celenv/functions.go);
    /// without it, every time-based fee program — the shape arkd's own README documents — dies
    /// with an undeclared-reference error instead of pricing the intent.
    /// </summary>
    private static ICelEnvironment CreateCelEnvironment()
    {
        var environment = new CelEnvironment(null, null);
        environment.RegisterFunction("now", [], _ => (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return environment;
    }

    /// <inheritdoc />
    public Task<Money> EstimateFeeAsync(ArkCoin[] coins, ArkTxOut[] outputs,
        CancellationToken cancellationToken = default) =>
        EstimateFeeAsync(new ArkIntentSpec(coins, outputs, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)),
            cancellationToken);

    /// <inheritdoc />
    public async Task<Money> EstimateFeeAsync(ArkIntentSpec spec, CancellationToken cancellationToken = default)
    {
        var info = await clientTransport.GetServerInfoAsync(cancellationToken);

        var offchainInputFeeFunc = _celEnvironment.Compile(info.FeeTerms.IntentOffchainInput);
        var onchainInputFeeFunc = _celEnvironment.Compile(info.FeeTerms.IntentOnchainInput);
        var inputFees = spec.Coins.Sum(coin => IsOnchainInput(coin)
            ? GetOnchainInputFee(onchainInputFeeFunc, coin)
            : GetOffchainInputFee(offchainInputFeeFunc, coin));

        var offchainOutputFeeFunc = _celEnvironment.Compile(info.FeeTerms.IntentOffchainOutput);
        var onchainOutputFeeFunc = _celEnvironment.Compile(info.FeeTerms.IntentOnchainOutput);
        var outputFees = spec.Outputs.Sum(o => GetOutputFee(
            o.Type == ArkTxOutType.Vtxo ? offchainOutputFeeFunc : onchainOutputFeeFunc, o));

        // arkd accumulates every term as a float64 and rounds the *total* up once
        // (arkfee.FeeAmount.ToSatoshis), so rounding per term here would overpay.
        return Money.Satoshis(Convert.ToInt64(Math.Ceiling(inputFees + outputFees)));
    }

    /// <summary>
    /// True when arkd prices this coin with the onchain-input program: only boarding UTXOs
    /// enter an intent as onchain inputs. Swept and unrolled VTXOs sit on-chain too, but arkd
    /// still carries them as <c>domain.Vtxo</c> and prices them offchain.
    /// </summary>
    private static bool IsOnchainInput(ArkCoin coin) =>
        coin.Contract.Type == ArkBoardingContract.ContractType;

    private double GetOutputFee(CelProgramDelegate feeFunc, ArkTxOut txOut)
    {
        var vars = new Dictionary<string, object?>
        {
            { "amount", Convert.ToDouble(txOut.Value.Satoshi) },
            { "script", txOut.ScriptPubKey.ToHex() }
        };

        return Convert.ToDouble(feeFunc.Invoke(vars)!);
    }

    /// <summary>Mirrors arkd's <c>IntentOnchainInputEnv</c>, which exposes <c>amount</c> only.</summary>
    private double GetOnchainInputFee(CelProgramDelegate onchainInputFeeFunc, ArkCoin arkCoin)
    {
        var vars = new Dictionary<string, object?>
        {
            { "amount", Convert.ToDouble(arkCoin.Amount.Satoshi) }
        };

        return Convert.ToDouble(onchainInputFeeFunc.Invoke(vars)!);
    }

    private double GetOffchainInputFee(CelProgramDelegate offchainInputFeeFunc, ArkCoin arkCoin)
    {
        var vars = new Dictionary<string, object?>
        {
            { "amount", Convert.ToDouble(arkCoin.Amount.Satoshi) },
            // arkd feeds time.Unix(vtxo.ExpiresAt, 0) — always unix seconds. A block height
            // passed here would read as a timestamp in 1970 and send any `expiry - now()`
            // program down its "expires imminently" branch.
            { "expiry", Convert.ToDouble(arkCoin.ExpiresAt?.ToUnixTimeSeconds() ?? 0) },
            { "birth", Convert.ToDouble(arkCoin.Birth.ToUnixTimeSeconds()) },
            // arkd's variable is `inputType`, and it classifies on Swept alone — an expired
            // but unswept VTXO is still a plain 'vtxo' there.
            { "inputType", arkCoin.Swept ? "recoverable" : arkCoin.Contract.Type == ArkNoteContract.ContractType ? "note" : "vtxo" },
            { "weight", Convert.ToDouble(ArkTxWeightEstimator.GetInputWeightUnits(arkCoin)) }
        };

        return Convert.ToDouble(offchainInputFeeFunc.Invoke(vars)!);
    }
}
