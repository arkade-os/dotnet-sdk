using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Batches;

/// <summary>A cosigner's position index and compressed public key in a MuSig2 tree-signing session.</summary>
public record CosignerPublicKeyData(byte Index, ECPubKey Key);
