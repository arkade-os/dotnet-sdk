using NArk.Abstractions.Helpers;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.ArkadeIntents.Lightning;

/// <summary>
/// Reads a swap's preimage back out of whatever spent its lockup.
/// </summary>
/// <remarks>
/// <para>
/// A Lightning swap is filled only when a preimage proves it, and this is where that proof comes
/// from. Nothing else in a spend distinguishes a fill from a refund: the covenant's non-interactive
/// refund carries no timelock, so the counterparty can push it at any moment, and both leaves leave
/// the same trace — an output that used to be there and now is not.
/// </para>
/// <para>
/// Every candidate is checked against the payment hash before it is believed. A witness of the right
/// shape is not evidence; a preimage that hashes to the invoice's own hash is, and it is evidence
/// nobody can forge, which is what makes this safe to act on without trusting the counterparty's
/// account of what it did.
/// </para>
/// </remarks>
public static class SwapPreimageReader
{
    /// <summary>
    /// Find the preimage in the transaction that spent <paramref name="lockup"/>, if it revealed one.
    /// </summary>
    /// <param name="transport">Where to fetch the spending transaction from.</param>
    /// <param name="lockup">The lockup outpoint the swap was funded at.</param>
    /// <param name="spendingTxid">The transaction that spent it.</param>
    /// <param name="paymentHashHex">The invoice's payment hash, big-endian hex.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The preimage, or <c>null</c> when the spend proved none.</returns>
    /// <remarks>
    /// A <c>null</c> answer never means "refunded" on its own — an unreachable indexer and a
    /// transaction that genuinely carried no preimage are the same silence here, and both callers
    /// should treat the same way: the swap is not provably filled, which is all this can honestly say.
    /// </remarks>
    public static async Task<byte[]?> FindAsync(
        IClientTransport transport,
        OutPoint lockup,
        string spendingTxid,
        string paymentHashHex,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(spendingTxid) || string.IsNullOrWhiteSpace(paymentHashHex))
        {
            return null;
        }

        IReadOnlyList<string> raw;
        try
        {
            raw = await transport.GetVirtualTxsAsync([spendingTxid], cancellationToken);
        }
        catch (Exception)
        {
            // Read lag and an outage look alike from here, and neither is proof of anything.
            return null;
        }

        foreach (var psbtBase64 in raw)
        {
            if (!PSBT.TryParse(psbtBase64, Network.Main, out var psbt))
            {
                continue;
            }

            for (var i = 0; i < psbt.Inputs.Count; i++)
            {
                var input = psbt.Inputs[i];
                if (input.PrevOut != lockup)
                {
                    continue;
                }

                foreach (var candidate in Candidates(input))
                {
                    if (Matches(candidate, paymentHashHex))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Where a preimage can be found on a spending input, in the order worth trying.
    /// </summary>
    /// <remarks>
    /// The Ark-specific condition field first, because that is where a spend built by this SDK puts
    /// it, then the finalised witness stack for a spend built by anything else. Neither is trusted on
    /// its own — see <see cref="Matches"/>.
    /// </remarks>
    private static IEnumerable<byte[]> Candidates(PSBTInput input)
    {
        if (input.GetArkFieldConditionWitness() is { } condition)
        {
            foreach (var push in condition.Pushes)
            {
                yield return push;
            }
        }

        if (input.FinalScriptWitness is { } final)
        {
            foreach (var push in final.Pushes)
            {
                yield return push;
            }
        }
    }

    private static bool Matches(byte[] candidate, string paymentHashHex) =>
        candidate.Length == 32
        && string.Equals(
            Convert.ToHexString(NBitcoin.Crypto.Hashes.SHA256(candidate)),
            paymentHashHex,
            StringComparison.OrdinalIgnoreCase);
}
