using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Services;

/// <summary>
/// Detects expired boarding/unrolled UTXOs and sweeps them via the unilateral exit path
/// to a fresh boarding address. This is a safety net for UTXOs whose CSV timeout has
/// expired before they were consumed in a batch.
/// </summary>
public class OnchainSweepService(
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IBitcoinBlockchain blockchain,
    IContractService contractService,
    IWalletProvider walletProvider,
    IClientTransport transport,
    IOnchainSweepHandler? sweepHandler = null,
    ILogger<OnchainSweepService>? logger = null)
{
    /// <summary>
    /// Scans for expired unrolled/boarding UTXOs and sweeps them.
    /// Call on-demand; caller may wrap in a timer for periodic execution.
    /// </summary>
    public async Task SweepExpiredUtxosAsync(CancellationToken cancellationToken)
    {
        var chainTime = await blockchain.GetChainTime(cancellationToken);
        logger?.LogDebug("Checking for expired unrolled UTXOs at height {Height}, time {Time}",
            chainTime.Height, chainTime.Timestamp);

        // Get all unspent VTXOs (includeSpent: false is the default)
        var allVtxos = await vtxoStorage.GetVtxos(cancellationToken: cancellationToken);

        // Filter: unrolled and expired
        var expiredUnrolled = allVtxos
            .Where(v => v.Unrolled && !v.IsSpent() && v.IsRecoverable(chainTime))
            .ToList();

        if (expiredUnrolled.Count == 0)
        {
            logger?.LogDebug("No expired unrolled UTXOs found");
            return;
        }

        logger?.LogInformation("Found {Count} expired unrolled UTXOs to sweep", expiredUnrolled.Count);

        // Look up contracts for the expired VTXOs
        var scripts = expiredUnrolled.Select(v => v.Script).Distinct().ToArray();
        var contracts = await contractStorage.GetContracts(
            scripts: scripts,
            contractTypes: [ArkBoardingContract.ContractType],
            cancellationToken: cancellationToken);

        var contractByScript = contracts.ToDictionary(c => c.Script);

        foreach (var vtxo in expiredUnrolled)
        {
            if (!contractByScript.TryGetValue(vtxo.Script, out var contractEntity))
            {
                logger?.LogWarning(
                    "No boarding contract found for expired UTXO {Outpoint}, skipping",
                    vtxo.OutPoint);
                continue;
            }

            try
            {
                await SweepSingleUtxoAsync(vtxo, contractEntity, cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex,
                    "Failed to sweep expired UTXO {Outpoint}", vtxo.OutPoint);
            }
        }
    }

    private async Task SweepSingleUtxoAsync(
        ArkVtxo vtxo,
        ArkContractEntity contractEntity,
        CancellationToken cancellationToken)
    {
        // If a custom handler is registered, give it first shot
        if (sweepHandler is not null)
        {
            var handled = await sweepHandler.HandleExpiredUtxoAsync(
                contractEntity.WalletIdentifier, vtxo, contractEntity, cancellationToken);

            if (handled)
            {
                logger?.LogInformation(
                    "Custom sweep handler processed expired UTXO {Outpoint}",
                    vtxo.OutPoint);
                return;
            }
        }

        logger?.LogInformation(
            "Sweeping expired UTXO {Outpoint} ({Amount} sats) to fresh boarding address",
            vtxo.OutPoint, vtxo.Amount);

        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);

        var contract = ArkContractParser.Parse(contractEntity.Type, contractEntity.AdditionalData, serverInfo.Network)
            ?? throw new InvalidOperationException(
                $"Failed to parse contract for VTXO {vtxo.OutPoint}");

        if (contract is not ArkBoardingContract boardingContract)
            throw new InvalidOperationException(
                $"VTXO {vtxo.OutPoint} has contract type '{contractEntity.Type}'; only boarding contracts are swept via {nameof(OnchainSweepService)}");

        var unilateralTapScript = (UnilateralPathArkTapScript)boardingContract.UnilateralPath();
        var tapScript = unilateralTapScript.Build();
        var spendInfo = boardingContract.GetTaprootSpendInfo();
        var controlBlock = spendInfo.GetControlBlock(tapScript);

        var vtxoTxOut = new TxOut(Money.Satoshis(vtxo.Amount), Script.FromHex(vtxo.Script));

        var freshContract = await contractService.DeriveContract(
            contractEntity.WalletIdentifier,
            NextContractPurpose.Boarding,
            cancellationToken: cancellationToken);

        if (freshContract is not ArkBoardingContract freshBoarding)
            throw new InvalidOperationException(
                $"DeriveContract returned a non-boarding contract for wallet {contractEntity.WalletIdentifier}");

        var destinationAddress = freshBoarding.GetOnchainAddress(serverInfo.Network);

        var signer = await walletProvider.GetSignerAsync(contractEntity.WalletIdentifier, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No signer for wallet {contractEntity.WalletIdentifier}; cannot sweep VTXO {vtxo.OutPoint}");

        if (!contractEntity.AdditionalData.TryGetValue("user", out var userDesc))
            throw new InvalidOperationException(
                $"User descriptor missing from contract data for VTXO {vtxo.OutPoint}");
        var descriptor = KeyExtensions.ParseOutputDescriptor(userDesc, serverInfo.Network);

        var feeRate = await blockchain.EstimateFeeRateAsync(6, cancellationToken);
        var sweepTx = Helpers.TransactionHelpers.BuildCsvSpendTransaction(
            vtxo.OutPoint, vtxoTxOut, destinationAddress,
            unilateralTapScript.Timeout, tapScript, controlBlock,
            feeRate, serverInfo.Network);

        var precomputed = sweepTx.PrecomputeTransactionData([vtxoTxOut]);
        var sighash = sweepTx.GetSignatureHashTaproot(
            precomputed,
            new TaprootExecutionData(0, tapScript.LeafHash) { SigHash = TaprootSigHash.Default });

        var (_, sig) = await signer.Sign(descriptor, sighash, cancellationToken);
        sweepTx.Inputs[0].WitScript = new WitScript(
            [sig.ToBytes(), tapScript.Script.ToBytes(), controlBlock.ToBytes()], true);

        var success = await blockchain.BroadcastAsync(sweepTx, cancellationToken);
        if (!success)
            throw new InvalidOperationException(
                $"Failed to broadcast sweep tx for VTXO {vtxo.OutPoint}");

        logger?.LogInformation(
            "Swept expired VTXO {Outpoint} ({Amount} sats) → {Address}, txid {SweepTxid}",
            vtxo.OutPoint, vtxo.Amount, destinationAddress, sweepTx.GetHash());
    }
}
