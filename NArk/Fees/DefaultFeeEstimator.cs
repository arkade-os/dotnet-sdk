using Cel;
using NArk.Abstractions;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Transport;

namespace NArk.Fees;

public class DefaultFeeEstimator(IClientTransport clientTransport) : IFeeEstimator
{
    private readonly ICelEnvironment _celEnvironment = new CelEnvironment(null, null);

    public Task<long> EstimateFeeAsync(ArkCoin[] coins, ArkTxOut[] outputs,
        CancellationToken cancellationToken = default) =>
        EstimateFeeAsync(new ArkIntentSpec(coins, outputs, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)),
            cancellationToken);

    public async Task<long> EstimateFeeAsync(ArkIntentSpec spec, CancellationToken cancellationToken = default)
    {
        var info = await clientTransport.GetServerInfoAsync(cancellationToken);
        var offchainInputFeeFunc = _celEnvironment.Compile(info.FeeTerms.IntentOffchainInput);
        var inputFees =
            spec
                .Coins
                .Select(lite => (lite, GetInputFee(offchainInputFeeFunc, lite)))
                //.Where(tuple => tuple.lite.Amount.Satoshi > tuple.Item2)
                .Sum(tuple => tuple.Item2);

        var offchainOutputFeeFunc = _celEnvironment.Compile(info.FeeTerms.IntentOffchainOutput);
        var onchainOutputFeeFunc = _celEnvironment.Compile(info.FeeTerms.IntentOnchainOutput);
        var outputFees =
            spec
                .Outputs
                .Sum(o =>
                    GetOutputFee(
                        o.Type == ArkTxOutType.Vtxo ? offchainOutputFeeFunc : onchainOutputFeeFunc,
                        o
                    )
                );
        var totalFee = inputFees + outputFees;
        return Convert.ToInt64(Math.Ceiling(totalFee));
    }

    private double GetOutputFee(CelProgramDelegate feeFunc, ArkTxOut txOut)
    {
        var vars = new Dictionary<string, object?>
        {
            { "amount", Convert.ToDouble(txOut.Value.Satoshi) },
            { "script", txOut.ScriptPubKey.ToHex() }
        };

        return Convert.ToDouble(feeFunc.Invoke(vars)!);
    }

    private double GetInputFee(CelProgramDelegate offchainInputFeeFunc, ArkCoin arkCoin)
    {
        var vars = new Dictionary<string, object?>
        {
            { "amount", Convert.ToDouble(arkCoin.Amount.Satoshi) },
            { "expiry", arkCoin.GetRawExpiry() },
            { "birth", arkCoin.Birth.ToUnixTimeSeconds() },
            { "type", arkCoin.IsRecoverable() ? "recoverable" : arkCoin.Contract.Type == "arknote" ? "note" : "vtxo" },
            { "weight", 0 }
        };

        return Convert.ToDouble(offchainInputFeeFunc.Invoke(vars)!);
    }
}