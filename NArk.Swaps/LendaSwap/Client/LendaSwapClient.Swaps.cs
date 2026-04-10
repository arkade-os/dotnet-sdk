using NArk.Swaps.LendaSwap.Models;

namespace NArk.Swaps.LendaSwap.Client;

public partial class LendaSwapClient
{
    /// <summary>
    /// Creates a BTC on-chain to Arkade swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateBtcToArkadeSwapAsync(
        CreateBtcToArkadeRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateBtcToArkadeRequest, LendaSwapResponse>(
            "swap/bitcoin/arkade", request, ct);
    }

    /// <summary>
    /// Creates an Arkade to BTC on-chain swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateArkadeToBtcSwapAsync(
        CreateArkadeToBtcRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateArkadeToBtcRequest, LendaSwapResponse>(
            "swap/arkade/bitcoin", request, ct);
    }

    /// <summary>
    /// Creates a Lightning to Arkade swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateLightningToArkadeSwapAsync(
        CreateLightningToArkadeRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateLightningToArkadeRequest, LendaSwapResponse>(
            "swap/lightning/arkade", request, ct);
    }

    /// <summary>
    /// Creates an Arkade to Lightning swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateArkadeToLightningSwapAsync(
        CreateArkadeToLightningRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateArkadeToLightningRequest, LendaSwapResponse>(
            "swap/arkade/lightning", request, ct);
    }

    /// <summary>
    /// Creates an Arkade to EVM token swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateArkadeToEvmSwapAsync(
        CreateArkadeToEvmRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateArkadeToEvmRequest, LendaSwapResponse>(
            "swap/arkade/evm", request, ct);
    }

    /// <summary>
    /// Creates an EVM token to Arkade swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateEvmToArkadeSwapAsync(
        CreateEvmToArkadeRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateEvmToArkadeRequest, LendaSwapResponse>(
            "swap/evm/arkade", request, ct);
    }

    /// <summary>
    /// Creates a Lightning to EVM token swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateLightningToEvmSwapAsync(
        CreateLightningToEvmRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateLightningToEvmRequest, LendaSwapResponse>(
            "swap/lightning/evm", request, ct);
    }

    /// <summary>
    /// Creates an EVM token to Lightning swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateEvmToLightningSwapAsync(
        CreateEvmToLightningRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateEvmToLightningRequest, LendaSwapResponse>(
            "swap/evm/lightning", request, ct);
    }

    /// <summary>
    /// Creates a BTC on-chain to EVM token swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateBtcToEvmSwapAsync(
        CreateBtcToEvmRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateBtcToEvmRequest, LendaSwapResponse>(
            "swap/bitcoin/evm", request, ct);
    }

    /// <summary>
    /// Creates an EVM token to BTC on-chain swap.
    /// </summary>
    public virtual async Task<LendaSwapResponse> CreateEvmToBtcSwapAsync(
        CreateEvmToBtcRequest request, CancellationToken ct = default)
    {
        return await PostAsJsonAsync<CreateEvmToBtcRequest, LendaSwapResponse>(
            "swap/evm/bitcoin", request, ct);
    }
}
