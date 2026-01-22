using System.Net.Http.Json;
using NArk.Swaps.Boltz.Models.Restore;
using NArk.Swaps.Boltz.Models.Swaps.Common;
using NArk.Swaps.Boltz.Models.Swaps.Reverse;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;

namespace NArk.Swaps.Boltz.Client;

public partial class BoltzClient
{

    /// <summary>
    /// Gets the status of a swap.
    /// </summary>
    /// <param name="swapId">The ID of the swap.</param>
    /// <returns>The status response for the swap.</returns>
    public async Task<SwapStatusResponse?> GetSwapStatusAsync(string swapId, CancellationToken cancellation)
    {
        return await _httpClient.GetFromJsonAsync<SwapStatusResponse>($"v2/swap/{swapId}", cancellation);
    }

    // Submarine Swaps

    /// <summary>
    /// Gets the submarine swap pairs information including fees and limits.
    /// </summary>
    /// <param name="cancellation">Cancellation token.</param>
    /// <returns>The submarine pairs response.</returns>
    public async Task<SubmarinePairsResponse?> GetSubmarinePairsAsync(CancellationToken cancellation = default)
    {
        return await _httpClient.GetFromJsonAsync<SubmarinePairsResponse>("v2/swap/submarine", cancellation);
    }

    /// <summary>
    /// Creates a new Submarine Swap.
    /// </summary>
    /// <param name="request">The submarine swap creation request.</param>
    /// <returns>The submarine swap response.</returns>
    public async Task<SubmarineResponse> CreateSubmarineSwapAsync(SubmarineRequest request, CancellationToken cancellation)
    {
        return await PostAsJsonAsync<SubmarineRequest, SubmarineResponse>("v2/swap/submarine", request, cancellation);
    }

    // Reverse Swaps

    /// <summary>
    /// Gets the reverse swap pairs information including fees and limits.
    /// </summary>
    /// <param name="cancellation">Cancellation token.</param>
    /// <returns>The reverse pairs response.</returns>
    public async Task<ReversePairsResponse?> GetReversePairsAsync(CancellationToken cancellation = default)
    {
        return await _httpClient.GetFromJsonAsync<ReversePairsResponse>("v2/swap/reverse", cancellation);
    }

    /// <summary>
    /// Creates a new Reverse Swap.
    /// </summary>
    /// <param name="request">The reverse swap creation request.</param>
    /// <param name="cancellation"></param>
    /// <returns>The reverse swap response.</returns>
    public async Task<ReverseResponse?> CreateReverseSwapAsync(ReverseRequest request, CancellationToken cancellation)
    {
        return await PostAsJsonAsync<ReverseRequest, ReverseResponse>("v2/swap/reverse", request, cancellation);
    }


    /// <summary>
    /// Requests a cooperative refund for a submarine swap.
    /// Boltz will co-sign the refund transaction to return funds to the sender.
    /// </summary>
    /// <param name="swapId">The ID of the submarine swap to refund.</param>
    /// <param name="request">The refund request containing transaction and checkpoint PSBTs.</param>
    /// <param name="cancellation">Cancellation token.</param>
    /// <returns>The refund response with Boltz-signed transactions.</returns>
    public async Task<SubmarineRefundResponse> RefundSubmarineSwapAsync(string swapId, SubmarineRefundRequest request, CancellationToken cancellation)
    {
        return await PostAsJsonAsync<SubmarineRefundRequest, SubmarineRefundResponse>($"v2/swap/submarine/{swapId}/refund/ark", request, cancellation);
    }

    // Swap Restoration

    /// <summary>
    /// Restores swaps associated with a single public key.
    /// </summary>
    /// <param name="publicKey">Hex-encoded public key to search for in swaps.</param>
    /// <param name="cancellation">Cancellation token.</param>
    /// <returns>Array of restorable swaps associated with the public key.</returns>
    public async Task<RestorableSwap[]> RestoreSwapsAsync(string publicKey, CancellationToken cancellation = default)
    {
        var request = new RestoreRequest { PublicKey = publicKey };
        return await PostAsJsonAsync<RestoreRequest, RestorableSwap[]>("v2/swap/restore", request, cancellation);
    }

    /// <summary>
    /// Restores swaps associated with multiple public keys.
    /// </summary>
    /// <param name="publicKeys">Array of hex-encoded public keys to search for in swaps.</param>
    /// <param name="cancellation">Cancellation token.</param>
    /// <returns>Array of restorable swaps associated with any of the public keys.</returns>
    public async Task<RestorableSwap[]> RestoreSwapsAsync(string[] publicKeys, CancellationToken cancellation = default)
    {
        var request = new RestoreRequest { PublicKeys = publicKeys };
        return await PostAsJsonAsync<RestoreRequest, RestorableSwap[]>("v2/swap/restore", request, cancellation);
    }
}
