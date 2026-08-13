using Ark.V1;
using NArk.Abstractions.Extensions;
using NArk.Core;
using NArk.Core.Scripts;
using NArk.Transport.GrpcClient.Extensions;
using NBitcoin;
using KeyExtensions = NArk.Transport.GrpcClient.Extensions.KeyExtensions;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    /// <summary>
    /// Reads an operator delay into a relative locktime, rounding a seconds-based one UP.
    /// </summary>
    /// <remarks>
    /// BIP68 encodes seconds in 512-second units and <see cref="Sequence"/> truncates, so an
    /// operator advertising 3600s would come back as 3584 — a timelock SHORTER than the one it
    /// requires, which its own validation then refuses. Rounding up is also what the reference
    /// implementations do before deriving anything from this value, so flooring here additionally
    /// puts every covenant built on it at a different address than the counterparty's.
    /// </remarks>
    private static Sequence ParseSequence(long val)
    {
        if (val < 512) return new Sequence((int)val);

        var units = (val + 511) / 512;
        return new Sequence(TimeSpan.FromSeconds(units * 512));
    }

    public async Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await _serviceClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken);
        var network = NArk.Core.Transport.Extensions.NetworkExtensions.ResolveArkNetwork(response.Network);

        var serverUnrollScript = UnilateralPathArkTapScript.Parse(response.CheckpointTapscript);
        //
        // if (ParseSequence(response.UnilateralExitDelay) != serverUnrollScript.Timeout)
        //     throw new InvalidOperationException("Ark server advertises inconsistent unilateral exit delay");

        var fPubKey = response.ForfeitPubkey.ToECXOnlyPubKey();

        // if (!serverUnrollScript.OwnersMultiSig.Owners[0].ToBytes().SequenceEqual(fPubKey.ToBytes()))
        //     throw new InvalidOperationException("Ark server advertises inconsistent forfeit pubkey");

        var result = new ArkServerInfo(
            Dust: Money.Satoshis(response.Dust),
            SignerKey: KeyExtensions.ParseOutputDescriptor(response.SignerPubkey, network),
            DeprecatedSigners: response.DeprecatedSigners.ToDictionary(signer => signer.Pubkey.ToECXOnlyPubKey(),
                signer => signer.CutoffDate, ECXOnlyPubKeyComparer.Instance),
            Network: network,
            UnilateralExit: ParseSequence(response.UnilateralExitDelay),
            BoardingExit: ParseSequence(response.BoardingExitDelay),
            ForfeitAddress: BitcoinAddress.Create(response.ForfeitAddress, network),
            ForfeitPubKey: fPubKey,
            CheckpointTapScript: serverUnrollScript,
            Digest: response.Digest,
            FeeTerms: new ArkOperatorFeeTerms(
                TxFeeRate: GetOrZero(response.Fees.TxFeeRate),
                IntentOffchainOutput: GetOrZero(response.Fees.IntentFee.OffchainOutput),
                IntentOnchainOutput: GetOrZero(response.Fees.IntentFee.OnchainOutput),
                IntentOffchainInput: GetOrZero(response.Fees.IntentFee.OffchainInput),
                IntentOnchainInput: GetOrZero(response.Fees.IntentFee.OnchainInput)
            ),
            MaxTxWeight: response.MaxTxWeight,
            MaxOpReturnOutputs: (int)response.MaxOpReturnOutputs,
            VtxoMinAmount: Money.Satoshis(response.VtxoMinAmount),
            VtxoMaxAmount: response.VtxoMaxAmount < 0 ? Money.Coins(21_000_000m) : Money.Satoshis(response.VtxoMaxAmount),
            UtxoMinAmount: Money.Satoshis(response.UtxoMinAmount),
            UtxoMaxAmount: response.UtxoMaxAmount < 0 ? Money.Coins(21_000_000m) : Money.Satoshis(response.UtxoMaxAmount)
        );
        _digestHolder.Digest = result.Digest;
        return result;
    }

    private static string GetOrZero(string feeTern)
    {
        return string.IsNullOrWhiteSpace(feeTern) ? "0.0" : feeTern;
    }
}