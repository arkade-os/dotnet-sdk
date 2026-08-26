using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VirtualTxs;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NArk.Core.Helpers;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NArk.Core.Wallet.SigningSources;
using NBitcoin;

namespace NArk.Core.Services;

/// <summary>Why a VTXO at an ArkadeCash address could not be swept by <see cref="ArkadeCashService.ClaimAsync"/>.</summary>
public enum ArkadeCashUnclaimedReason
{
    /// <summary>Already spent or settled — most likely the note was claimed before.</summary>
    AlreadySpent,

    /// <summary>Swept by the Arkade server after the VTXO expired; only a unilateral exit could have saved it.</summary>
    ServerSwept,

    /// <summary>Below the server's dust threshold, so it cannot be spent offchain.</summary>
    Subdust,

    /// <summary>Carries Arkade-issued assets, which this thin sweep deliberately does not move.</summary>
    AssetBearing,

    /// <summary>The sweep was attempted and the server rejected it.</summary>
    SweepFailed,
}

/// <summary>One VTXO at an ArkadeCash address that the claim reported instead of sweeping.</summary>
/// <param name="Outpoint">The VTXO's outpoint.</param>
/// <param name="Amount">Its value in satoshis.</param>
/// <param name="Reason">Why it was left behind.</param>
public record ArkadeCashUnclaimedVtxo(OutPoint Outpoint, ulong Amount, ArkadeCashUnclaimedReason Reason);

/// <summary>The outcome of claiming an ArkadeCash note: what moved, and what did not.</summary>
/// <param name="Swept">Total satoshis swept to the destination.</param>
/// <param name="Unclaimed">Every VTXO left behind, with a per-VTXO reason.</param>
public record ArkadeCashClaimResult(ulong Swept, IReadOnlyList<ArkadeCashUnclaimedVtxo> Unclaimed)
{
    /// <summary>Total satoshis left behind at the note's address.</summary>
    public ulong UnclaimedAmount => Unclaimed.Aggregate(0UL, (total, vtxo) => total + vtxo.Amount);
}

