using NBitcoin;

namespace NArk.Abstractions.Contracts;

/// <summary>Parses a typed Arkade contract from its serialized key-value data.</summary>
public interface IArkContractParser
{
    /// <summary>Contract type discriminator this parser handles (e.g. <c>"vtxo"</c>, <c>"boarding"</c>).</summary>
    string Type { get; }
    /// <summary>Parses a contract from serialized data. Returns null when the data is not valid for this type.</summary>
    ArkContract? Parse(Dictionary<string, string> contractData, Network network);

    /// <summary>Splits an <c>arkcontract=…</c> string into its key-value parameters.</summary>
    public static Dictionary<string, string> GetContractData(string contract)
    {
        var parts = contract.Split('&');
        var data = new Dictionary<string, string>();
        foreach (var part in parts)
        {
            var kvp = part.Split('=');
            if (kvp.Length == 2)
            {
                data[kvp[0]] = kvp[1];
            }
        }

        return data;
    }
}
