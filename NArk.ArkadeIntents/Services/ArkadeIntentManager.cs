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
using NBitcoin;
using NBitcoin.Scripting;
using NArk.ArkadeIntents.Lightning;

namespace NArk.ArkadeIntents.Services;

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
/// The Arkade swap creation entry point: builds the covenant offer from the wallet's own receive
/// address + signing key, funds it (attaching the offer packet so the solver can fill it), and
/// persists the resulting <see cref="ArkadeSwapIntent"/> as pending — after which the storage-backed
/// <see cref="ArkadeSwapIntentMonitoringService"/> drives it to a terminal status from the covenant VTXO.
/// </summary>
public sealed class ArkadeIntentManager
{
    private readonly IClientTransport _transport;
    private readonly IEmulatorProvider _emulator;
    private readonly IContractService _contractService;
    private readonly IWalletProvider _walletProvider;
    private readonly ISpendingService _spendingService;
    private readonly IArkadeIntentStorage _intentStorage;
    private readonly IVtxoStorage _vtxoStorage;

    public ArkadeIntentManager(
        IClientTransport transport,
        IEmulatorProvider emulator,
        IContractService contractService,
        IWalletProvider walletProvider,
        ISpendingService spendingService,
        IArkadeIntentStorage intentStorage,
        IVtxoStorage vtxoStorage)
    {
        _transport = transport;
        _emulator = emulator;
        _contractService = contractService;
        _walletProvider = walletProvider;
        _spendingService = spendingService;
        _intentStorage = intentStorage;
        _vtxoStorage = vtxoStorage;
    }

    /// <summary>
    /// Create and fund an Arkade swap: derive a fresh receive address (payout target) and signing key
    /// (cancel signer), build the offer against the current server + emulator keys, deposit to the
    /// swap address with the offer packet attached, and store the intent as pending.
    /// </summary>
    public async Task<ArkadeSwapIntent> CreateSwap(CreateSwapRequest request, CancellationToken cancellationToken = default)
    {
        var serverInfo = await _transport.GetServerInfoAsync(cancellationToken);
        var emulatorInfo = await _emulator.GetInfoAsync(cancellationToken);
        // Parsed, not sliced. This corridor quotes no counterparty address to check the derivation
        // against, so a key mangled here reaches the covenant unchallenged and the deposit lands
        // under a co-signer that does not exist.
        // The network's pinned co-signer, not whatever the emulator says about itself — the offer
        // embeds this key, and an offer embedding the wrong one is filled by nobody.
        if (!EmulatorPubKeys.AgreesWithPin(serverInfo.NetworkName, emulatorInfo.SignerPubkey))
        {
            throw new InvalidOperationException(
                $"The emulator at this deployment reports {emulatorInfo.SignerPubkey}, but "
                + $"'{serverInfo.NetworkName}' is pinned to a different co-signer. An offer embedding "
                + "the reported key is one no solver on this network can fill, so this refuses rather "
                + "than funding a deposit nobody will take.");
        }

        var emulatorPubkey = LightningCorridor.NormalizeToXOnly(
            Convert.FromHexString(EmulatorPubKeys.DefaultFor(serverInfo.NetworkName))).ToBytes();

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
            offerAsset: isBtcToAsset ? null : request.Asset);

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
            OfferHex = created.OfferHex,
            MakerDescriptor = makerSigner.ToString(),
            FromAssetId = isBtcToAsset ? "btc" : request.Asset.ToString(),
            ToAssetId = isBtcToAsset ? request.Asset.ToString() : "btc",
        };
        await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);
        return intent;
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
        if (intent.MakerDescriptor is not { } makerDescriptorStr)
            throw new InvalidOperationException($"Swap '{swapId}' has no maker descriptor to sign the cancel path.");

        // Move out of Pending BEFORE spending so the monitor can't read the cancel-spend as a fill.
        intent.Status = ArkadeSwapIntentStatus.Cancelling;
        await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        try
        {
            var serverInfo = await _transport.GetServerInfoAsync(cancellationToken);
            var offer = OfferCodec.Decode(Convert.FromHexString(intent.OfferHex));
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
