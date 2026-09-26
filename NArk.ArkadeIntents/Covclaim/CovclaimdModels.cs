using System.Text.Json.Serialization;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Covclaim;

/// <summary>The two keys a covclaimd instance publishes.</summary>
/// <param name="CovclaimdPubKey">
/// What a preimage is sealed to. Read live: the daemon generates it at startup.
/// </param>
/// <param name="EmulatorPubKey">
/// The covenant co-signer the daemon will claim through. Read even though this SDK pins its own per
/// network: a daemon on a different emulator cannot claim anything we build, and comparing the two
/// turns that into an error at registration rather than a lockup nobody touches.
/// </param>
public sealed record CovclaimdKeys(ECPubKey CovclaimdPubKey, ECPubKey EmulatorPubKey)
{
    /// <summary>The emulator key in the x-only form the covenant commits to.</summary>
    public TaprootPubKey EmulatorTaprootPubKey => new(EmulatorPubKey.ToXOnlyPubKey().ToBytes());
}

/// <summary>Wire shape of <c>GET /v1/preimage/covclaimd-pubkey</c>.</summary>
internal sealed class CovclaimdKeysResponse
{
    [JsonPropertyName("covclaimd_pub_key")]
    public string? CovclaimdPubKey { get; set; }

    [JsonPropertyName("emulator_pub_key")]
    public string? EmulatorPubKey { get; set; }
}

/// <summary>Wire shape of <c>POST /v1/reveal</c>, mirroring <c>covclaimd.v1.RevealRequest</c>.</summary>
internal sealed class RevealRequestBody
{
    [JsonPropertyName("swap_address")]
    public string SwapAddress { get; set; } = "";

    [JsonPropertyName("packet")]
    public RevealPacketBody Packet { get; set; } = new();

    [JsonPropertyName("taptree")]
    public string Taptree { get; set; } = "";
}

/// <summary>The claim packet a reveal carries; both fields are standard base64.</summary>
/// <remarks>
/// Two fields and no third: the extension path's packet also names which covclaimd may open it,
/// because there the packet is found on a public stream. Here there is one named daemon.
/// </remarks>
internal sealed class RevealPacketBody
{
    [JsonPropertyName("ciphertext")]
    public string Ciphertext { get; set; } = "";

    [JsonPropertyName("arkade_script")]
    public string ArkadeScript { get; set; } = "";
}

/// <summary>Error body returned by the REST gateway on a non-2xx response.</summary>
internal sealed class CovclaimdErrorBody
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
