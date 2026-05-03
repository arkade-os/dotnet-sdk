namespace NArk.Swaps.Boltz.Client;

/// <summary>
/// Thrown by <see cref="BoltzClient"/> when Boltz's <c>v2/swap/{id}</c> endpoint
/// responds with HTTP 404 and a body of the form
/// <c>{"error":"could not find swap with id: ..."}</c>. This signals the
/// configured Boltz instance has no record of the swap — typically because the
/// swap was created against a different Boltz endpoint that the client has
/// since been pointed away from.
/// </summary>
/// <remarks>
/// Distinct from a generic <see cref="HttpRequestException"/> so callers can
/// reason about "swap is unknown to this provider" without conflating it with
/// transient network or 5xx errors.
/// </remarks>
public class BoltzSwapNotFoundException : Exception
{
    /// <summary>The swap ID that was not found.</summary>
    public string SwapId { get; }

    public BoltzSwapNotFoundException(string swapId, string? boltzErrorMessage)
        : base($"Boltz returned 404 for swap '{swapId}': {boltzErrorMessage ?? "(no body)"}")
    {
        SwapId = swapId;
    }
}
