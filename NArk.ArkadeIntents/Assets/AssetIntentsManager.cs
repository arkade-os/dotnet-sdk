using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Models;
using NArk.Core.Assets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Arkade.Emulator;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Scripting;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Asset;
using NArk.ArkadeIntents.SolverRegistry;
using System.Numerics;

namespace NArk.ArkadeIntents.Assets;

/// <summary>
/// A request to create an Arkade swap. For <see cref="ArkadeSwapIntentType.BtcToAsset"/> the wallet deposits
/// <see cref="DepositAmount"/> sats and wants <see cref="WantAmount"/> units of <see cref="Asset"/>;
/// for <see cref="ArkadeSwapIntentType.AssetToBtc"/> it deposits <see cref="DepositAmount"/> units of
/// <see cref="Asset"/> and wants <see cref="WantAmount"/> sats.
/// </summary>
public sealed record CreateSwapRequest(
    string WalletId,
    ArkadeSwapIntentType Type,
    long DepositAmount,
    long WantAmount,
    AssetId Asset);

/// <summary>
/// A request to have a solver quote an Arkade swap before anything is funded.
/// </summary>
/// <param name="WalletId">The wallet that deposits, and that the fill pays.</param>
/// <param name="OfferAsset">The asset deposited, or <c>null</c> to deposit sats.</param>
/// <param name="WantAsset">The asset wanted, or <c>null</c> to want sats.</param>
/// <param name="Amount">
/// Atomic units of whichever leg <paramref name="AmountSide"/> names — sats on a BTC leg, the
/// asset's own atomic unit otherwise.
/// </param>
/// <param name="AmountSide">
/// Which leg <paramref name="Amount"/> fixes. <see cref="RfqAmountSide.From"/> asks "quote me a
/// payout for this deposit", <see cref="RfqAmountSide.To"/> asks "quote me the deposit that reaches
/// this payout" — this corridor serves both.
/// </param>
/// <param name="MaxFromAmount">
/// The most this caller will deposit, or <c>null</c> for no cap. Meaningful only with
/// <see cref="RfqAmountSide.To"/>, where the deposit is the side the solver chose.
/// </param>
/// <param name="MinToAmount">
/// The least this caller will accept, or <c>null</c> for no floor. Meaningful only with
/// <see cref="RfqAmountSide.From"/>, for the same reason.
/// </param>
/// <remarks>
/// Exactly one leg names an asset. Both legs naming one is a swap the wire can express and this
/// covenant cannot: the offer commits to the wanted asset and carries the offered one on the
/// deposit, and the reference client draws the same line.
/// </remarks>
public sealed record QuotedSwapRequest(
    string WalletId,
    AssetId? OfferAsset,
    AssetId? WantAsset,
    BigInteger Amount,
    RfqAmountSide AmountSide = RfqAmountSide.From,
    BigInteger? MaxFromAmount = null,
    BigInteger? MinToAmount = null);

/// <summary>The outcome of funding a quoted Arkade swap.</summary>
/// <param name="Intent">The persisted swap, already funded and pending a fill.</param>
/// <param name="RfqId">The negotiation's correlation id — what a status request asks about.</param>
/// <param name="Quote">The quote, as accepted.</param>
/// <param name="DepositedSats">Sats locked: the deposit itself on a BTC leg, the carrier otherwise.</param>
/// <param name="DepositedAssetUnits">Asset units locked, or <c>null</c> when the deposit was sats.</param>
public sealed record QuotedArkadeSwap(
    ArkadeSwapIntent Intent,
    string RfqId,
    RfqQuote<ArkadeSwapQuoteProfile> Quote,
    long DepositedSats,
    BigInteger? DepositedAssetUnits);

/// <summary>
/// The Arkade swap creation entry point: builds the covenant offer from the wallet's own receive
/// address + signing key, funds it (attaching the offer packet so the solver can fill it), and
/// persists the resulting <see cref="ArkadeSwapIntent"/> as pending — after which the storage-backed
/// <see cref="ArkadeSwapIntentMonitoringService"/> drives it to a terminal status from the covenant VTXO.
/// </summary>
public sealed class AssetIntentsManager
{
    private readonly IClientTransport _transport;
    private readonly IContractService _contractService;
    private readonly IWalletProvider _walletProvider;
    private readonly ISpendingService _spendingService;
    private readonly IArkadeIntentStorage _intentStorage;
    private readonly IVtxoStorage _vtxoStorage;

