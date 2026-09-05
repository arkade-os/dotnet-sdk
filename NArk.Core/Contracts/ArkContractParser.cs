using NArk.Abstractions.Contracts;
using NBitcoin;

namespace NArk.Core.Contracts;

public class ArkContractParser
{
    private static readonly object RegistrationGate = new();

    private static IReadOnlyList<IArkContractParser> _parsers =
    [
        new GenericArkContractParser(ArkPaymentContract.ContractType, ArkPaymentContract.Parse),
        new GenericArkContractParser(ArkBoardingContract.ContractType, ArkBoardingContract.Parse),
        new GenericArkContractParser(HashLockedArkPaymentContract.ContractType, HashLockedArkPaymentContract.Parse),
        new GenericArkContractParser(VHTLCContract.ContractType, VHTLCContract.Parse),
        new GenericArkContractParser(ArkNoteContract.ContractType, ArkNoteContract.Parse),
        new GenericArkContractParser(ArkDelegateContract.ContractType, ArkDelegateContract.Parse),
        new GenericArkContractParser(UnknownArkContract.ContractType, UnknownArkContract.Parse),
    ];

    /// <summary>
    /// Teaches the parser a contract type defined outside this assembly.
    /// </summary>
    /// <param name="parser">Parser for the new type; replaces any parser already claiming it.</param>
    /// <remarks>
    /// <para>
    /// Without this, a package that adds a contract type can persist it and never read it back:
    /// storage records the type name happily, and every later lookup fails to parse. The failure
    /// lands nowhere near the cause — as a sweeper that cannot see its own VTXOs, or a coin that
    /// cannot be signed — so the type is registered where it is defined rather than being invisibly
    /// absent here.
    /// </para>
    /// <para>
    /// Replacing rather than appending on a repeated type keeps registration idempotent, which
    /// matters because DI extension methods are called more than once in tests and in hosts that
    /// build several service providers.
    /// </para>
    /// </remarks>
    public static void Register(IArkContractParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);

        // Copy-on-write, so Parse never enumerates a list being mutated underneath it. Registration
        // happens at startup and parsing happens throughout, and only one of those is rare.
        lock (RegistrationGate)
        {
            _parsers =
            [
                parser,
                .. _parsers.Where(existing => existing.Type != parser.Type),
            ];
        }
    }

    /// <summary>Registers a parser for <paramref name="type"/> from a parse delegate.</summary>
    /// <param name="type">The contract type name as persisted.</param>
    /// <param name="parse">Rebuilds the contract from its stored data.</param>
    public static void Register(string type, Func<Dictionary<string, string>, Network, ArkContract?> parse) =>
        Register(new GenericArkContractParser(type, parse));
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
        return _parsers.FirstOrDefault(parser => parser.Type == type)?
            .Parse(contractData, network);
    }

}