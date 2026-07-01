using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Enums;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Wallet;

public class SingleKeyAddressProvider(
    IClientTransport transport,
    ArkWalletInfo wallet,
    Network network,
    ArkAddress? sweepingAddress,
    ILogger? logger = null
) : IArkadeAddressProvider
{
    public OutputDescriptor Descriptor { get; } = OutputDescriptor.Parse(wallet.AccountDescriptor!, network);

    /// <inheritdoc/>>
    public Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var theirs = descriptor.Extract().XOnlyPubKey.ToBytes();
        var ours = Descriptor.Extract().XOnlyPubKey.ToBytes();
        var match = theirs.SequenceEqual(ours);

        if (!match)
        {
            logger?.LogDebug(
                "IsOurs: walletId={WalletId}, match=false, " +
                "theirXOnly={TheirXOnly}, ourXOnly={OurXOnly}",
                wallet.Id,
                Convert.ToHexString(theirs).ToLowerInvariant(),
                Convert.ToHexString(ours).ToLowerInvariant());
        }

        return Task.FromResult(match);
    }

    /// <inheritdoc/>>
    public Task<OutputDescriptor> GetNextSigningDescriptor(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Descriptor);
    }
    
    /// <inheritdoc/>>
    public async Task<(ArkContract contract, ArkContractEntity entity)> GetNextContract(
        NextContractPurpose purpose,
        ContractActivityState activityState,
        ArkContract[]? inputContracts = null,
        CancellationToken cancellationToken = default)
    {
        var info = await transport.GetServerInfoAsync(cancellationToken);
        var signingDescriptor = await GetNextSigningDescriptor(cancellationToken);
        
        (ArkContract contract, ContractActivityState state) = purpose switch
        {
            NextContractPurpose.Boarding => (
                (ArkContract)new ArkBoardingContract(info.SignerKey, info.BoardingExit, signingDescriptor),
                activityState),

            // Collaborative-exit sweep target: a fixed external address, never tracked.
            NextContractPurpose.SendToSelf when sweepingAddress is not null => (
                new UnknownArkContract(sweepingAddress, info.SignerKey, info.Network.ChainName == ChainName.Mainnet),
                ContractActivityState.Inactive),

            NextContractPurpose.SendToSelf => (
                new ArkPaymentContract(info.SignerKey, info.UnilateralExit, signingDescriptor),
                ContractActivityState.Active),

            _ => (
                new HashLockedArkPaymentContract(
                    info.SignerKey,
                    info.UnilateralExit,
                    signingDescriptor,
                    RandomUtils.GetBytes(32),
                    HashLockTypeOption.Hash160
                ), activityState)
        };

        var entity = contract.ToEntity(wallet.Id, info.SignerKey, null, state);
        if (contract is UnknownArkContract)
            entity = entity with { Metadata = new Dictionary<string, string> { ["Source"] = "sweep-destination" } };
        return (contract, entity);
    }
}
