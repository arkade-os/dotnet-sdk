using NBitcoin;

namespace NArk.Abstractions.Scripts;

/// <summary>Marks a spend whose input index binds its payout output index; builders must preserve order and separate outputs.</summary>
public interface IIndexedOutputScriptBuilder
{
    /// <summary>Rejects an invalid aligned payout before any condition witness can be submitted.</summary>
    void ValidateIndexedOutput(TxOut input, TxOut output);
}
