using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.SolverRegistry;
using NBitcoin;

namespace NArk.ArkadeIntents.Composition;

/// <summary>Outgoing quote seam used by the client-side composer.</summary>
public interface IEvmOutgoingQuoteClient
{
    /// <summary>Creates and persists L before any ingress quote is requested.</summary>
    Task<PendingEvmSend> CreateSendQuoteAsync(string walletId, long amountSats, string evmClaimAddress,
        EvmSendPolicy policy, IRfqTransport rfqTransport, SwapLinkSecret? secret = null,
        string? rfqId = null, SolverCard? solverCard = null, ArkContract? refundContract = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Lightning ingress quote seam used by the client-side composer.</summary>
public interface ILightningIngressQuoteClient
{
    /// <summary>Creates M with its non-interactive claim pinned to L.</summary>
    Task<PendingLightningReceive> ReceiveFromLightningIntoAsync(string walletId, long amountSats,
        IRfqTransport rfqTransport, string covclaimdPubKey, SwapLinkSecret secret,
        ArkAddress payoutAddress, ArkContract receiverContract, SolverCard? solverCard = null,
        string? rfqId = null, CancellationToken cancellationToken = default);
}

/// <summary>Bitcoin-chain ingress quote seam used by the client-side composer.</summary>
public interface IOnchainIngressQuoteClient
{
    /// <summary>Creates the L1 HTLC and M with its non-interactive claim pinned to L.</summary>
    Task<PendingOnchainReceive> ReceiveFromOnchainIntoAsync(string walletId, long amountSats,
        IRfqTransport rfqTransport, string covclaimdPubKey, BitcoinAddress l1RefundAddress,
        SwapLinkSecret secret, ArkAddress payoutAddress, ArkContract receiverContract,
        SolverCard? solverCard = null, string? rfqId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Dynamic timing policy for independently quoted route legs.</summary>
public sealed record ComposedSwapOptions
{
    /// <summary>Time reserved after checkout expiry to get L observed by the outgoing solver.</summary>
    public int OutgoingFundingSafetySeconds { get; init; } = 30;

    /// <summary>Smallest checkout window accepted after both quotes exist.</summary>
    public int MinimumCheckoutWindowSeconds { get; init; } = 60;

    internal void Validate()
    {
        if (OutgoingFundingSafetySeconds < 0 || MinimumCheckoutWindowSeconds < 1)
            throw new ArgumentException("composed swap checkout timing is invalid");
    }
}

/// <summary>An Arkade payment address whose verified settlement is an ERC20 delivery.</summary>
/// <param name="Outgoing">Verified outgoing quote and protected route secret.</param>
/// <param name="CheckoutExpiresAt">Last Unix second at which the address may be shown for payment.</param>
public sealed record ArkadeToEvmRoute(PendingEvmSend Outgoing, long CheckoutExpiresAt);

/// <summary>A Lightning invoice whose M claim feeds the outgoing Arkade lock L.</summary>
/// <param name="Outgoing">First, independent Arkade-to-EVM quote.</param>
/// <param name="Ingress">Second Lightning-to-Arkade quote using the same H and paying L.</param>
/// <param name="CheckoutExpiresAt">Last Unix second at which the invoice may be shown.</param>
/// <param name="IngressFeeSats">Customer-paid from-to spread.</param>
public sealed record LightningToEvmRoute(PendingEvmSend Outgoing, PendingLightningReceive Ingress,
    long CheckoutExpiresAt, long IngressFeeSats);

/// <summary>An L1 address whose Arkade delivery feeds the outgoing Arkade lock L.</summary>
/// <param name="Outgoing">First, independent Arkade-to-EVM quote.</param>
/// <param name="Ingress">Second onchain-to-Arkade quote using the same H and paying L.</param>
/// <param name="CheckoutExpiresAt">Last Unix second at which the L1 address may be shown.</param>
/// <param name="IngressFeeSats">Customer-paid from-to spread.</param>
public sealed record OnchainToEvmRoute(PendingEvmSend Outgoing, PendingOnchainReceive Ingress,
    long CheckoutExpiresAt, long IngressFeeSats);

/// <summary>Composes independent solver quotes entirely on the client.</summary>
/// <remarks>
/// The outgoing quote is always completed first. A receive quote then reuses H and pins its claim
/// to L. No solver is told that the two RFQs form a route. Claiming M reveals P before an EVM lock
/// necessarily exists, so this mechanism relies on the configured outgoing solver state machine;
/// only a verified ERC20 claim is final settlement.
/// </remarks>
public sealed class ComposedSwapClient(
    IEvmOutgoingQuoteClient evm,
    ILightningIngressQuoteClient lightning,
    IOnchainIngressQuoteClient onchain,
    ComposedSwapOptions? options = null,
    TimeProvider? timeProvider = null)
{
    private readonly ComposedSwapOptions _options = Validated(options);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Creates a direct Arkade-to-EVM route.</summary>
    public async Task<ArkadeToEvmRoute> CreateArkadeAsync(string walletId, long amountSats,
        string evmClaimAddress, EvmSendPolicy policy, IRfqTransport outgoingTransport,
        string? outgoingRfqId = null, SolverCard? outgoingCard = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePreparedIds(outgoingRfqId, null);
        var outgoing = await evm.CreateSendQuoteAsync(walletId, amountSats, evmClaimAddress, policy,
            outgoingTransport, SwapLinkSecret.Generate(), outgoingRfqId, outgoingCard,
            cancellationToken: cancellationToken);
        return new ArkadeToEvmRoute(outgoing, CheckoutExpiry(outgoing.Quote.ValidUntil));
    }

    /// <summary>Creates outgoing L first, then a Lightning invoice whose M claim pays L.</summary>
    public async Task<LightningToEvmRoute> CreateLightningAsync(string walletId, long amountSats,
        string evmClaimAddress, EvmSendPolicy policy, IRfqTransport outgoingTransport,
        IRfqTransport ingressTransport, string covclaimdPubKey, string? outgoingRfqId = null,
        string? ingressRfqId = null, SolverCard? outgoingCard = null, SolverCard? ingressCard = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePreparedIds(outgoingRfqId, ingressRfqId);
        var secret = SwapLinkSecret.Generate();
        var outgoing = await evm.CreateSendQuoteAsync(walletId, amountSats, evmClaimAddress, policy,
            outgoingTransport, secret, outgoingRfqId, outgoingCard, cancellationToken: cancellationToken);
        var ingress = await lightning.ReceiveFromLightningIntoAsync(
            walletId, amountSats, ingressTransport, covclaimdPubKey, secret,
            outgoing.Contract.GetArkAddress(), outgoing.RefundContract, ingressCard, ingressRfqId,
            cancellationToken);
        var expiry = ValidateLinked(outgoing, ingress.RfqId, ingress.PaymentHash,
            ingress.Quote.FromAmount, ingress.Quote.ToAmount, ingress.PayoutAddress,
            ingress.Quote.ValidUntil, amountSats);
        return new LightningToEvmRoute(
            outgoing, ingress, expiry, checked(ingress.Quote.FromAmount - ingress.Quote.ToAmount));
    }

    /// <summary>Creates outgoing L first, then an L1 receive whose M claim pays L.</summary>
    public async Task<OnchainToEvmRoute> CreateOnchainAsync(string walletId, long amountSats,
        string evmClaimAddress, EvmSendPolicy policy, IRfqTransport outgoingTransport,
        IRfqTransport ingressTransport, string covclaimdPubKey, BitcoinAddress l1RefundAddress,
        string? outgoingRfqId = null, string? ingressRfqId = null,
        SolverCard? outgoingCard = null, SolverCard? ingressCard = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePreparedIds(outgoingRfqId, ingressRfqId);
        var secret = SwapLinkSecret.Generate();
        var outgoing = await evm.CreateSendQuoteAsync(walletId, amountSats, evmClaimAddress, policy,
            outgoingTransport, secret, outgoingRfqId, outgoingCard, cancellationToken: cancellationToken);
        var ingress = await onchain.ReceiveFromOnchainIntoAsync(
            walletId, amountSats, ingressTransport, covclaimdPubKey, l1RefundAddress, secret,
            outgoing.Contract.GetArkAddress(), outgoing.RefundContract, ingressCard, ingressRfqId,
            cancellationToken);
        var expiry = ValidateLinked(outgoing, ingress.RfqId, ingress.PaymentHash,
            ingress.Quote.FromAmount, ingress.Quote.ToAmount, ingress.PayoutAddress,
            ingress.Quote.ValidUntil, amountSats);
        return new OnchainToEvmRoute(
            outgoing, ingress, expiry, checked(ingress.Quote.FromAmount - ingress.Quote.ToAmount));
    }

    private long ValidateLinked(PendingEvmSend outgoing, string ingressRfqId, string paymentHash,
        long ingressFromAmount, long ingressToAmount, string payoutAddress,
        long ingressValidUntil, long amountSats)
    {
        if (outgoing.RfqId == ingressRfqId || outgoing.Secret.PaymentHash != paymentHash
            || outgoing.Quote.FromAmount != amountSats || ingressToAmount != amountSats
            || ingressFromAmount < ingressToAmount || payoutAddress != outgoing.LockupAddress)
            throw new InvalidOperationException("independent quote legs do not form the requested H/M-to-L route");
        return CheckoutExpiry(Math.Min(outgoing.Quote.ValidUntil, ingressValidUntil));
    }

    private long CheckoutExpiry(long quoteValidUntil)
    {
        var expiry = checked(quoteValidUntil - _options.OutgoingFundingSafetySeconds);
        if (expiry - _time.GetUtcNow().ToUnixTimeSeconds() < _options.MinimumCheckoutWindowSeconds)
            throw new InvalidOperationException("composed quotes leave too little checkout time");
        return expiry;
    }

    private static void ValidatePreparedIds(string? outgoing, string? ingress)
    {
        static bool Valid(string? value) => value is null || value.Length == 64
            && value == value.ToLowerInvariant() && value.All(Uri.IsHexDigit);
        if (!Valid(outgoing) || !Valid(ingress) || outgoing is not null && outgoing == ingress)
            throw new ArgumentException("prepared RFQ ids must be distinct lowercase 32-byte hex values");
    }

    private static ComposedSwapOptions Validated(ComposedSwapOptions? options)
    {
        var resolved = options ?? new ComposedSwapOptions();
        resolved.Validate();
        return resolved;
    }
}
