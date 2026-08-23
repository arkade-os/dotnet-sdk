namespace NArk.Abstractions.Batches.ServerEvents;

/// <summary>
/// Server published the unsigned commitment tx and cosigner pubkeys, signalling the start of MuSig2 tree signing.
/// </summary>
/// <param name="UnsignedCommitmentTx">Unsigned commitment transaction (hex).</param>
/// <param name="Id">Batch ID.</param>
/// <param name="CosignersPubkeys">Compressed public keys of all required cosigners (hex).</param>
public record TreeSigningStartedEvent(string UnsignedCommitmentTx, string Id, string[] CosignersPubkeys) : BatchEvent;
