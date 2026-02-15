using System.Net.Http.Json;
using NArk.Swaps.Boltz.Models.Swaps.Chain;

namespace NArk.Swaps.Boltz.Client;

public partial class BoltzClient
{
    // Chain Swap Pairs

    /// <summary>
    /// Gets the chain swap pairs information including fees and limits.
    /// </summary>
    public virtual async Task<ChainPairsResponse?> GetChainPairsAsync(CancellationToken cancellation = default)
    {
        return await _httpClient.GetFromJsonAsync<ChainPairsResponse>("v2/swap/chain", cancellation);
    }

    // Chain Swap Creation

    /// <summary>
    /// Creates a new chain swap.
    /// </summary>
    public virtual async Task<ChainResponse> CreateChainSwapAsync(ChainRequest request, CancellationToken cancellation = default)
    {
        return await PostAsJsonAsync<ChainRequest, ChainResponse>("v2/swap/chain", request, cancellation);
    }

    // Chain Swap Claiming (MuSig2 cooperative)

    /// <summary>
    /// Gets Boltz's signing details for cooperative chain swap claim.
    /// Returns Boltz's pubNonce, publicKey, and transactionHash to cross-sign.
    /// </summary>
    public virtual async Task<ChainClaimDetails?> GetChainClaimDetailsAsync(string swapId, CancellationToken cancellation = default)
    {
        var resp = await _httpClient.GetAsync($"v2/swap/chain/{swapId}/claim", cancellation);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellation);
            throw new HttpRequestException($"GET chain/{swapId}/claim failed ({resp.StatusCode}): {body}", null, resp.StatusCode);
        }
        return await resp.Content.ReadFromJsonAsync<ChainClaimDetails>(cancellationToken: cancellation);
    }

    /// <summary>
    /// Submits claim signature and unsigned transaction for MuSig2 cooperative claim.
    /// Returns Boltz's partial signature for our claim transaction.
    /// </summary>
    public virtual async Task<PartialSignatureData?> PostChainClaimAsync(string swapId, ChainClaimRequest request, CancellationToken cancellation = default)
    {
        return await PostAsJsonAsync<ChainClaimRequest, PartialSignatureData?>($"v2/swap/chain/{swapId}/claim", request, cancellation);
    }

    /// <summary>
    /// Submits only a cross-signature for Boltz to claim the user's BTC lockup.
    /// Used for BTC→ARK chain swaps where we don't need a response signature.
    /// Boltz returns an empty body on success.
    /// </summary>
    public virtual async Task PostChainClaimCrossSignatureAsync(string swapId, ChainClaimRequest request, CancellationToken cancellation = default)
    {
        var resp = await _httpClient.PostAsJsonAsync($"v2/swap/chain/{swapId}/claim", request, JsonOptions, cancellation);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellation);
            throw new HttpRequestException($"POST chain/{swapId}/claim cross-sign failed ({resp.StatusCode}): {body}", null, resp.StatusCode);
        }
    }

    // Chain Swap Refunding

    /// <summary>
    /// Requests cooperative BTC-side refund via MuSig2 for a chain swap.
    /// </summary>
    public virtual async Task<PartialSignatureData> RefundChainSwapAsync(string swapId, ChainRefundRequest request, CancellationToken cancellation = default)
    {
        return await PostAsJsonAsync<ChainRefundRequest, PartialSignatureData>($"v2/swap/chain/{swapId}/refund", request, cancellation);
    }

    /// <summary>
    /// Requests cooperative Ark-side refund for a chain swap (PSBT-based).
    /// </summary>
    public virtual async Task<ChainArkRefundResponse> RefundChainSwapArkAsync(string swapId, ChainArkRefundRequest request, CancellationToken cancellation = default)
    {
        return await PostAsJsonAsync<ChainArkRefundRequest, ChainArkRefundResponse>($"v2/swap/chain/{swapId}/refund/ark", request, cancellation);
    }

    // Chain Swap Quotes (renegotiation)

    /// <summary>
    /// Gets a quote for a chain swap in transaction.lockupFailed state.
    /// </summary>
    public virtual async Task<ChainQuote?> GetChainQuoteAsync(string swapId, CancellationToken cancellation = default)
    {
        return await _httpClient.GetFromJsonAsync<ChainQuote>($"v2/swap/chain/{swapId}/quote", cancellation);
    }

    /// <summary>
    /// Accepts a chain swap quote to renegotiate the swap amount.
    /// </summary>
    public virtual async Task AcceptChainQuoteAsync(string swapId, ChainQuote quote, CancellationToken cancellation = default)
    {
        var resp = await _httpClient.PostAsJsonAsync($"v2/swap/chain/{swapId}/quote", quote, cancellation);
        resp.EnsureSuccessStatusCode();
    }

    // Transaction Broadcasting

    /// <summary>
    /// Broadcasts a raw BTC transaction via Boltz.
    /// </summary>
    public virtual async Task<BroadcastResponse> BroadcastBtcTransactionAsync(BroadcastRequest request, CancellationToken cancellation = default)
    {
        return await PostAsJsonAsync<BroadcastRequest, BroadcastResponse>("v2/chain/BTC/transaction", request, cancellation);
    }
}
