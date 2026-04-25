using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NBitcoin.Scripting;

namespace NArk.Core.Recovery;

/// <summary>
/// No-op discovery provider used as a placeholder when an optional dependency
/// (e.g. <c>IBoardingUtxoProvider</c>) isn't registered. Always returns
/// <see cref="DiscoveryResult.NotFound"/> so it's invisible to the gap-limit
/// orchestrator while still satisfying DI.
/// </summary>
internal sealed class NullContractDiscoveryProvider : IContractDiscoveryProvider
{
    public static readonly NullContractDiscoveryProvider Instance = new();

    private NullContractDiscoveryProvider() { }

    public string Name => "null";

    public Task<DiscoveryResult> DiscoverAsync(
        ArkWalletInfo wallet,
        OutputDescriptor userDescriptor,
        int index,
        CancellationToken cancellationToken = default)
        => Task.FromResult(DiscoveryResult.NotFound);
}
