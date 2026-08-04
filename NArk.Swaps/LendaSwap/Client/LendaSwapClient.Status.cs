using System.Net.Http.Json;
using NArk.Swaps.LendaSwap.Models;

namespace NArk.Swaps.LendaSwap.Client;

public partial class LendaSwapClient
{
    /// <summary>
    /// Gets the current status and details of a swap.
    /// </summary>
    /// <param name="swapId">The swap identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<LendaSwapResponse?> GetSwapStatusAsync(string swapId, CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<LendaSwapResponse>($"swap/{swapId}", JsonOptions, ct);
    }
}
