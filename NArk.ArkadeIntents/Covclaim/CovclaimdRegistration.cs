using Microsoft.Extensions.Logging;
using NArk.Arkade.Contracts;

namespace NArk.ArkadeIntents.Covclaim;

/// <summary>Hands one lockup to covclaimd, and never lets that failure become the swap's failure.</summary>
/// <remarks>
/// Shared by both receive corridors on purpose: the daemon binds a registration to the address, the
/// arkade script and the taptree together, so two call sites drifting apart would show up as one
/// corridor whose claims silently stop being registered.
/// </remarks>
public static class CovclaimdRegistration
{
    /// <summary>Register a lockup's claim with covclaimd, if there is a daemon configured at all.</summary>
    /// <param name="client">The daemon, or <c>null</c> when none is configured.</param>
    /// <param name="contract">The lockup, which supplies both the claim script and the taptree.</param>
    /// <param name="lockupAddress">The lockup's address, as the daemon will see it funded.</param>
    /// <param name="preimage">The secret, sealed to the daemon inside the client.</param>
    /// <param name="logger">Optional log sink.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>true</c> when the daemon accepted the registration.</returns>
    public static async Task<bool> TryRegisterAsync(
        ICovclaimdClient? client,
        VHTLCv2Contract contract,
        string lockupAddress,
        byte[] preimage,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (client is null)
        {
            return false;
        }

        if (contract.NonInteractiveClaim is null)
        {
            logger?.LogDebug(
                "skipping covclaimd registration for {LockupAddress}: the lockup carries no " +
                "non-interactive claim leaf", lockupAddress);
            return false;
        }

        try
        {
            await client.RevealAsync(
                lockupAddress,
                preimage,
                contract.NonInteractiveClaimArkadeScript,
                contract.GetTapScriptList(),
                cancellationToken);
            return true;
        }
        catch (CovclaimdException ex)
        {
            // Swallowed: the wallet's own claimer still races for this lockup, so what was lost is
            // redundancy, not the swap.
            logger?.LogWarning(
                ex, "covclaimd did not register a claim for {LockupAddress}; this wallet remains " +
                    "the only claimant", lockupAddress);
            return false;
        }
    }
}
