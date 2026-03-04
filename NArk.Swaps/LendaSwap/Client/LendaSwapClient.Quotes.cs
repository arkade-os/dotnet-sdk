using System.Net.Http.Json;
using NArk.Swaps.LendaSwap.Models;

namespace NArk.Swaps.LendaSwap.Client;

public partial class LendaSwapClient
{
    /// <summary>
    /// Lists available trading pairs and tokens.
    /// </summary>
    public virtual async Task<TokenListResponse?> GetTokensAsync(CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<TokenListResponse>("tokens", JsonOptions, ct);
    }

    /// <summary>
    /// Gets a swap quote for the specified route and amount.
    /// </summary>
    /// <param name="sourceChain">Source chain identifier (e.g. "Arkade", "Lightning", "Bitcoin", "137").</param>
    /// <param name="sourceToken">Source token identifier.</param>
    /// <param name="targetChain">Target chain identifier.</param>
    /// <param name="targetToken">Target token identifier.</param>
    /// <param name="sourceAmount">Amount in source token (specify either source or target, not both).</param>
    /// <param name="targetAmount">Amount in target token (specify either source or target, not both).</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<QuoteResponse?> GetQuoteAsync(
        string sourceChain,
        string sourceToken,
        string targetChain,
        string targetToken,
        long? sourceAmount = null,
        long? targetAmount = null,
        CancellationToken ct = default)
    {
        var queryParams = new List<string>
        {
            $"source_chain={Uri.EscapeDataString(sourceChain)}",
            $"source_token={Uri.EscapeDataString(sourceToken)}",
            $"target_chain={Uri.EscapeDataString(targetChain)}",
            $"target_token={Uri.EscapeDataString(targetToken)}"
        };

        if (sourceAmount.HasValue)
            queryParams.Add($"source_amount={sourceAmount.Value}");

        if (targetAmount.HasValue)
            queryParams.Add($"target_amount={targetAmount.Value}");

        var url = $"quote?{string.Join("&", queryParams)}";
        return await _httpClient.GetFromJsonAsync<QuoteResponse>(url, JsonOptions, ct);
    }
}
