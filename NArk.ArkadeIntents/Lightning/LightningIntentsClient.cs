using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Abstractions;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.SolverRegistry;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core;
using NBitcoin.Scripting;
using NBitcoin;
using System.Security.Cryptography;

namespace NArk.ArkadeIntents.Lightning;

/// <summary>
/// Both Lightning corridors: paying a BOLT11 out of an Arkade balance, and being paid over
/// Lightning into one.
/// </summary>
/// <remarks>
/// <para>
/// One class in three files, because the two directions are one mechanism seen from either end.
/// The covenant, the leaves, the checks and the bookkeeping are the same; what differs is who
/// occupies which role, and that difference is worth a file name rather than a second type.
/// </para>
/// <para>
/// They were two types, and the cost showed up twice in one day: nine of their eleven dependencies
/// were identical, so every parameter added to one had to be added to the other, and both times a
/// hand-written construction site somewhere silently took the new argument in an old position.
/// One constructor cannot drift from itself.
/// </para>
/// <para>
/// See <c>LightningIntentsClient.Send.cs</c> and <c>LightningIntentsClient.Receive.cs</c> for each direction.
/// </para>
/// </remarks>
public sealed partial class LightningIntentsClient
{
    private readonly IClientTransport _transport;
    private readonly IContractService _contractService;
    private readonly ISpendingService _spendingService;
    private readonly IArkadeIntentStorage _intentStorage;
    private readonly IContractStorage _contractStorage;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IWalletProvider _walletProvider;

    /// <summary>Supplies AES-GCM for the receive corridor's claim packet.</summary>
    private readonly IAesGcmCipher _cipher;

    /// <summary>Answers when a send corridor's refund locktime has actually matured.</summary>
    private readonly IBitcoinBlockchain? _blockchain;

    /// <summary>A co-signer supplied in place of the network's pin, or <c>null</c>.</summary>
    private readonly string? _emulatorPubkeyOverride;

    /// <summary>The ceiling on what a receive quote may bill the payer, or <c>null</c> for none.</summary>
    private readonly long? _maxPayAmountSats;

    private readonly TimeProvider _time;
    private readonly ILogger<LightningIntentsClient>? _logger;

    /// <summary>Creates the client.</summary>
    /// <param name="transport">The Arkade server connection.</param>
    /// <param name="contractService">Derives and imports contracts.</param>
    /// <param name="spendingService">Builds and submits the spends.</param>
    /// <param name="intentStorage">Where swaps are recorded.</param>
    /// <param name="contractStorage">Where a funded lockup is read back from.</param>
    /// <param name="vtxoStorage">The chain view a lockup's outputs are found in.</param>
    /// <param name="walletProvider">Signs, and anchors the derived preimage.</param>
    /// <param name="cipher">
    /// AES-GCM for the claim packet. Defaults to the platform's, which is right everywhere but a
    /// browser — see <see cref="IAesGcmCipher"/>.
    /// </param>
    /// <param name="blockchain">
    /// Optional. Supplied, a refund matures on the chain's own median time past; absent, it waits
    /// out the worst-case lag instead.
    /// </param>
    /// <param name="options">
    /// Corridor settings: the covenant co-signer override, and the ceiling on what a receive quote
    /// may bill the payer.
    /// </param>
    /// <param name="time">Clock for the deadline comparisons; defaults to the system clock.</param>
    /// <param name="logger">Optional logger.</param>
    public LightningIntentsClient(
        IClientTransport transport,
        IContractService contractService,
        ISpendingService spendingService,
        IArkadeIntentStorage intentStorage,
        IContractStorage contractStorage,
        IVtxoStorage vtxoStorage,
        IWalletProvider walletProvider,
        IAesGcmCipher? cipher = null,
        IBitcoinBlockchain? blockchain = null,
        IOptions<ArkadeIntentsOptions>? options = null,
        TimeProvider? time = null,
        ILogger<LightningIntentsClient>? logger = null)
    {
        _transport = transport;
        _contractService = contractService;
        _spendingService = spendingService;
        _intentStorage = intentStorage;
        _contractStorage = contractStorage;
        _vtxoStorage = vtxoStorage;
        _walletProvider = walletProvider;
        _cipher = cipher ?? new AesGcmCipher();
        _blockchain = blockchain;
        var resolved = options?.Value ?? new ArkadeIntentsOptions();
        _emulatorPubkeyOverride = resolved.EmulatorPubkeyOverride;
        _maxPayAmountSats = resolved.MaxPayAmountSats;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// The wallet's own key out of a derived receive contract, whatever shape it came back as.
    /// </summary>
    /// <remarks>
    /// A wallet with payment tracking on derives <see cref="HashLockedArkPaymentContract"/> for
    /// receiving, which carries the same user key but does not inherit
    /// <see cref="ArkPaymentContract"/> — so matching only the plain shape refused a perfectly good
    /// address and failed the swap before it started. Both are spendable by this wallet, which is
    /// the only property this key is being read for.
    /// </remarks>
    private static OutputDescriptor UserKeyOf(ArkContract contract, string role) => contract switch
    {
        ArkPaymentContract payment => payment.User,
        HashLockedArkPaymentContract hashLocked => hashLocked.User,
        _ => throw new InvalidOperationException(
            $"expected a payment contract to take the {role} key from, got {contract.GetType().Name}"),
    };
}