    /// <summary>A co-signer supplied in place of the network's pin, or <c>null</c>.</summary>
    private readonly string? _emulatorPubkeyOverride;

    public AssetIntentsManager(
        IClientTransport transport,
        IContractService contractService,
        IWalletProvider walletProvider,
        ISpendingService spendingService,
        IArkadeIntentStorage intentStorage,
        IVtxoStorage vtxoStorage,
        IOptions<ArkadeIntentsOptions>? options = null)
    {
        _transport = transport;
        _contractService = contractService;
        _walletProvider = walletProvider;
        _spendingService = spendingService;
        _intentStorage = intentStorage;
        _vtxoStorage = vtxoStorage;
        _emulatorPubkeyOverride = (options?.Value ?? new ArkadeIntentsOptions()).EmulatorPubkeyOverride;
    }

    /// <summary>
    /// Create and fund an Arkade swap: derive a fresh receive address (payout target) and signing key
    /// (cancel signer), build the offer against the current server + emulator keys, deposit to the
    /// swap address with the offer packet attached, and store the intent as pending.
    /// </summary>
    public async Task<ArkadeSwapIntent> CreateSwap(CreateSwapRequest request, CancellationToken cancellationToken = default)
    {
        var serverInfo = await _transport.GetServerInfoAsync(cancellationToken);
        // Parsed, not sliced. This corridor quotes no counterparty address to check the derivation
        // against, so a key mangled here reaches the covenant unchallenged and the deposit lands
        // under a co-signer that does not exist.
        var emulatorPubkey = LightningCorridor.NormalizeToXOnly(
            Convert.FromHexString(
                EmulatorPubKeys.Resolve(serverInfo.NetworkName, _emulatorPubkeyOverride))).ToBytes();

        var addressProvider = await _walletProvider.GetAddressProviderAsync(request.WalletId, cancellationToken)
            ?? throw new InvalidOperationException($"No address provider for wallet '{request.WalletId}'.");

        // Payout target: a fresh receive address (34-byte taproot spk). Cancel signer: a fresh
        // signing key the wallet can spend the covenant's cancel path with.
        var receive = await _contractService.DeriveContract(request.WalletId, NextContractPurpose.Receive,
            cancellationToken: cancellationToken);
        var makerPkScript = receive.GetArkAddress().ScriptPubKey.ToBytes();
        var makerSigner = await addressProvider.GetNextSigningDescriptor(cancellationToken);
        var makerPublicKey = makerSigner.ToXOnlyPubKey().ToBytes();

        var isBtcToAsset = request.Type == ArkadeSwapIntentType.BtcToAsset;
        var created = OfferBuilder.CreateOffer(
            makerPkScript, makerPublicKey, emulatorPubkey, serverInfo.SignerKey, serverInfo.Network,
            request.WantAmount,
            wantAsset: isBtcToAsset ? request.Asset : null,
            offerAsset: isBtcToAsset ? null : request.Asset,
            // On by default: without it both remaining paths need the server, so a server that
            // goes away strands the deposit.
            exitDelay: serverInfo.UnilateralExit);

        var swapAddress = created.Contract.GetArkAddress();
        var deposit = isBtcToAsset
            ? new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(request.DepositAmount), swapAddress)
            : new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, swapAddress)
            {
                // Asset deposit rides on a dust-sat carrier.
                Assets = [new ArkTxOutAsset(request.Asset.ToString(), (ulong)request.DepositAmount)],
            };

        var txid = await _spendingService.Spend(request.WalletId, [deposit], cancellationToken,
            extensionPackets: [OfferPacket.FromPayload(created.Payload)]);

        var intent = new ArkadeSwapIntent
        {
            Id = txid.ToString(),
            WalletId = request.WalletId,
            Type = request.Type,
            // What the deposit is worth IN SATS. For AssetToBtc the deposit amount is asset units
            // riding a dust-sat carrier, so the sats locked are the carrier, not the request's number.
            OfferAmount = isBtcToAsset ? Money.Satoshis(request.DepositAmount) : serverInfo.Dust,
            WantAmount = Money.Satoshis(request.WantAmount),
            Status = ArkadeSwapIntentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            SwapPkScript = swapAddress.ScriptPubKey.ToHex(),
            SwapAddress = created.Address,
            FromAssetId = isBtcToAsset ? "btc" : request.Asset.ToString(),
            ToAssetId = isBtcToAsset ? request.Asset.ToString() : "btc",
        }.WithAssetMetadata(new AssetSwapMetadata(created.OfferHex, makerSigner.ToString()));
        await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);
        return intent;
    }

    /// <summary>
    /// Agree terms with a solver, derive the offer locally, check it, and fund it.
    /// </summary>
    /// <param name="request">What to swap, and on which side the amount is fixed.</param>
    /// <param name="rfqTransport">How to reach the solver.</param>
    /// <param name="solverCard">The solver's published card, when one is known.</param>
    /// <param name="cancellationToken">Cancels before funding; after funding the swap is live regardless.</param>
    /// <returns>The funded swap, the quote it was funded under, and what the deposit carried.</returns>
    /// <exception cref="RfqRefusedException">The solver declined to quote.</exception>
    /// <exception cref="ArkadeSwapNotFundableException">A safety gate refused — nothing was funded.</exception>
    /// <exception cref="OfferAddressMismatchException">The solver's offer is not ours — nothing was funded.</exception>
    /// <remarks>
    /// <para>
    /// The difference from <see cref="CreateSwap"/> is who sets the price. That one publishes a
    /// standing offer at terms the caller picked and waits for any taker; this one asks a named
    /// solver what it will pay, and funds only if the answer is acceptable. Everything after the
    /// quote is the same covenant, so the same <see cref="CancelSwap"/> reclaims an unfilled
    /// deposit — there is no timelock on this class, and no refund path that does not need the
    /// server.
    /// </para>
    /// <para>
    /// From the quote only the <b>binding</b> fields are used: the two amounts and
    /// <c>valid_until</c>. The offer address it publishes is compared against the client's own
    /// derivation and never used, which is what leaves a wrong or malicious solver able to produce
    /// only an offer this client declines.
    /// </para>
    /// <para>
    /// An asset deposit rides on dust sats. The quote publishes how many as <c>carrier_sats</c>,
    /// already netted into the amounts, so sending a different number is not a rounding difference —
    /// it is funding terms the solver did not quote. When a quote omits it, the server's own dust
    /// floor stands in: the safe direction, since a carrier below dust is refused outright.
    /// </para>
    /// </remarks>
    public async Task<QuotedArkadeSwap> CreateQuotedSwap(
        QuotedSwapRequest request,
        IRfqTransport rfqTransport,
        SolverCard? solverCard = null,
        CancellationToken cancellationToken = default)
    {
        if (request.OfferAsset is null == (request.WantAsset is null))
        {
            throw new ArgumentException(
                "set exactly one of OfferAsset (BTC→asset) or WantAsset (asset→BTC): with neither " +
                "both legs are sats, and with both the offer covenant has no leg to commit to",
                nameof(request));
        }

        var serverInfo = await _transport.GetServerInfoAsync(cancellationToken);
        var emulatorPubkey = LightningCorridor.NormalizeToXOnly(
            Convert.FromHexString(
                EmulatorPubKeys.Resolve(serverInfo.NetworkName, _emulatorPubkeyOverride))).ToBytes();

        var addressProvider = await _walletProvider.GetAddressProviderAsync(request.WalletId, cancellationToken)
            ?? throw new InvalidOperationException($"No address provider for wallet '{request.WalletId}'.");

        // Read once, and read BEFORE the request: these two values are the covenant parameters the
        // solver quotes against, so a wallet that rotated its receive address between the request
        // and the derivation would derive an offer the solver never saw.
        var receive = await _contractService.DeriveContract(request.WalletId, NextContractPurpose.Receive,
            cancellationToken: cancellationToken);
        var makerPkScript = receive.GetArkAddress().ScriptPubKey.ToBytes();
        var makerSigner = await addressProvider.GetNextSigningDescriptor(cancellationToken);
        var makerPublicKey = makerSigner.ToXOnlyPubKey().ToBytes();

        var rfqRequest = ArkadeSwapProfile.Request(
            request.OfferAsset, request.WantAsset, request.Amount, request.AmountSide,
            makerPkScript, makerPublicKey);

        // Asked before the request rather than after the refusal: a size outside the advertised
        // range is one the solver declines anyway, and its refusal cannot say by how much.
        if (solverCard is not null)
        {
            if (request.AmountSide == RfqAmountSide.From)
            {
                SolverTerms.AssertInputWithinLimits(solverCard, rfqRequest.Pair, request.Amount);
            }
            else
            {
                SolverTerms.AssertWithinLimits(solverCard, rfqRequest.Pair, request.Amount);
            }
        }

        var quote = await rfqTransport
            .RequestQuoteAsync<ArkadeSwapRequestProfile, ArkadeSwapQuoteProfile>(rfqRequest, cancellationToken);

        ArkadeSwapGates.AssertFundable(
            quote, rfqRequest.Pair, request.Amount, request.AmountSide,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), request.MaxFromAmount, request.MinToAmount);

        // The want amount is the quote's payout, and it is what the covenant commits to — the whole
        // point of quoting first is that this number came from the solver rather than from a guess.
        var created = OfferBuilder.CreateOffer(
            makerPkScript, makerPublicKey, emulatorPubkey, serverInfo.SignerKey, serverInfo.Network,
            WantAmountOf(quote),
            wantAsset: request.WantAsset,
            offerAsset: request.OfferAsset,
            exitDelay: serverInfo.UnilateralExit);

        ArkadeSwapGates.AssertOfferIsOurs(quote, created);

        var swapAddress = created.Contract.GetArkAddress();
        var isBtcDeposit = request.OfferAsset is null;
        var carrierSats = quote.CarrierSats is { } carrier && carrier > BigInteger.Zero
            ? Money.Satoshis((long)carrier)
            : serverInfo.Dust;
        var depositSats = isBtcDeposit ? Money.Satoshis((long)quote.FromAtomicAmount) : carrierSats;

        var deposit = isBtcDeposit
            ? new ArkTxOut(ArkTxOutType.Vtxo, depositSats, swapAddress)
            : new ArkTxOut(ArkTxOutType.Vtxo, depositSats, swapAddress)
            {
                Assets = [new ArkTxOutAsset(request.OfferAsset!.ToString(), (ulong)quote.FromAtomicAmount)],
            };

        var txid = await _spendingService.Spend(request.WalletId, [deposit], cancellationToken,
            extensionPackets: [OfferPacket.FromPayload(created.Payload)]);

        // Keyed by the funding txid, exactly as an unquoted offer is: the cancel path looks the
        // deposit up by it, so the negotiation's own id rides in the metadata instead.
        var intent = new ArkadeSwapIntent
        {
            Id = txid.ToString(),
            WalletId = request.WalletId,
            Type = request.WantAsset is not null
                ? ArkadeSwapIntentType.BtcToAsset
                : ArkadeSwapIntentType.AssetToBtc,
            OfferAmount = depositSats,
            WantAmount = Money.Satoshis(WantAmountOf(quote)),
            Status = ArkadeSwapIntentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            SwapPkScript = swapAddress.ScriptPubKey.ToHex(),
            SwapAddress = created.Address,
            FromAssetId = isBtcDeposit ? "btc" : request.OfferAsset!.ToString(),
            ToAssetId = request.WantAsset is null ? "btc" : request.WantAsset.ToString(),
        }
            .WithAssetMetadata(new AssetSwapMetadata(created.OfferHex, makerSigner.ToString()))
            .WithRfqId(rfqRequest.RfqId)
            .WithSolver(quote.SolverPubkey);
        await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        return new QuotedArkadeSwap(
            intent,
            rfqRequest.RfqId,
            quote,
            depositSats.Satoshi,
            isBtcDeposit ? null : quote.FromAtomicAmount);
    }

    /// <summary>
    /// The quote's payout, narrowed to what the offer TLV can carry.
    /// </summary>
    /// <remarks>
    /// The wire is 256-bit and the offer's <c>wantAmount</c> record is 64. Nothing a solver quotes
    /// today comes close, but the narrowing is explicit so an amount that did would fail here —
    /// before a deposit — rather than wrapping into a covenant obliging a payment of something else.
    /// </remarks>
    private static long WantAmountOf(RfqQuote<ArkadeSwapQuoteProfile> quote)
    {
        if (quote.ToAtomicAmount > long.MaxValue)
        {
            throw new ArkadeSwapNotFundableException(
                ArkadeSwapRefusal.AmountRejected,
                $"the quote pays {quote.ToAtomicAmount}, more than the offer's want-amount record holds");
        }
        return (long)quote.ToAtomicAmount;
    }

    /// <summary>
    /// Cancel a pending swap: reclaim the deposit by spending the covenant's <c>cancel</c> path
    /// (<c>$user</c>+<c>$server</c>) back to the wallet. The intent is moved to
    /// <see cref="ArkadeSwapIntentStatus.Cancelling"/> before the spend so the monitor can't read the
    /// cancel spend as a fill; on success it becomes <see cref="ArkadeSwapIntentStatus.Cancelled"/>, and on
    /// failure it rolls back to <see cref="ArkadeSwapIntentStatus.Pending"/>.
    /// </summary>
    public async Task<ArkadeSwapIntent> CancelSwap(string swapId, CancellationToken cancellationToken = default)
    {
        var intent = await _intentStorage.GetArkadeSwapIntent(swapId, cancellationToken)
                     ?? throw new InvalidOperationException($"Swap '{swapId}' not found.");
        if (intent.Status != ArkadeSwapIntentStatus.Pending)
            throw new InvalidOperationException($"Swap '{swapId}' is not pending (status {intent.Status}).");
        var assetMetadata = intent.AssetMetadata();
        if (assetMetadata.MakerDescriptor is not { } makerDescriptorStr)
            throw new InvalidOperationException($"Swap '{swapId}' has no maker descriptor to sign the cancel path.");

        // Move out of Pending BEFORE spending so the monitor can't read the cancel-spend as a fill.
        intent.Status = ArkadeSwapIntentStatus.Cancelling;
        await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        try
        {
            var serverInfo = await _transport.GetServerInfoAsync(cancellationToken);
            var offer = OfferCodec.Decode(Convert.FromHexString(assetMetadata.OfferHex));
            var maker = OutputDescriptor.Parse(makerDescriptorStr, serverInfo.Network);
            var contract = OfferBuilder.BuildContract(offer, serverInfo.SignerKey, serverInfo.Network, maker);

            // The rebuilt covenant must land on the script that was funded. It is rebuilt against
            // the CURRENT server key, so a rotation between funding and cancel changes the tree —
            // and spending against the wrong tree fails at the server with nothing pointing at why.
            var rebuilt = contract.GetArkAddress().ScriptPubKey.ToHex();
            if (!string.Equals(rebuilt, intent.SwapPkScript, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"the rebuilt covenant for swap '{swapId}' does not match the funded script " +
                    $"(rebuilt {rebuilt}, funded {intent.SwapPkScript}) — has the Arkade server rotated its key?");
            }

            // Only this swap's own funding output, never "whatever sits at the address": identical
            // offers derive identical addresses, so a fallback could spend somebody else's deposit.
            var vtxos = await _vtxoStorage.GetVtxos(scripts: [intent.SwapPkScript], cancellationToken: cancellationToken);
            var vtxo = vtxos.FirstOrDefault(v => v.TransactionId == intent.Id && !v.IsSpent() && !v.Swept)
                ?? throw new InvalidOperationException(
                    $"the funding output {intent.Id} of swap '{swapId}' is not spendable at the swap address");

            var coin = await new ArkProgramContractTransformer(_walletProvider)
                .Transform(intent.WalletId, contract, vtxo, "cancel");

            // Return the deposit (and any asset it carried) to a fresh receive address.
            var payout = (await _contractService.DeriveContract(intent.WalletId, NextContractPurpose.Receive,
                cancellationToken: cancellationToken)).GetArkAddress();
            var output = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis((long)vtxo.Amount), payout)
            {
                Assets = vtxo.Assets is { Count: > 0 } assets
                    ? assets.Select(a => new ArkTxOutAsset(a.AssetId, a.Amount)).ToList()
                    : null,
            };

            await _spendingService.Spend(intent.WalletId, [coin], [output], cancellationToken);

            intent.Status = ArkadeSwapIntentStatus.Cancelled;
            await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);
            return intent;
        }
        catch
        {
            // Roll back so the swap can still complete or be retried.
            intent.Status = ArkadeSwapIntentStatus.Pending;
            await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);
            throw;
        }
    }
}
