using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Swaps.Helpers;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;
using OutputDescriptorHelpers = NArk.Swaps.Helpers.OutputDescriptorHelpers;

namespace NArk.Tests.End2End.Wallets;

public class SimpleSeedWallet : IArkadeWalletSigner, IArkadeAddressProvider
{
    private readonly string _identifier;
    private readonly string _descriptor;
    private readonly string _mnemonic;
    private int _lastIndex;
    private readonly IClientTransport _clientTransport;

    private SimpleSeedWallet(string identifier, string descriptor, string mnemonic, int lastIndex, IClientTransport clientTransport)
    {
        _identifier = identifier;
        _descriptor = descriptor;
        _mnemonic = mnemonic;
        _lastIndex = lastIndex;
        _clientTransport = clientTransport;
    }

    public static async Task<SimpleSeedWallet> CreateNewWallet(IClientTransport clientTransport, CancellationToken cancellationToken = default)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
        var extKey = mnemonic.DeriveExtKey();
        var fingerprint = extKey.GetPublicKey().GetHDFingerPrint();
        var coinType = serverInfo.Network.ChainName == ChainName.Mainnet ? "0" : "1";

        // BIP-86 Taproot: m/86'/coin'/0'
        var accountKeyPath = new KeyPath($"m/86'/{coinType}'/0'");
        var accountXpriv = extKey.Derive(accountKeyPath);
        var accountXpub = accountXpriv.Neuter().GetWif(serverInfo.Network).ToWif();

        // Descriptor format: tr([fingerprint/86'/coin'/0']xpub/0/*)
        var descriptor = $"tr([{fingerprint}/86'/{coinType}'/0']{accountXpub}/0/*)";

        return new SimpleSeedWallet(fingerprint.ToString(), descriptor, mnemonic.ToString(), 0, clientTransport);
    }

    public async Task<string> GetWalletFingerprint(CancellationToken cancellationToken = default)
    {
        return _identifier;
    }

    private Task<ECPrivKey> DerivePrivateKey(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var info = OutputDescriptorHelpers.Extract(descriptor);
        var extKey = new Mnemonic(_mnemonic).DeriveExtKey();
        return Task.FromResult(ECPrivKey.Create(extKey.Derive(info.FullPath!).PrivateKey.ToBytes()));
    }


    public async Task<MusigPartialSignature> SignMusig(OutputDescriptor descriptor, MusigContext context, MusigPrivNonce nonce,
        CancellationToken cancellationToken = default)
    {
        var privKey = await DerivePrivateKey(descriptor, cancellationToken);
        return context.Sign(privKey, nonce);
    }

    public async Task<(ECXOnlyPubKey, SecpSchnorrSignature)> Sign(OutputDescriptor descriptor, uint256 hash, CancellationToken cancellationToken = default)
    {
        var privKey = await DerivePrivateKey(descriptor, cancellationToken);

        return (privKey.CreateXOnlyPubKey(), privKey.SignBIP340(hash.ToBytes()));
    }

    public async Task<MusigPrivNonce> GenerateNonces(OutputDescriptor descriptor, MusigContext context, CancellationToken cancellationToken = default)
    {
        return context.GenerateNonce(await DerivePrivateKey(descriptor, cancellationToken));
    }

    public async Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {

        var network = (await _clientTransport.GetServerInfoAsync(cancellationToken)).Network;
        var index = descriptor.Extract().DerivationPath?.Indexes.Last().ToString();
        if (index is null)
        {
            return false;
        }

        var expected = OutputDescriptor.Parse(_descriptor.Replace("/*", $"/{index}"), network);
        return expected.Equals(descriptor);
    }

    static OutputDescriptor GetDescriptorFromIndex(Network network, string descriptor, int index)
    {
        return OutputDescriptor.Parse(descriptor.Replace("/*", $"/{index}"), network);
    }

    public async Task<OutputDescriptor> GetNextSigningDescriptor(CancellationToken cancellationToken = default)
    {
        var network = (await _clientTransport.GetServerInfoAsync(cancellationToken)).Network;
        return GetDescriptorFromIndex(network, _descriptor, _lastIndex++);
    }

    public async Task<(ArkContract contract, ArkContractEntity entity)> GetNextContract(NextContractPurpose purpose, ContractActivityState activityState,
        CancellationToken cancellationToken = default)
    {

        var descriptor = await GetNextSigningDescriptor(cancellationToken);
        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        var contract = new ArkPaymentContract(serverInfo.SignerKey, serverInfo.UnilateralExit, descriptor);
        return (contract, contract.ToEntity(_identifier, null, null, activityState));
    }
}