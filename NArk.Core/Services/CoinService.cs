using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using NArk.Core.Transformers;
using NArk.Core.Transport;

namespace NArk.Core.Services;

public class CoinService(IClientTransport clientTransport, IContractStorage contractStorage, IEnumerable<IContractTransformer> transformers, ILogger<CoinService>? logger = null) : ICoinService
{
    public async Task<ArkCoin> GetCoin(ArkContractEntity contract, ArkVtxo vtxo, CancellationToken cancellationToken = default)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var parsedContract = ArkContractParser.Parse(contract.Type, contract.AdditionalData, serverInfo.Network);
        if (parsedContract is null)
        {
            if (vtxo is not null)
                logger?.LogWarning("Could not parse contract for vtxo {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
            else
                logger?.LogWarning("Could not parse note contract");

            throw new UnableToSignUnknownContracts("Could not parse contract");
        }

        return await RunTransformer(contract.WalletIdentifier, vtxo, parsedContract);
    }

    private async Task<ArkCoin> RunTransformer(string walletIdentifier, ArkVtxo vtxo, ArkContract contract)
    {
        foreach (var transformer in transformers)
        {
            if (await transformer.CanTransform(walletIdentifier, contract, vtxo))
                return await transformer.Transform(walletIdentifier, contract, vtxo);
        }

        throw new AdditionalInformationRequiredException("Unknown contract, please inject proper IContractTransformer");
    }
    public async Task<ArkCoin> GetCoin(ArkVtxo vtxo, string walletIdentifier, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Getting PSBT signer for vtxo by script {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
        var contracts = await contractStorage.LoadContractsByScripts([vtxo.Script], [walletIdentifier], cancellationToken);

        if (contracts.FirstOrDefault() is not { } contract)
        {
            logger?.LogWarning("Could not find contract for vtxo {TxId}:{Index}", vtxo.TransactionId,
                vtxo.TransactionOutputIndex);
            throw new UnableToSignUnknownContracts("Could not find contract for vtxo");
        }

        return await GetCoin(contract, vtxo, cancellationToken);
    }
}