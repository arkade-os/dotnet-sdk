namespace NArk.Core.Assets;

public static class AssetConstants
{
    public const int TxHashSize = 32;
    public const int AssetIdSize = 34; // 32 + 2
    public const byte AssetVersion = 0x01;
    public const byte MaskAssetId = 0x01;
    public const byte MaskControlAsset = 0x02;
    public const byte MaskMetadata = 0x04;
    public static readonly byte[] ArkadeMagic = [0x41, 0x52, 0x4B]; // "ARK"
    public const byte MarkerAssetPayload = 0x00;
}

public enum AssetInputType : byte { Unspecified = 0, Local = 1, Intent = 2 }
public enum AssetRefType : byte { Unspecified = 0, ByID = 1, ByGroup = 2 }
