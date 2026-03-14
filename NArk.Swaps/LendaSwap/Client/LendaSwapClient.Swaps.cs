using NArk.Swaps.LendaSwap.Models;

namespace NArk.Swaps.LendaSwap.Client;

public partial class LendaSwapClient
{
    /// <summary>
    /// Creates a BTC to Arkade swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateBtcToArkadeSwapAsync(
        CreateBtcToArkadeRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateBtcToArkadeRequest, LendaSwapResponse>(
            "swap/bitcoin/arkade", request, ct);
    }

    /// <summary>
    /// Creates an Arkade to EVM swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateArkadeToEvmSwapAsync(
        CreateArkadeToEvmRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateArkadeToEvmRequest, LendaSwapResponse>(
            "swap/arkade/evm", request, ct);
    }

    /// <summary>
    /// Creates an EVM to Arkade swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateEvmToArkadeSwapAsync(
        CreateEvmToArkadeRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateEvmToArkadeRequest, LendaSwapResponse>(
            "swap/evm/arkade", request, ct);
    }
}
