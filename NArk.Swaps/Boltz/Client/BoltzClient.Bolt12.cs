using System.Net.Http.Json;
using NArk.Swaps.Boltz.Models.Bolt12;

namespace NArk.Swaps.Boltz.Client;

public partial class BoltzClient
{
    // ─── Bolt12 offer bridge ───────────────────────────────────────────────────
    // These endpoints are for operators who run a BOLT 12-capable Lightning node
    // (CLN, LDK) and want incoming offer payments converted to Arkade VTXOs by
    // Boltz. They are distinct from the submarine-swap fetch flow below.

    /// <summary>
    /// Retrieves the parameters needed to construct a BOLT 12 offer that routes
    /// payments through Boltz to the specified receiving chain via
    /// <c>GET /v2/lightning/{currency}/bolt12/{receiving}</c>.
    /// </summary>
    /// <param name="currency">Lightning currency, e.g. <c>"BTC"</c>.</param>
    /// <param name="receiving">
    /// Symbol of the chain that should receive the swapped funds, e.g. <c>"ARK"</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Bolt12OfferParams"/> containing the minimum CLTV delta.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// Thrown when the currency has no BOLT 12 support (HTTP 404) or the
    /// receiving symbol is not supported (HTTP 400).
    /// </exception>
    public virtual async Task<Bolt12OfferParams> GetBolt12OfferParamsAsync(
        string currency,
        string receiving,
        CancellationToken ct = default)
    {
        var resp = await _httpClient.GetAsync($"v2/lightning/{currency}/bolt12/{receiving}", ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<Bolt12OfferParams>(ct))!;
    }

    /// <summary>
    /// Registers a BOLT 12 offer with Boltz via
    /// <c>POST /v2/lightning/{currency}/bolt12</c> so that payments to the offer
    /// are converted to Arkade VTXOs.
    /// </summary>
    /// <param name="currency">Lightning currency, e.g. <c>"BTC"</c>.</param>
    /// <param name="request">Offer string and optional webhook URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="HttpRequestException">
    /// Thrown on HTTP 400 (invalid request) or 404 (currency has no BOLT 12 support).
    /// </exception>
    public virtual async Task RegisterBolt12OfferAsync(
        string currency,
        Bolt12RegisterOfferRequest request,
        CancellationToken ct = default)
    {
        var resp = await _httpClient.PostAsJsonAsync(
            $"v2/lightning/{currency}/bolt12", request, JsonOptions, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(body, null, resp.StatusCode);
        }
    }

    /// <summary>
    /// Updates the webhook URL of a registered BOLT 12 offer via
    /// <c>PATCH /v2/lightning/{currency}/bolt12</c>.
    /// </summary>
    /// <param name="currency">Lightning currency, e.g. <c>"BTC"</c>.</param>
    /// <param name="request">
    /// Offer string, new webhook URL (or <c>null</c> to remove), and a Schnorr
    /// signature authorising the change.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="HttpRequestException">
    /// Thrown on HTTP 400, 404, or 422 (invalid signature).
    /// </exception>
    public virtual async Task UpdateBolt12OfferAsync(
        string currency,
        Bolt12UpdateOfferRequest request,
        CancellationToken ct = default)
    {
        var resp = await _httpClient.PatchAsJsonAsync(
            $"v2/lightning/{currency}/bolt12", request, JsonOptions, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(body, null, resp.StatusCode);
        }
    }

    /// <summary>
    /// Deletes a registered BOLT 12 offer via
    /// <c>POST /v2/lightning/{currency}/bolt12/delete</c>.
    /// </summary>
    /// <param name="currency">Lightning currency, e.g. <c>"BTC"</c>.</param>
    /// <param name="request">Offer string and a Schnorr signature authorising deletion.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="HttpRequestException">
    /// Thrown on HTTP 404 (offer not found or currency unsupported).
    /// </exception>
    public virtual async Task DeleteBolt12OfferAsync(
        string currency,
        Bolt12DeleteOfferRequest request,
        CancellationToken ct = default)
    {
        var resp = await _httpClient.PostAsJsonAsync(
            $"v2/lightning/{currency}/bolt12/delete", request, JsonOptions, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(body, null, resp.StatusCode);
        }
    }

    // ─── Bolt12 submarine (paying an offer) ───────────────────────────────────

    /// <summary>
    /// Fetches a BOLT 12 invoice from the given offer via
    /// <c>POST /v2/lightning/{currency}/bolt12/fetch</c>.
    /// </summary>
    /// <param name="currency">Lightning currency, e.g. <c>"BTC"</c>.</param>
    /// <param name="request">Offer string, amount in satoshis, and optional note.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="Bolt12FetchResponse"/> containing the BOLT 12 invoice
    /// (<c>lni1…</c>) and an optional magic routing hint.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// Thrown when the currency has no BOLT 12 support (HTTP 404) or when
    /// Boltz could not reach the offer's node to obtain an invoice (HTTP 500).
    /// </exception>
    public virtual Task<Bolt12FetchResponse> FetchBolt12InvoiceAsync(
        string currency,
        Bolt12FetchRequest request,
        CancellationToken ct = default)
        => PostAsJsonAsync<Bolt12FetchRequest, Bolt12FetchResponse>(
            $"v2/lightning/{currency}/bolt12/fetch", request, ct);
}
