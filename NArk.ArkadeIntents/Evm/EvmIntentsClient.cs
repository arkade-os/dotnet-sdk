using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using NArk.ArkadeIntents.SolverRegistry;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.ArkadeIntents.Evm;

/// <summary>A verified Arkade-to-EVM quote waiting for L to be funded.</summary>
/// <param name="RfqId">Outgoing RFQ identity.</param>
/// <param name="Quote">Accepted solver quote.</param>
/// <param name="Secret">Client-owned route secret, already persisted in SDK intent storage.</param>
/// <param name="Contract">Locally reconstructed outgoing Arkade lock L.</param>
/// <param name="LockupAddress">Address of L.</param>
/// <param name="RefundContract">Wallet-owned contract supplying the refund key and destination.</param>
/// <param name="EvmValues">Exact tuple that must be proven on EVM before claiming.</param>
public sealed record PendingEvmSend(
    string RfqId,
    RfqQuote<EvmSendQuoteProfile> Quote,
    SwapLinkSecret Secret,
    NArk.Arkade.Contracts.VHTLCv2Contract Contract,
    string LockupAddress,
    ArkContract RefundContract,
    Erc20SwapValues EvmValues);

/// <summary>Negotiates and persists the outgoing Arkade-to-ERC20 leg of a composed route.</summary>
public sealed class EvmIntentsClient : IEvmOutgoingQuoteClient
{
    private readonly IClientTransport _transport;
    private readonly IContractService _contracts;
    private readonly IArkadeIntentStorage _intents;
    private readonly IEvmSwapRpc _evm;
    private readonly string? _emulatorPubkeyOverride;
    private readonly TimeProvider _time;
    private readonly ILogger<EvmIntentsClient>? _logger;

