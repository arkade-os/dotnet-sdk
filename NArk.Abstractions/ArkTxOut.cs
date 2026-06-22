using NBitcoin;

namespace NArk.Abstractions;

/// <summary>A VTXO tree or on-chain output produced by an Arkade intent.</summary>
public class ArkTxOut(ArkTxOutType type, Money amount, IDestination dest) : TxOut(amount, dest)
{
    /// <summary>Whether this output lands in the batch VTXO tree or on-chain.</summary>
    public ArkTxOutType Type { get; } = type;
    /// <summary>Ark-issued assets attached to this output; null for BTC-only outputs.</summary>
    public IReadOnlyList<ArkTxOutAsset>? Assets { get; init; }
}

/// <summary>Determines where an Arkade intent output is settled.</summary>
public enum ArkTxOutType
{
    /// <summary>Output is a VTXO inside the batch tree.</summary>
    Vtxo,
    /// <summary>Output is an on-chain Bitcoin UTXO (collaborative exit).</summary>
    Onchain
}