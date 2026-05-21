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
        // TEMP latency probe — kept light. Heavier inline benchmark was used
        // during root-cause analysis (proved the parser itself is sub-ms; the
        // wall-time slowdowns came from cache misses + competing CPU load),
        // removed once the cache fixes landed.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var infoMs = sw.ElapsedMilliseconds;

        var parseSw = System.Diagnostics.Stopwatch.StartNew();
        var parsedContract = ArkContractParser.Parse(contract.Type, contract.AdditionalData, serverInfo.Network);
        var parseMs = parseSw.ElapsedMilliseconds;

        if (parsedContract is null)
        {
            if (vtxo is not null)
                logger?.LogWarning("Could not parse contract for vtxo {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
            else
                logger?.LogWarning("Could not parse note contract");

            throw new UnableToSignUnknownContracts("Could not parse contract");
        }

        var xfSw = System.Diagnostics.Stopwatch.StartNew();
        var result = await RunTransformer(contract.WalletIdentifier, vtxo, parsedContract);
        logger?.LogTrace(
            "[coin-probe] GetCoin {Type}: GetServerInfo={InfoMs}ms parse={ParseMs}ms transformer={XfMs}ms",
            contract.Type, infoMs, parseMs, xfSw.ElapsedMilliseconds);
        return result;
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
        var contracts = await contractStorage.GetContracts(walletIds: [walletIdentifier], scripts: [vtxo.Script], cancellationToken: cancellationToken);

        if (contracts.FirstOrDefault() is not { } contract)
        {
            logger?.LogWarning("Could not find contract for vtxo {TxId}:{Index}", vtxo.TransactionId,
                vtxo.TransactionOutputIndex);
            throw new UnableToSignUnknownContracts("Could not find contract for vtxo");
        }

        return await GetCoin(contract, vtxo, cancellationToken);
    }
}