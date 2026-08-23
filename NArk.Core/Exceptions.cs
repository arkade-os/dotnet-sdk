namespace NArk.Core;

/// <summary>Thrown when attempting to spend a VTXO that is currently locked by an in-progress operation.</summary>
public class AlreadyLockedVtxoException(string msg) : Exception(msg);

/// <summary>Thrown when attempting to spend a VTXO that has already been spent.</summary>
public class VtxoAlreadySpentException(string msg) : Exception(msg);

/// <summary>Thrown when the wallet is asked to sign contracts for VTXOs it does not recognise.</summary>
public class UnableToSignUnknownContracts(string msg) : Exception(msg);

/// <summary>Thrown when an operation cannot proceed because the caller must supply additional information first.</summary>
public class AdditionalInformationRequiredException(string msg) : Exception(msg);

/// <summary>
/// Thrown when the Arkade server rejects this SDK because its build version is too old.
/// The SDK does not catch this exception — it propagates to the caller.
/// </summary>
public class IncompatibleSdkVersionException(string msg) : Exception(msg);

/// <summary>
/// Thrown when the Arkade server rejects a request because the cached server-info digest no longer matches.
/// The SDK clears the cached digest and server info before throwing; the caller should retry after
/// calling <c>GetServerInfoAsync</c> to refresh the configuration.
/// </summary>
public class DigestMismatchException(string msg) : Exception(msg);
