namespace NArk.Core.Exit;

/// <summary>
/// Thrown while parsing a tree virtual tx for broadcast when its input is missing the
/// MuSig2 taproot key-path signature (<c>PSBT_IN_TAP_KEY_SIG</c>).
/// </summary>
internal sealed class TreeTxNotSignedException(string message) : Exception(message);