/// <summary>
/// Claims <see cref="ArkadeCash"/> bearer instruments.
/// </summary>
/// <remarks>
/// The claim is deliberately thin: nothing is persisted. No contract is imported and the note's key
/// never reaches wallet storage — it signs one offchain transaction per VTXO, in memory, straight to
/// the destination address. That is what makes a note claimable at all: importing its contract would
/// only register a script to watch, since the wallet holds no key matching the note's descriptor and
/// so could never sign for it.
/// <para>
/// Not importing also means the claim does not care whether the Arkade server has rotated its signer
/// since the note was funded. The note is spent under the key it was issued against, which the
/// operator keeps co-signing until that key's deprecation cutoff passes.
/// </para>
/// </remarks>
public class ArkadeCashService(
    IClientTransport transport,
    ISafetyService safetyService,
    IIntentStorage intentStorage,
    IVirtualTxStorage? virtualTxStorage = null,
    IEnumerable<ISpendSubmitHandler>? submitHandlers = null,
    ILogger<ArkadeCashService>? logger = null)
{
    /// <summary>
    /// Sweeps every spendable VTXO at the note's address to <paramref name="destination"/>, and
    /// reports the rest.
    /// </summary>
    /// <param name="cash">The note to claim. Not disposed — the caller owns it.</param>
    /// <param name="destination">Where the swept funds are sent. Normally the claiming wallet's own address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The swept total and a per-VTXO report of anything left behind.</returns>
    /// <remarks>
    /// One transaction per VTXO, so a single stale or rejected input dents only its own sweep instead
    /// of sinking the whole claim. A VTXO that cannot be swept is never fatal: it comes back in
    /// <see cref="ArkadeCashClaimResult.Unclaimed"/> with a reason.
    /// </remarks>
    public async Task<ArkadeCashClaimResult> ClaimAsync(
        ArkadeCash cash,
        ArkAddress destination,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);

        // The note's OWN server key, not the one the server currently advertises: these funds are
        // locked to the key the note was issued against, and that is the script they sit at.
        var contract = cash.ToContract(serverInfo.Network);
        var script = contract.GetScriptPubKey().ToHex();

        var vtxos = new List<ArkVtxo>();
        await foreach (var vtxo in transport.GetVtxoByScriptsAsSnapshot(
                           new HashSet<string> { script }, cancellationToken))
        {
            vtxos.Add(vtxo);
        }

        logger?.LogDebug("ArkadeCash claim: {Count} VTXO(s) at script {Script}", vtxos.Count, script);

        var unclaimed = new List<ArkadeCashUnclaimedVtxo>();
        var spendable = new List<ArkVtxo>();
        foreach (var vtxo in vtxos)
        {
            var reason = Classify(vtxo, serverInfo.Dust);
            if (reason is null)
                spendable.Add(vtxo);
            else
                unclaimed.Add(new ArkadeCashUnclaimedVtxo(vtxo.OutPoint, vtxo.Amount, reason.Value));
        }

        if (spendable.Count == 0)
            return new ArkadeCashClaimResult(0, unclaimed);

        // A wallet provider scoped to this claim: it answers for the note's key and nothing else, and
        // lives only as long as the call. The synthetic identifier never reaches storage — it exists
        // because the transaction builder resolves signers by wallet id.
        var walletId = $"arkadecash:{Convert.ToHexString(cash.Pubkey.ToBytes()).ToLowerInvariant()}";
        var walletProvider = new ArkadeCashWalletProvider(cash);
        var builder = new TransactionHelpers.ArkTransactionBuilder(
            transport, safetyService, walletProvider, intentStorage, virtualTxStorage, submitHandlers);

        var swept = 0UL;
        foreach (var vtxo in spendable)
        {
            try
            {
                var coin = ToCoin(walletId, contract, vtxo);
                var output = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(vtxo.Amount), destination);
                await builder.ConstructAndSubmitArkTransaction([coin], [output], cancellationToken);
                swept += vtxo.Amount;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(0, ex, "ArkadeCash claim: sweeping VTXO {Outpoint} failed", vtxo.OutPoint);
                unclaimed.Add(new ArkadeCashUnclaimedVtxo(
                    vtxo.OutPoint, vtxo.Amount, ArkadeCashUnclaimedReason.SweepFailed));
            }
        }

        logger?.LogInformation(
            "ArkadeCash claim: swept {Swept} sat, left {UnclaimedCount} VTXO(s) behind", swept, unclaimed.Count);
        return new ArkadeCashClaimResult(swept, unclaimed);
    }

    /// <summary>Returns why this VTXO cannot be swept, or <c>null</c> when it can.</summary>
    private static ArkadeCashUnclaimedReason? Classify(ArkVtxo vtxo, Money dust) => vtxo switch
    {
        _ when vtxo.IsSpent() => ArkadeCashUnclaimedReason.AlreadySpent,
        _ when vtxo.Swept => ArkadeCashUnclaimedReason.ServerSwept,
        _ when vtxo.Amount < (ulong)dust.Satoshi => ArkadeCashUnclaimedReason.Subdust,
        _ when vtxo.Assets is { Count: > 0 } => ArkadeCashUnclaimedReason.AssetBearing,
        _ => null,
    };

    /// <summary>Builds the spendable coin for a note VTXO, on the contract's collaborative path.</summary>
    private static ArkCoin ToCoin(string walletId, ArkPaymentContract contract, ArkVtxo vtxo) =>
        new(walletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut,
            contract.User, contract.CollaborativePath(), null, null, null, vtxo.Swept, vtxo.Unrolled,
            assets: vtxo.Assets);

    /// <summary>
    /// An <see cref="IWalletProvider"/> backed solely by a note's key, for the duration of one claim.
    /// Every identifier resolves to the same signer — the caller only ever asks about the synthetic
    /// wallet id it just minted. There is no address provider: a claim sweeps each VTXO whole, so it
    /// never derives a change address.
    /// </summary>
    private sealed class ArkadeCashWalletProvider(ArkadeCash cash) : IWalletProvider
    {
        private readonly IArkadeWalletSigner _signer = new CompositeArkadeWalletSigner(
            new NsecSigningSource(cash.PrivKey));

        public Task<IArkadeWalletSigner?> GetSignerAsync(
            string identifier, CancellationToken cancellationToken = default) => Task.FromResult<IArkadeWalletSigner?>(_signer);

        public Task<IArkadeAddressProvider?> GetAddressProviderAsync(
            string identifier, CancellationToken cancellationToken = default) => Task.FromResult<IArkadeAddressProvider?>(null);
    }
}
