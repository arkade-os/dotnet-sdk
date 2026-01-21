using Ark.V1;
using NArk.Scripts;
using NArk.Transport.GrpcClient.Extensions;
using NBitcoin;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    private static Sequence ParseSequence(long val)
    {
        return val >= 512 ? new Sequence(TimeSpan.FromSeconds(val)) : new Sequence((int)val);
    }

    public async Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await _serviceClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken);
        var network =
            response.Network switch
            {
                _ when Network.GetNetwork(response.Network) is { } net => net,
                "bitcoin" => Network.Main,
                _ => throw new InvalidOperationException("Ark server advertises unknown network")
            };

        var serverUnrollScript = UnilateralPathArkTapScript.Parse(response.CheckpointTapscript);

        if (ParseSequence(response.UnilateralExitDelay) != serverUnrollScript.Timeout)
            throw new InvalidOperationException("Ark server advertises inconsistent unilateral exit delay");

        var fPubKey = response.ForfeitPubkey.ToECXOnlyPubKey();

        if (!serverUnrollScript.OwnersMultiSig.Owners[0].ToBytes().SequenceEqual(fPubKey.ToBytes()))
            throw new InvalidOperationException("Ark server advertises inconsistent forfeit pubkey");

        return new ArkServerInfo(
            Dust: Money.Satoshis(response.Dust),
            SignerKey: KeyExtensions.ParseOutputDescriptor(response.SignerPubkey, network),
            DeprecatedSigners: response.DeprecatedSigners.ToDictionary(signer => signer.Pubkey.ToECXOnlyPubKey(),
                signer => signer.CutoffDate),
            Network: network,
            UnilateralExit: ParseSequence(response.UnilateralExitDelay),
            BoardingExit: ParseSequence(response.BoardingExitDelay),
            ForfeitAddress: BitcoinAddress.Create(response.ForfeitAddress, network),
            ForfeitPubKey: fPubKey,
            CheckpointTapScript: serverUnrollScript,
            FeeTerms: new ArkOperatorFeeTerms(
                TxFeeRate: GetOrZero(response.Fees.TxFeeRate),
                IntentOffchainOutput: GetOrZero(response.Fees.IntentFee.OffchainOutput),
                IntentOnchainOutput: GetOrZero(response.Fees.IntentFee.OnchainOutput),
                IntentOffchainInput: GetOrZero(response.Fees.IntentFee.OffchainInput),
                IntentOnchainInput: GetOrZero(response.Fees.IntentFee.OnchainInput)
            )
        );
    }

    private static string GetOrZero(string feeTern)
    {
        return string.IsNullOrWhiteSpace(feeTern) ? "0.0" : feeTern;
    }
}