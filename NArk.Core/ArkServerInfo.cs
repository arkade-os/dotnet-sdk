using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Core;

public record ArkServerInfo(
    Money Dust,
    OutputDescriptor SignerKey,
    Dictionary<ECXOnlyPubKey, long> DeprecatedSigners,
    Network Network,
    Sequence UnilateralExit,
    Sequence BoardingExit,
    BitcoinAddress ForfeitAddress,
    ECXOnlyPubKey ForfeitPubKey,
    UnilateralPathArkTapScript CheckpointTapScript,
    ArkOperatorFeeTerms FeeTerms,
    long MaxTxWeight = 0,
    int MaxOpReturnOutputs = 0,
    Money VtxoMinAmount = default!,
    Money VtxoMaxAmount = default!,
    Money UtxoMinAmount = default!,
    Money UtxoMaxAmount = default!
)
{
    /// <summary>
    /// Whether boarding (onchain UTXOs) is allowed by the server.
    /// UtxoMaxAmount == 0 means boarding is not allowed.
    /// </summary>
    public bool BoardingAllowed => UtxoMaxAmount is null || UtxoMaxAmount != Money.Zero;
};

public record ArkOperatorFeeTerms(
    string TxFeeRate,
    string IntentOffchainOutput,
    string IntentOnchainOutput,
    string IntentOffchainInput,
    string IntentOnchainInput
);