using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Wallet;

public class HierarchicalDeterministicAddressProvider(
    IClientTransport transport,
    ISafetyService safetyService,
    IWalletStorage walletStorage,
    IContractStorage contractStorage,
    ArkWalletInfo wallet,
    Network network,
    ArkAddress? sweepDestination)
    : IArkadeAddressProvider
{
    private int _lastUsedIndex = wallet.LastUsedIndex;

    public async Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        // Guard: the wallet must have an account descriptor. The pre-existing
        // `OutputDescriptor.Parse(wallet.AccountDescriptor, network)` line on
        // entry was discarding its result — a ~500-1000ms no-op that fired on
        // every IsOurs call (and IsOurs is called multiple times per VTXO
        // settlement via VHTLCContractTransformer / PaymentContractTransformer).
        // Replaced with a null-check that keeps the validation but skips the
        // wasted parse.
        if (string.IsNullOrEmpty(wallet.AccountDescriptor))
            throw new Exception("Malformed HD Wallet");
        var index = descriptor.Extract().DerivationPath?.Indexes.Last().ToString();
        if (index is null)
        {
            return false;
        }
        var expected = GetDescriptorFromIndex(network, wallet.AccountDescriptor, Convert.ToInt32(index));
        return expected.Equals(descriptor);
    }

    public async Task<OutputDescriptor> GetNextSigningDescriptor(CancellationToken cancellationToken = default)
    {
        await using var @lock = await safetyService.LockKeyAsync($"wallet::{wallet.Id}", cancellationToken);

        var freshWallet = await walletStorage.GetWalletById(wallet.Id, cancellationToken)
            ?? throw new Exception("Wallet not found");

        var nextIndex = freshWallet.LastUsedIndex;
        var descriptor = GetDescriptorFromIndex(
            network,
            freshWallet.AccountDescriptor ?? throw new Exception("Malformed HD Wallet"),
            nextIndex
        );

        await walletStorage.UpdateLastUsedIndex(wallet.Id, nextIndex + 1, cancellationToken);

        _lastUsedIndex = nextIndex + 1;

        return descriptor;
    }

    /// <summary>
    /// Resolves the wildcard <paramref name="descriptor"/> at the given <paramref name="index"/>.
    /// This is the canonical derivation entry point — both the runtime
    /// <see cref="GetNextSigningDescriptor"/> path and any external recovery
    /// scanners (e.g. <c>HdWalletRecoveryService</c>) MUST go through here so
    /// they always agree on which script corresponds to a given HD index.
    /// </summary>
    internal static OutputDescriptor GetDescriptorFromIndex(Network network, string descriptor, int index)
    {
        // Route through the cached parser — `OutputDescriptor.Parse` is observed
        // at ~500-1000ms per call and this path runs on every IsOurs check.
        return NArk.Abstractions.Extensions.KeyExtensions.ParseOutputDescriptor(descriptor.Replace("/*", $"/{index}"), network);
    }

    public async Task<(ArkContract contract, ArkContractEntity entity)> GetNextContract(
        NextContractPurpose purpose,
        ContractActivityState activityState,
        ArkContract[]? inputContracts = null,
        CancellationToken cancellationToken = default)
    {
        var info = await transport.GetServerInfoAsync(cancellationToken);
        ArkContract? result = null;

        if (purpose == NextContractPurpose.Boarding)
        {
            result = new ArkBoardingContract(
                info.SignerKey,
                info.BoardingExit,
                await GetNextSigningDescriptor(cancellationToken)
            );
        }
        else if (purpose == NextContractPurpose.SendToSelf && sweepDestination is not null)
        {
            result = new UnknownArkContract(sweepDestination, info.SignerKey, info.Network.ChainName == ChainName.Mainnet);
            activityState = ContractActivityState.Inactive;
        }
        else if (purpose == NextContractPurpose.SendToSelf)
        {
            var recycledDescriptor = inputContracts is not null
                ? await TryGetRecyclableDescriptor(inputContracts, info.SignerKey, cancellationToken)
                : null;

            if (recycledDescriptor is not null)
            {
                result = new ArkPaymentContract(info.SignerKey, info.UnilateralExit, recycledDescriptor);
                activityState = ContractActivityState.Inactive;
            }
            else
            {
                activityState = ContractActivityState.AwaitingFundsBeforeDeactivate;
            }
        }

        result ??= new ArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            await GetNextSigningDescriptor(cancellationToken)
        );

        var entity = result.ToEntity(wallet.Id, info.SignerKey, null, activityState);
        if (result is UnknownArkContract)
            entity = entity with { Metadata = new Dictionary<string, string> { ["Source"] = "sweep-destination" } };
        return (result, entity);
    }

    private async Task<OutputDescriptor?> TryGetRecyclableDescriptor(
        ArkContract[] inputs, OutputDescriptor serverKey, CancellationToken cancellationToken)
    {
        var inputScripts = inputs
            .Select(c => c.GetScriptPubKey().ToHex())
            .Distinct()
            .ToArray();
        var storedContracts = await contractStorage.GetContracts(
            walletIds: [wallet.Id],
            scripts: inputScripts,
            cancellationToken: cancellationToken);
        var invoiceScripts = storedContracts
            .Where(c => c.Metadata?.TryGetValue("Source", out var src) == true
                        && src.StartsWith("invoice:", StringComparison.Ordinal))
            .Select(c => c.Script)
            .ToHashSet();

        foreach (var payment in inputs.OfType<ArkPaymentContract>())
        {
            if (invoiceScripts.Contains(payment.GetScriptPubKey().ToHex()))
                continue;

            if (await IsOurs(payment.User, cancellationToken))
            {
                return payment.User;
            }
        }

        foreach (var htlc in inputs.OfType<VHTLCContract>())
        {
            if (invoiceScripts.Contains(htlc.GetScriptPubKey().ToHex()))
                continue;

            if (await IsOurs(htlc.Receiver, cancellationToken))
            {
                return htlc.Receiver;
            }
            if (await IsOurs(htlc.Sender, cancellationToken))
            {
                return htlc.Sender;
            }
        }

        return null;
    }
}
