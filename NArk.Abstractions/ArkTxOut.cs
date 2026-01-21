using NBitcoin;

namespace NArk.Abstractions;

public class ArkTxOut(ArkTxOutType type, Money amount, IDestination dest) : TxOut(amount, dest)
{
    public ArkTxOutType Type { get; } = type;
}

public enum ArkTxOutType
{
    Vtxo,
    Onchain
}