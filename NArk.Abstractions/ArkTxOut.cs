using NBitcoin;

namespace NArk.Abstractions;

public class ArkTxOut(ArkTxOutType type, Money amount, IDestination dest) : TxOut(amount, dest)
{
    public ArkTxOutType Type { get; } = type;
    public IReadOnlyList<ArkTxOutAsset>? Assets { get; init; }
}

public enum ArkTxOutType
{
    Vtxo,
    Onchain
}