using NArk.Abstractions.Contracts;
using NBitcoin;

namespace NArk.Contracts;

public class ArkContractParser
{
    private static readonly List<IArkContractParser> Parsers = [];

    static ArkContractParser()
    {
        Parsers.Add(new GenericArkContractParser(ArkPaymentContract.ContractType, ArkPaymentContract.Parse));
        Parsers.Add(new GenericArkContractParser(HashLockedArkPaymentContract.ContractType, HashLockedArkPaymentContract.Parse));
        Parsers.Add(new GenericArkContractParser(VHTLCContract.ContractType, VHTLCContract.Parse));
        Parsers.Add(new GenericArkContractParser(ArkNoteContract.ContractType, ArkNoteContract.Parse));
        Parsers.Add(new GenericArkContractParser(UnknownArkContract.ContractType, UnknownArkContract.Parse));
    }
    public static ArkContract? Parse(string contract, Network network)
    {
        if (!contract.StartsWith("arkcontract"))
        {
            throw new ArgumentException("Invalid contract format. Must start with 'arkcontract'");
        }

        var contractData = IArkContractParser.GetContractData(contract);
        contractData.TryGetValue("arkcontract", out var contractType);

        return
            !string.IsNullOrEmpty(contractType) ?
                Parse(contractType, contractData, network) :
                throw new ArgumentException("Contract type is missing in the contract data");
    }

    public static ArkContract? Parse(string type, Dictionary<string, string> contractData, Network network)
    {
        return Parsers.FirstOrDefault(parser => parser.Type == type)?
            .Parse(contractData, network); // Ensure the Payment parser is registered
    }

}