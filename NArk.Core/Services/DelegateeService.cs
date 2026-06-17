using Microsoft.Extensions.Options;
using NArk.Abstractions.Services;
using NArk.Abstractions.Wallets;
using NArk.Core.Models.Options;

namespace NArk.Core.Services;

/// <summary>
/// Server-side handler for the Arkade VTXO-refresh delegation service (the delegatee). Implements the
/// behaviour behind <c>fulmine.v1.DelegatorService</c>: advertises the delegator's identity and fee,
/// and (from Task 6) accepts delegations, co-signs them, and writes them into the intent pipeline for
/// refresh before expiry.
/// </summary>
public class DelegateeService(
    IWalletProvider walletProvider,
    IOptions<DelegatorOptions> options)
{
    private readonly DelegatorOptions _options = options.Value;

    /// <summary>
    /// Returns the delegator's public key (hex), advertised fee, and fee address. The pubkey is what
    /// clients embed in the delegate leaf of their delegate contract.
    /// </summary>
    public async Task<DelegatorInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var signer = await walletProvider.GetSignerAsync(_options.WalletId, cancellationToken)
            ?? throw new InvalidOperationException($"No signer for delegator wallet {_options.WalletId}");
        var pubkey = await signer.GetPubKey(_options.DelegateDescriptor, cancellationToken);
        var pubkeyHex = Convert.ToHexString(pubkey.ToBytes()).ToLowerInvariant();
        return new DelegatorInfo(pubkeyHex, _options.Fee, _options.DelegatorAddress);
    }
}
