using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Batches;

public record CosignerPublicKeyData(byte Index, ECPubKey Key);