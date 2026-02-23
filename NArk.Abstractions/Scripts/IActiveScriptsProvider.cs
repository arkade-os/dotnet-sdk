namespace NArk.Abstractions.Scripts;

/// <summary>
/// Provides the set of scripts (hex-encoded) that the VTXO synchronization service
/// should actively monitor for incoming VTXOs. Both <c>IVtxoStorage</c> and
/// <c>IContractStorage</c> implement this interface.
/// </summary>
public interface IActiveScriptsProvider
{
    /// <summary>
    /// Raised when the set of active scripts changes (e.g., a new contract is derived).
    /// </summary>
    event EventHandler? ActiveScriptsChanged;

    /// <summary>
    /// Returns the current set of scripts to monitor.
    /// </summary>
    Task<HashSet<string>> GetActiveScripts(CancellationToken cancellationToken = default);
}