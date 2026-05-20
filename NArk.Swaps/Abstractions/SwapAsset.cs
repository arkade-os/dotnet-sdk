namespace NArk.Swaps.Abstractions;

public record SwapAsset(SwapNetwork Network, string AssetId)
{
    public static readonly SwapAsset BtcOnchain = new(SwapNetwork.BitcoinOnchain, "BTC");
    public static readonly SwapAsset BtcLightning = new(SwapNetwork.Lightning, "BTC");
    public static readonly SwapAsset ArkBtc = new(SwapNetwork.Ark, "BTC");

    public static SwapAsset ArkAsset(string assetId)
        => new(SwapNetwork.Ark, assetId);

    /// <summary>
    /// ERC-20 token asset on an EVM chain — used by cross-chain providers
    /// (LendaSwap) where <paramref name="contractAddress"/> is the 0x-prefixed
    /// token contract address on the chosen <paramref name="chain"/>.
    /// </summary>
    public static SwapAsset Erc20(SwapNetwork chain, string contractAddress)
        => new(chain, contractAddress);
}
