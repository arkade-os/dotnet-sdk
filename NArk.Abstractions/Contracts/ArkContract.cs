using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Contracts;

public abstract class ArkContract(OutputDescriptor server)
{

    public abstract string Type { get; }

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
        var builder = GetTapScriptList().WithTree();
        return builder.Finalize(new TaprootInternalPubKey(Constants.UnspendableKey.ToECXOnlyPubKey().ToBytes()));
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

    public ArkContractEntity ToEntity(
        string walletIdentifier,
        OutputDescriptor? defaultServerKey = null,
        DateTimeOffset? createdAt = null,
        ContractActivityState activityState = ContractActivityState.Active)
    {
        return new ArkContractEntity(
            GetArkAddress(defaultServerKey: defaultServerKey).ScriptPubKey.ToHex(),
            activityState,
            Type,
            GetContractData(),
            walletIdentifier,
            createdAt ?? DateTimeOffset.UtcNow
        );
    }

    protected abstract IEnumerable<ScriptBuilder> GetScriptBuilders();
    protected abstract Dictionary<string, string> GetContractData();
}