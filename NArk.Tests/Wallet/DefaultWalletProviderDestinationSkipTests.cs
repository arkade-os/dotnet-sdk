using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Wallet;

namespace NArk.Tests.Wallet;

/// <summary>
/// Tests the predicate that gates whether a wallet's sweep destination is used.
/// When a destination is flagged pending re-confirmation after an Arkade signer rotation,
/// <see cref="DefaultWalletProvider.ShouldUseDestination"/> must return <c>false</c> so
/// the provider falls back to self-output (null sweepDestination).
/// </summary>
[TestFixture]
public class DefaultWalletProviderDestinationSkipTests
{
    private static ArkWalletInfo MakeWallet(string? destination, Dictionary<string, string>? metadata)
        => new(
            Id: "test-wallet-id",
            Secret: null,
            Destination: destination,
            WalletType: WalletType.SingleKey,
            AccountDescriptor: "tr(0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798)",
            LastUsedIndex: 0,
            Metadata: metadata);

    [Test]
    public void ShouldUseDestination_false_when_pending_confirmation_flag_set()
    {
        var w = MakeWallet(
            destination: "ark1...",
            metadata: new Dictionary<string, string> { [DestinationSafety.PendingConfirmationMetadataKey] = "deadbeef" });

        Assert.That(DefaultWalletProvider.ShouldUseDestination(w), Is.False);
    }

    [Test]
    public void ShouldUseDestination_true_when_no_flag_and_destination_present()
    {
        var w = MakeWallet(destination: "ark1...", metadata: null);

        Assert.That(DefaultWalletProvider.ShouldUseDestination(w), Is.True);
    }

    [Test]
    public void ShouldUseDestination_false_when_destination_empty()
    {
        var w = MakeWallet(destination: null, metadata: null);

        Assert.That(DefaultWalletProvider.ShouldUseDestination(w), Is.False);
    }

    [Test]
    public void ShouldUseDestination_true_when_other_metadata_keys_present_but_not_flag()
    {
        var w = MakeWallet(
            destination: "ark1...",
            metadata: new Dictionary<string, string> { ["vtxo.lastFullPollAt"] = "2024-01-01T00:00:00Z" });

        Assert.That(DefaultWalletProvider.ShouldUseDestination(w), Is.True);
    }
}
