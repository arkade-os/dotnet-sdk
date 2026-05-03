using NArk.Abstractions.Scripts;
using NBitcoin;

namespace NArk.Arkade.Scripts;

/// <summary>
/// Marker interface for <see cref="ScriptBuilder"/>s that produce a tapscript
/// leaf bound to an <see cref="ArkadeScript"/> body — i.e. leaves the
/// introspector co-signs only after executing the attached script.
/// </summary>
/// <remarks>
/// <para>
/// Both layers of the introspector integration use this interface:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     The PSBT co-signing helper (slice 2) walks an
///     <see cref="NArk.Abstractions.Contracts.ArkContract"/>'s
///     <c>GetScriptBuilders()</c>, type-checks against this interface, and
///     builds an <see cref="Introspector.IntrospectorEntry"/> per match —
///     so the OP_RETURN packet that travels with the transaction tells the
///     introspector exactly which script to execute for each input.
///     </description>
///   </item>
///   <item>
///     <description>
///     The signer dispatch (slice 2) checks "does this contract have any
///     arkade-bound leaves at all?" via the same interface — if no, the
///     PSBT skips the introspector REST round-trip entirely.
///     </description>
///   </item>
/// </list>
/// <para>
/// New arkade-bound script flavours (CSV multisig, condition multisig, ...)
/// just implement this interface — the dispatch and packet-assembly code
/// doesn't need to know the concrete type.
/// </para>
/// </remarks>
public interface IArkadeBoundScriptBuilder
{
    /// <summary>The ArkadeScript bytecode the introspector executes for this leaf.</summary>
    byte[] ArkadeScript { get; }

    /// <summary>
    /// Pre-tweak introspector pubkeys (one per introspector). The tapscript
    /// leaf's signing set carries the post-tweak versions of these — the
    /// pre-tweak forms are kept so the dispatch layer can decide whether
    /// to call out to a specific introspector instance.
    /// </summary>
    IReadOnlyList<TaprootPubKey> IntrospectorKeys { get; }
}
