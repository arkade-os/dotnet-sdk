using NArk.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk;

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
    ArkOperatorFeeTerms FeeTerms
);

public record ArkOperatorFeeTerms(
    string TxFeeRate,
    string IntentOffchainOutput,
    string IntentOnchainOutput,
    string IntentOffchainInput,
    string IntentOnchainInput
);