using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Core.Extensions;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Core.Wallet;

/// <summary>
/// Factory for creating wallet info records from secrets (nsec or mnemonic),
/// and watch-only wallets from an account descriptor.
/// </summary>
public static class WalletFactory
{
    public static Task<ArkWalletInfo> CreateWallet(
        string walletSecret,
        string? destination,
        ArkServerInfo serverInfo,
        CancellationToken cancellationToken = default)
    {
        if (destination is not null)
        {
            ValidateDestination(destination, serverInfo);
        }

        if (walletSecret.StartsWith("nsec", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(CreateNsecWallet(walletSecret, destination));
        }

        return Task.FromResult(CreateHdWallet(walletSecret, destination, serverInfo));
    }

    /// <summary>
    /// Creates a watch-only wallet from an account descriptor. The wallet
    /// can derive addresses and observe VTXOs but holds no signing material —
    /// <see cref="IWalletProvider.GetSignerAsync"/> will return <c>null</c>
    /// for it, and signing-dependent operations (batch participation,
    /// unilateral exits) will throw with a descriptive error.
    /// </summary>
    /// <param name="accountDescriptor">
    /// The account descriptor to observe. May be a bare <c>tr(pubkey)</c> for
    /// single-key style, or a <c>tr([fingerprint/path]xpub/0/*)</c> for
    /// hierarchical-deterministic style; the wallet's address provider is
    /// picked accordingly.
    /// </param>
    /// <param name="destination">
    /// Optional sweep destination address (must be addressed to the same
    /// Arkade server as <paramref name="serverInfo"/>).
    /// </param>
    /// <param name="serverInfo">The Arkade server the wallet is bound to.</param>
    /// <param name="metadata">Optional initial metadata to attach to the wallet.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<ArkWalletInfo> CreateWatchOnlyWallet(
        string accountDescriptor,
        string? destination,
        ArkServerInfo serverInfo,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountDescriptor);
        if (destination is not null)
        {
            ValidateDestination(destination, serverInfo);
        }

        return Task.FromResult(new ArkWalletInfo(
            Id: accountDescriptor,
            Secret: null,
            Destination: destination,
            WalletType: WalletType.WatchOnly,
            AccountDescriptor: accountDescriptor,
            LastUsedIndex: 0,
            Metadata: metadata
        ));
    }

    public static void ValidateDestination(string destination, ArkServerInfo serverInfo)
    {
        var addr = ArkAddress.Parse(destination);
        var serverKey = serverInfo.SignerKey.Extract().XOnlyPubKey;
        if (!serverKey.ToBytes().SequenceEqual(addr.ServerKey.ToBytes()))
        {
            throw new InvalidOperationException("Invalid destination server key.");
        }
    }

    private static ArkWalletInfo CreateNsecWallet(string nsec, string? destination)
    {
        var outputDescriptor = GetOutputDescriptorFromNsec(nsec);
        return new ArkWalletInfo(
            Id: outputDescriptor,
            Secret: nsec,
            Destination: destination,
            WalletType: WalletType.SingleKey,
            AccountDescriptor: outputDescriptor,
            LastUsedIndex: 0
        );
    }

    public static string GetOutputDescriptorFromNsec(string nsec)
    {
        var privKey = DecodeNsecPrivKey(nsec);
        var outputDescriptor = $"tr({privKey.CreatePubKey().ToBytes().ToHexStringLower()})";
        return outputDescriptor;
    }

    public static string[] GetAlternateWalletIdsFromNsec(string nsec)
    {
        var privKey = DecodeNsecPrivKey(nsec);
        var compressed = privKey.CreatePubKey().ToBytes().ToHexStringLower();
        var xonly = privKey.CreateXOnlyPubKey().ToBytes().ToHexStringLower();
        return [compressed, xonly, $"tr({xonly})"];
    }

    private static ECPrivKey DecodeNsecPrivKey(string nsec)
    {
        var encoder = Bech32Encoder.ExtractEncoderFromString(nsec);
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        var keyData = encoder.DecodeDataRaw(nsec, out _);
        return ECPrivKey.Create(keyData);
    }

    private static ArkWalletInfo CreateHdWallet(
        string mnemonic,
        string? destination,
        ArkServerInfo serverInfo)
    {
        var mnemonicObj = new Mnemonic(mnemonic);
        var extKey = mnemonicObj.DeriveExtKey();
        var fingerprint = extKey.GetPublicKey().GetHDFingerPrint();
        var coinType = serverInfo.Network.ChainName == ChainName.Mainnet ? "0" : "1";

        var accountKeyPath = new KeyPath($"m/86'/{coinType}'/0'");
        var accountXpriv = extKey.Derive(accountKeyPath);
        var accountXpub = accountXpriv.Neuter().GetWif(serverInfo.Network).ToWif();
        var accountDescriptor = $"tr([{fingerprint}/86'/{coinType}'/0']{accountXpub}/0/*)";

        return new ArkWalletInfo(
            Id: accountDescriptor,
            Secret: mnemonic,
            Destination: destination,
            WalletType: WalletType.HD,
            AccountDescriptor: accountDescriptor,
            LastUsedIndex: 0
        );
    }
}
