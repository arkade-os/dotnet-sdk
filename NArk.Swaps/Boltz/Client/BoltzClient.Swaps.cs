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
    /// <exception cref="BoltzSwapNotFoundException">
    /// The configured Boltz instance has no record of this swap (HTTP 404 with
    /// a "could not find swap with id" body). Distinct from generic HTTP errors
    /// so callers can treat it as "swap unknown to this provider" rather than
    /// a transient failure.
    /// </exception>
    public virtual async Task<SwapStatusResponse?> GetSwapStatusAsync(string swapId, CancellationToken cancellation)
    {
        using var resp = await _httpClient.GetAsync($"v2/swap/{swapId}", cancellation);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Defensive: only treat as "swap unknown" if the body matches the
            // shape Boltz returns for missing swaps. A 404 from a renamed
            // route or proxy misconfiguration shouldn't trip the safety net.
            var body = await resp.Content.ReadAsStringAsync(cancellation);
            if (body.Contains("could not find swap", StringComparison.OrdinalIgnoreCase))
            {
                throw new BoltzSwapNotFoundException(swapId, body);
            }
            throw new HttpRequestException(body, null, resp.StatusCode);
        }

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SwapStatusResponse>(cancellation);
    }

    // Submarine Swaps

    /// <summary>
    /// Gets the submarine swap pairs information including fees and limits.
    /// </summary>
    /// <param name="cancellation">Cancellation token.</param>
    /// <returns>The submarine pairs response.</returns>
    public virtual async Task<SubmarinePairsResponse?> GetSubmarinePairsAsync(CancellationToken cancellation = default)
    {
        return await _httpClient.GetFromJsonAsync<SubmarinePairsResponse>("v2/swap/submarine", cancellation);
    }

    /// <summary>
    /// Creates a new Submarine Swap.
    /// </summary>
    /// <param name="request">The submarine swap creation request.</param>
    /// <returns>The submarine swap response.</returns>
    public virtual async Task<SubmarineResponse> CreateSubmarineSwapAsync(SubmarineRequest request, CancellationToken cancellation)
    {
        return await PostAsJsonAsync<SubmarineRequest, SubmarineResponse>("v2/swap/submarine", request, cancellation);
    }

    // Reverse Swaps

    /// <summary>
    /// Gets the reverse swap pairs information including fees and limits.
    /// </summary>
    /// <param name="cancellation">Cancellation token.</param>
    /// <returns>The reverse pairs response.</returns>
    public virtual async Task<ReversePairsResponse?> GetReversePairsAsync(CancellationToken cancellation = default)
    {
        return await _httpClient.GetFromJsonAsync<ReversePairsResponse>("v2/swap/reverse", cancellation);
    }

    /// <summary>
    /// Creates a new Reverse Swap.
    /// </summary>
    /// <param name="request">The reverse swap creation request.</param>
    /// <param name="cancellation"></param>
    /// <returns>The reverse swap response.</returns>
    public virtual async Task<ReverseResponse?> CreateReverseSwapAsync(ReverseRequest request, CancellationToken cancellation)
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
    public virtual async Task<SubmarineRefundResponse> RefundSubmarineSwapAsync(string swapId, SubmarineRefundRequest request, CancellationToken cancellation)
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
    public virtual async Task<RestorableSwap[]> RestoreSwapsAsync(string publicKey, CancellationToken cancellation = default)
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
    public virtual async Task<RestorableSwap[]> RestoreSwapsAsync(string[] publicKeys, CancellationToken cancellation = default)
    {
        var request = new RestoreRequest { PublicKeys = publicKeys };
        return await PostAsJsonAsync<RestoreRequest, RestorableSwap[]>("v2/swap/restore", request, cancellation);
    }
}
