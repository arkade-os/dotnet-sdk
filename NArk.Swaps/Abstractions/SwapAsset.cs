namespace NArk.Swaps.Abstractions;

public record SwapAsset(SwapNetwork Network, string AssetId)
{
    public static readonly SwapAsset BtcOnchain = new(SwapNetwork.BitcoinOnchain, "BTC");
    public static readonly SwapAsset BtcLightning = new(SwapNetwork.Lightning, "BTC");
    public static readonly SwapAsset ArkBtc = new(SwapNetwork.Ark, "BTC");

    public static SwapAsset ArkAsset(string assetId)
        => new(SwapNetwork.Ark, assetId);
}
