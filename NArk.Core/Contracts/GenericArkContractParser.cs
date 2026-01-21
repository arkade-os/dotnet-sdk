using NArk.Abstractions.Contracts;
using NBitcoin;

namespace NArk.Core.Contracts;

public class GenericArkContractParser(string type, Func<Dictionary<string, string>, Network, ArkContract?> parse)
    : IArkContractParser
{
    public string Type { get; } = type;

    public ArkContract? Parse(Dictionary<string, string> contractData, Network network)
    {
        return parse(contractData, network);
    }
}