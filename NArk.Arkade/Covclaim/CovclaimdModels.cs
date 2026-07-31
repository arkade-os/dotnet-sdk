using System.Text.Json.Serialization;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Covclaim;

/// <summary>
/// The keys a claim daemon publishes so makers can target it — the response of
/// <c>GET /v1/preimage/covclaimd-pubkey</c>.
/// </summary>
/// <param name="CovclaimdPubKey">
/// The key to ECIES-encrypt the preimage to (see <see cref="CovclaimEcies.Encrypt"/>).
/// </param>
/// <param name="EmulatorPubKey">
/// The daemon's emulator signer key. It MUST be the emulator key baked into the
/// covenant-claim leaf, otherwise the leaf commits to a signer that will never
/// co-sign and the claim silently never happens.
/// </param>
public sealed record CovclaimdKeys(ECPubKey CovclaimdPubKey, ECPubKey EmulatorPubKey)
{
    /// <summary>The emulator key in the x-only form the tapscript leaf needs.</summary>
    public TaprootPubKey EmulatorTaprootPubKey => new(EmulatorPubKey.ToXOnlyPubKey().ToBytes());
}

/// <summary>Raw JSON shape of the covclaimd-pubkey response; hex-encoded compressed keys.</summary>
internal sealed class CovclaimdKeysResponse
{
    [JsonPropertyName("covclaimd_pub_key")]
    public string? CovclaimdPubKey { get; set; }

    [JsonPropertyName("emulator_pub_key")]
    public string? EmulatorPubKey { get; set; }
}

/// <summary>Request body of <c>POST /v1/reveal</c>.</summary>
internal sealed class RevealRequestBody
{
    [JsonPropertyName("swap_address")]
    public string SwapAddress { get; set; } = "";

    [JsonPropertyName("packet")]
    public RevealPacketBody Packet { get; set; } = new();

    /// <summary>Hex-encoded BIP-371 taptree of the funding output.</summary>
    [JsonPropertyName("taptree")]
    public string Taptree { get; set; } = "";
}

/// <summary>The claim packet carried by a reveal request; both fields are standard base64.</summary>
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
