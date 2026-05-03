using NArk.Core.Models;

namespace NArk.Arkade.Introspector;

/// <summary>
/// Client surface for an Arkade introspector co-signing service. Mirrors
/// the ts-sdk's <c>IntrospectorProvider</c> shape so callers picking a
/// .NET vs. TypeScript wallet stack write the same orchestration code.
/// </summary>
/// <remarks>
/// The introspector is a co-signing service that:
/// <list type="number">
///   <item>Owns a private key.</item>
///   <item>Validates the ArkadeScript attached to each transaction input.</item>
///   <item>Computes the per-input signing key as
///         <c>introspector_pubkey + tagged_hash("ArkScriptHash", script) · G</c>.</item>
///   <item>Returns its partial signature, or — when it's the last non-arkd
///         signer — submits the full transaction set to arkd, finalises, and
///         returns the complete PSBTs.</item>
/// </list>
/// </remarks>
public interface IIntrospectorProvider
{
    /// <summary><c>GET /v1/info</c> — version and signing-key fingerprint.</summary>
    Task<IntrospectorInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST /v1/tx</c> — submits a partially-signed Arkade transaction plus
    /// any checkpoint PSBTs and returns the introspector's co-signed PSBTs.
    /// </summary>
    Task<IntrospectorSubmitTxResult> SubmitTxAsync(
        string arkTx,
        IReadOnlyList<string> checkpointTxs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST /v1/intent</c> — co-signs the intent registration proof so the
    /// resulting BIP-322 signature is valid for the introspector-tweaked key.
    /// Returns the co-signed proof base64.
    /// </summary>
    Task<string> SubmitIntentAsync(
        string proof,
        Messages.RegisterIntentMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST /v1/finalization</c> — co-signs forfeit PSBTs and (conditionally)
    /// the commitment tx after the introspector has already co-signed the
    /// matching intent via <see cref="SubmitIntentAsync"/>.
    /// </summary>
    Task<IntrospectorFinalizationResult> SubmitFinalizationAsync(
        string signedProof,
        Messages.RegisterIntentMessage message,
        IReadOnlyList<string> forfeits,
        IReadOnlyList<ConnectorTreeNode>? connectorTree,
        string commitmentTx,
        CancellationToken cancellationToken = default);
}

/// <summary>Server identification returned from <c>GET /v1/info</c>.</summary>
/// <param name="Version">Free-form server version string. Empty if absent.</param>
/// <param name="SignerPubkey">x-only public key (32-byte hex) the introspector co-signs with — pre-tweak.</param>
public sealed record IntrospectorInfo(string Version, string SignerPubkey);

/// <summary>Co-signed PSBTs returned from <c>POST /v1/tx</c>.</summary>
public sealed record IntrospectorSubmitTxResult(
    string SignedArkTx,
    IReadOnlyList<string> SignedCheckpointTxs);

/// <summary>Co-signed forfeits + (conditionally) commitment tx from <c>POST /v1/finalization</c>.</summary>
public sealed record IntrospectorFinalizationResult(
    IReadOnlyList<string> SignedForfeits,
    string? SignedCommitmentTx);

/// <summary>One node in the connector tree carried through the finalization request.</summary>
public sealed record ConnectorTreeNode(
    string Txid,
    string Tx,
    IReadOnlyDictionary<string, string> Children);
