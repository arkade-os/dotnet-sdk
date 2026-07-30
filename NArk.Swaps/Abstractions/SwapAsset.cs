namespace NArk.Swaps.Abstractions;

public record SwapAsset(SwapNetwork Network, string AssetId)
{
    public static readonly SwapAsset BtcOnchain = new(SwapNetwork.BitcoinOnchain, "BTC");
    public static readonly SwapAsset BtcLightning = new(SwapNetwork.Lightning, "BTC");
    public static readonly SwapAsset ArkBtc = new(SwapNetwork.Ark, "BTC");

    /// <summary>
    /// tBTC on Arbitrum, identified by Boltz's chain-swap currency symbol
    /// ("TBTC") — the counterpart asset in the live <c>TBTC &lt;-&gt; ARK</c>
    /// Chain Swap pair. The underlying ERC-20 contract address is resolved
    /// at runtime via Boltz's <c>/v2/chain/{currency}/contracts</c> endpoint,
    /// not stored here.
    /// </summary>
    public static readonly SwapAsset ArbitrumTbtc = new(SwapNetwork.EvmArbitrum, "TBTC");

    public static SwapAsset ArkAsset(string assetId)
        => new(SwapNetwork.Ark, assetId);

    /// <summary>
    /// An ERC-20 token asset on an EVM chain, identified by its 0x-prefixed
    /// contract address on the chosen <paramref name="chain"/>.
    /// </summary>
    public static SwapAsset Erc20(SwapNetwork chain, string contractAddress)
        => new(chain, contractAddress);
}
