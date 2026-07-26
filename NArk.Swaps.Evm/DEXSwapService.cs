namespace NArk.Swaps.Evm;

/// <summary>
/// Service managing swaps in DEXes, which happen on EVM side of things. Uses smart contracts on eth to swap any provided asset
/// to tBTC, which is later consumed by EvmChainSwapProvider.  
/// </summary>
public class DEXSwapService(EvmChainClient evmClient)
{
    
}