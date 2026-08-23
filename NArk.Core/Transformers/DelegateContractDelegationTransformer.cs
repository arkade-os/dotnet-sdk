using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NBitcoin.Secp256k1;

namespace NArk.Core.Transformers;

public class DelegateContractDelegationTransformer(
    IWalletProvider walletProvider,
    ILogger<DelegateContractDelegationTransformer>? logger = null) : IDelegationTransformer
{
    public async Task<bool> CanDelegate(string walletIdentifier, ArkContract contract, ECPubKey delegatePubkey)
    {
        if (contract is not ArkDelegateContract delegateContract)
            return false;

        // Verify the delegator's pubkey matches the contract's delegate key
        if (!ECXOnlyPubKeyComparer.Instance.Equals(delegateContract.Delegate.ToXOnlyPubKey(), delegatePubkey.ToXOnlyPubKey()))
        {
            logger?.LogDebug(
                "Delegator pubkey mismatch: contract={ContractDelegate}, delegator={DelegatorPubkey}",
                delegateContract.Delegate, Convert.ToHexString(delegatePubkey.ToBytes()).ToLowerInvariant());
            return false;
        }

        if (await walletProvider.GetAddressProviderAsync(walletIdentifier) is not { } addressProvider)
            return false;

        return await addressProvider.IsOurs(delegateContract.User);
    }

    public (ScriptBuilder intentScript, ScriptBuilder forfeitScript) GetDelegationScriptBuilders(ArkContract contract)
    {
        var delegateContract = (ArkDelegateContract)contract;
        return (delegateContract.CollaborativePath(), delegateContract.DelegatePath());
    }
}
