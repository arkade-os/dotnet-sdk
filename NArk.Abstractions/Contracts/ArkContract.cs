using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Contracts;

/// <summary>
/// Base class for all Arkade taproot contracts (VTXO, boarding, delegate, etc.).
/// Subclasses define the script structure; this base handles taproot spend-info derivation
/// and serialization shared across all types.
/// </summary>
public abstract class ArkContract(OutputDescriptor server)
{
    /// <summary>Contract type discriminator string (e.g. <c>"vtxo"</c>, <c>"boarding"</c>).</summary>
    public abstract string Type { get; }

    /// <summary>The Arkade server key descriptor. Null for contracts that predate multi-server support.</summary>
    public OutputDescriptor? Server { get; } = server;

    /// <summary>Derives the bech32m Arkade address for this contract's output pubkey.</summary>
    public virtual ArkAddress GetArkAddress(OutputDescriptor? defaultServerKey = null)
    {
        var spendInfo = GetTaprootSpendInfo();
        return new ArkAddress(
            ECXOnlyPubKey.Create(spendInfo.OutputPubKey.ToBytes()),
            (Server ?? defaultServerKey)?.ToXOnlyPubKey() ?? throw new InvalidOperationException("Server key is required for address generation")
        );
    }

    /// <summary>Builds the taproot spend info (NUMS internal key + script tree) for this contract.</summary>
    public virtual TaprootSpendInfo GetTaprootSpendInfo()
    {
        var internalKey = new TaprootInternalPubKey(Constants.UnspendableKey.ToECXOnlyPubKey().ToBytes());
        return TaprootSpendInfo.FromNodeInfo(internalKey, GetTapScriptList().BuildTree());
    }

    /// <summary>Returns the compiled tapscript leaves that form this contract's script tree.</summary>
    public virtual TapScript[] GetTapScriptList()
    {
        var leaves = GetScriptBuilders().ToArray();
        return leaves.Select(x => x.Build()).ToArray();
    }

    /// <summary>Serializes the contract as an <c>arkcontract=…</c> query string.</summary>
    public override string ToString()
    {
        var contractData = GetContractData();
        contractData.Remove("arkcontract");
        var dataString = string.Join("&", contractData.Select(kvp => $"{kvp.Key}={kvp.Value}"));

        return $"arkcontract={Type}&{dataString}";
    }

    /// <summary>
    /// Returns the taproot scriptPubKey for this contract.
    /// Unlike <see cref="GetArkAddress"/>, this works for all contract types including boarding.
    /// </summary>
    public virtual Script GetScriptPubKey()
    {
        var spendInfo = GetTaprootSpendInfo();
        return spendInfo.OutputPubKey.ScriptPubKey;
    }

    /// <summary>Creates a storage entity for this contract, ready to persist.</summary>
    public ArkContractEntity ToEntity(
        string walletIdentifier,
        OutputDescriptor? defaultServerKey = null,
        DateTimeOffset? createdAt = null,
        ContractActivityState activityState = ContractActivityState.Active)
    {
        return new ArkContractEntity(
            GetScriptPubKey().ToHex(),
            activityState,
            Type,
            GetContractData(),
            walletIdentifier,
            createdAt ?? DateTimeOffset.UtcNow
        );
    }

    /// <summary>Returns the script builders for each tapscript leaf in the tree.</summary>
    protected abstract IEnumerable<ScriptBuilder> GetScriptBuilders();
    /// <summary>Returns the key-value map of contract parameters used for serialization.</summary>
    protected abstract Dictionary<string, string> GetContractData();
}