    /// <summary>Creates the client.</summary>
    /// <param name="transport">Live Arkade server facts.</param>
    /// <param name="contracts">Wallet contract derivation and import.</param>
    /// <param name="intents">SDK swap persistence. The host must protect this store at rest.</param>
    /// <param name="evm">Read-only EVM chain adapter used during quote validation.</param>
    /// <param name="options">Arkade emulator override shared by all corridors.</param>
    /// <param name="timeProvider">Clock for quote deadlines.</param>
    /// <param name="logger">Optional structured logger.</param>
    public EvmIntentsClient(
        IClientTransport transport,
        IContractService contracts,
        IArkadeIntentStorage intents,
        IEvmSwapRpc evm,
        IOptions<ArkadeIntentsOptions>? options = null,
        TimeProvider? timeProvider = null,
        ILogger<EvmIntentsClient>? logger = null)
    {
        _transport = transport;
        _contracts = contracts;
        _intents = intents;
        _evm = evm;
        _emulatorPubkeyOverride = options?.Value.EmulatorPubkeyOverride;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Creates the outgoing quote first, using a fresh route secret when none is supplied.</summary>
    /// <param name="walletId">Wallet that can recover an expired L.</param>
    /// <param name="amountSats">Exact Arkade input required at L.</param>
    /// <param name="evmClaimAddress">Merchant ERC20 destination.</param>
    /// <param name="policy">Dynamic chain, token, contract, proof, and timing policy.</param>
    /// <param name="rfqTransport">How to reach the selected outgoing solver.</param>
    /// <param name="secret">Route secret. Omit only when this is the first leg.</param>
    /// <param name="rfqId">Caller-reserved global RFQ identity, or null to generate one.</param>
    /// <param name="solverCard">Optional signed advertised terms.</param>
    /// <param name="refundContract">Optional existing wallet contract to avoid consuming another HD index.</param>
    /// <param name="cancellationToken">Cancels before the quote is exposed.</param>
    /// <returns>Verified L and the exact EVM tuple.</returns>
    public async Task<PendingEvmSend> CreateSendQuoteAsync(
        string walletId,
        long amountSats,
        string evmClaimAddress,
        EvmSendPolicy policy,
        IRfqTransport rfqTransport,
        SwapLinkSecret? secret = null,
        string? rfqId = null,
        SolverCard? solverCard = null,
        ArkContract? refundContract = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        secret ??= SwapLinkSecret.Generate();
        var serverInfo = await _transport.GetServerInfoAsync(cancellationToken);
        var payout = refundContract ?? await _contracts.DeriveContract(
            walletId, NextContractPurpose.Receive, cancellationToken: cancellationToken);
        var refundDescriptor = UserKeyOf(payout);
        var refundArkAddress = payout.GetArkAddress();
        var isMainnet = serverInfo.Network == Network.Main;
        var refundAddress = refundArkAddress.ToString(isMainnet);
        var refundPkScript = refundArkAddress.ScriptPubKey.ToBytes();

        var request = EvmSendProfile.Request(
            amountSats, secret.PaymentHash, evmClaimAddress, refundAddress,
            Convert.ToHexString(refundDescriptor.ToXOnlyPubKey().ToBytes()).ToLowerInvariant(),
            policy.TokenAddress, rfqId);
        if (solverCard is not null)
            SolverTerms.AssertInputWithinLimits(solverCard, request.Pair, amountSats);

        var quote = await rfqTransport.RequestEvmSendQuoteAsync(request, cancellationToken);
        if (solverCard is not null)
            SolverTerms.AssertFeeWithinAdvertised(solverCard, quote);
        if (await _evm.GetChainIdAsync(cancellationToken) != policy.ChainId)
            throw new EvmSendQuoteException("connected RPC has an unexpected chain id");
        var tip = await _evm.GetBlockNumberAsync(cancellationToken);
        var values = EvmSendQuoteValidator.Validate(
            request, quote, policy, _time.GetUtcNow().ToUnixTimeSeconds(), tip);
        var lockup = EvmArkadeLockupValidator.Validate(
            request, quote, policy, serverInfo, refundDescriptor, refundPkScript, _emulatorPubkeyOverride);
        var lockupAddress = lockup.Contract.GetArkAddress().ToString(isMainnet);

        await _contracts.ImportContract(
            walletId, lockup.Contract, ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"evm-send:{request.RfqId}" },
            cancellationToken: cancellationToken);
        await _intents.SaveArkadeSwapIntent(new ArkadeSwapIntent
        {
            Id = request.RfqId,
            WalletId = walletId,
            Type = ArkadeSwapIntentType.BtcToEvm,
            OfferAmount = Money.Satoshis(amountSats),
            WantAmount = Money.Zero,
            Status = ArkadeSwapIntentStatus.Pending,
            CreatedAt = _time.GetUtcNow(),
            SwapPkScript = lockup.Contract.GetScriptPubKey().ToHex(),
            SwapAddress = lockupAddress,
            FromAssetId = "btc",
            ToAssetId = $"eip155:{policy.ChainId}/erc20:{policy.TokenAddress}",
            PaymentHash = secret.PaymentHash,
            RefundLocktime = quote.RefundLocktime,
        }.WithEvmMetadata(new EvmSwapMetadata(
            Convert.ToHexString(secret.ExportPreimage()).ToLowerInvariant(),
            values.Amount.ToString(), values.TokenAddress, values.ClaimAddress, values.RefundAddress,
            values.TimeoutBlock.ToString(), policy.SwapContractAddress)).WithSolver(quote.SolverPubkey),
            cancellationToken);

        _logger?.LogInformation(
            "EVM send {RfqId} negotiated for {AmountSats} sats at Arkade lockup {Lockup}",
            request.RfqId, amountSats, lockupAddress);
        return new PendingEvmSend(request.RfqId, quote, secret, lockup.Contract, lockupAddress, payout, values);
    }

    private static OutputDescriptor UserKeyOf(ArkContract contract) => contract switch
    {
        ArkPaymentContract payment => payment.User,
        HashLockedArkPaymentContract hashLocked => hashLocked.User,
        ArkDelegateContract delegated => delegated.User,
        _ => throw new InvalidOperationException(
            $"expected a wallet payment contract for the EVM refund, got {contract.GetType().Name}"),
    };
}
