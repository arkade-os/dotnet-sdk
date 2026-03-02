namespace NArk.Core.Assets;

public static class AssetConstants
{
    public const int TxHashSize = 32;
    public const int AssetIdSize = 34; // 32 + 2
    public const byte AssetVersion = 0x01;
    public const byte MaskAssetId = 0x01;
    public const byte MaskControlAsset = 0x02;
    public const byte MaskMetadata = 0x04;
    /// <summary>ArkadeMagic moved to <see cref="Extension.ArkadeMagic"/>.</summary>
    [Obsolete("Use Extension.ArkadeMagic instead")]
    public static readonly byte[] ArkadeMagic = Extension.ArkadeMagic;
}

public enum AssetInputType : byte { Unspecified = 0, Local = 1, Intent = 2 }
public enum AssetRefType : byte { Unspecified = 0, ByID = 1, ByGroup = 2 }
