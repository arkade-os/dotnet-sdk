using System.Text.RegularExpressions;

namespace NArk.ArkadeIntents.SolverRegistry;

/// <summary>A canonical CAIP-19 asset identity, including its settlement chain.</summary>
public sealed record AssetIdentifier
{
    private static readonly Regex Form = new(
        @"\A(?:(?:arkade|bolt11|bitcoin):(?:bitcoin/slip44:0|(?:signet|mutinynet|regtest)/slip44:1|(?:bitcoin|signet|mutinynet|regtest)/asset:[0-9a-f]{68})|eip155:[1-9][0-9]{0,31}/(?:slip44:(?:0|[1-9][0-9]{0,9})|erc20:0x[0-9a-f]{40}))\z",
        RegexOptions.CultureInvariant);

    private AssetIdentifier(string value) => Value = value;

    /// <summary>The canonical identity, preserved byte for byte.</summary>
    public string Value { get; }
    /// <summary>The chain namespace: arkade, bolt11, bitcoin, or eip155.</summary>
    public string Namespace => Value[..Value.IndexOf(':')];
    /// <summary>The network name or EIP-155 chain id.</summary>
    public string ChainReference => Value[(Value.IndexOf(':') + 1)..Value.IndexOf('/')];
    /// <summary>The asset namespace and reference after the chain.</summary>
    public string Asset => Value[(Value.IndexOf('/') + 1)..];

    /// <summary>Parses a supported canonical identifier without case folding or inferred networks.</summary>
    public static AssetIdentifier Parse(string value) => Form.IsMatch(value)
        ? new(value) : throw new FormatException($"Invalid canonical asset identifier '{value}'.");

    /// <summary>Converts an old registry identifier using an explicitly supplied network and rail.</summary>
    public static AssetIdentifier FromLegacy(string id, string network, string corridor = "arkade")
    {
        var rail = corridor switch { "lightning" => "bolt11", "onchain" => "bitcoin", _ => corridor };
        var asset = id == "btc" ? $"slip44:{(network == "bitcoin" ? 0 : 1)}" : $"asset:{id}";
        return Parse($"{rail}:{network}/{asset}");
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
