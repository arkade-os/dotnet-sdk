using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Contracts;

public abstract class ArkContract(OutputDescriptor server)
{

    public abstract string Type { get; }

    /// <summary>
    /// The layer this contract type's funds live on by default (on-chain vs off-chain).
    /// Abstract so every contract type makes an explicit, compile-time scope decision.
    /// Used as the fallback when <see cref="ToEntity"/> is called without a scope override.
    /// </summary>
    public abstract ContractScope DefaultScope { get; }

    public OutputDescriptor? Server { get; } = server;

    public virtual ArkAddress GetArkAddress(OutputDescriptor? defaultServerKey = null)
    {
        var spendInfo = GetTaprootSpendInfo();
        return new ArkAddress(
            ECXOnlyPubKey.Create(spendInfo.OutputPubKey.ToBytes()),
            (Server ?? defaultServerKey)?.ToXOnlyPubKey() ?? throw new InvalidOperationException("Server key is required for address generation")
        );
    }

    public virtual TaprootSpendInfo GetTaprootSpendInfo()
    {
        var internalKey = new TaprootInternalPubKey(Constants.UnspendableKey.ToECXOnlyPubKey().ToBytes());
        return TaprootSpendInfo.FromNodeInfo(internalKey, GetTapScriptList().BuildTree());
    }

    public virtual TapScript[] GetTapScriptList()
    {
        var leaves = GetScriptBuilders().ToArray();
        return leaves.Select(x => x.Build()).ToArray();
    }

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

    /// <summary>
    /// Projects this contract into a persistable <see cref="ArkContractEntity"/>.
    /// </summary>
    /// <param name="walletIdentifier">The wallet that owns the contract.</param>
    /// <param name="defaultServerKey">Reserved for address derivation parity; not persisted.</param>
    /// <param name="createdAt">Creation timestamp; defaults to now.</param>
    /// <param name="activityState">Initial activity state; defaults to Active.</param>
    /// <param name="scopeOverride">
    /// Per-instance scope override. When null, the entity's effective scope is
    /// <see cref="DefaultScope"/>. The override is write-time only — the persisted
    /// column always means "this contract's effective scope."
    /// </param>
    public ArkContractEntity ToEntity(
        string walletIdentifier,
        OutputDescriptor? defaultServerKey = null,
        DateTimeOffset? createdAt = null,
        ContractActivityState activityState = ContractActivityState.Active,
        ContractScope? scopeOverride = null)
    {
        return new ArkContractEntity(
            GetScriptPubKey().ToHex(),
            activityState,
            Type,
            GetContractData(),
            walletIdentifier,
            createdAt ?? DateTimeOffset.UtcNow
        )
        {
            Scope = scopeOverride ?? DefaultScope
        };
    }

    protected abstract IEnumerable<ScriptBuilder> GetScriptBuilders();
    protected abstract Dictionary<string, string> GetContractData();
}