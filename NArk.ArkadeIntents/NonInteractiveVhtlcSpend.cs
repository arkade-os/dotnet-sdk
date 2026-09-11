using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Arkade.Contracts;
using NArk.Core;
using NBitcoin;

namespace NArk.ArkadeIntents;

internal static class NonInteractiveVhtlcSpend
{
    internal static ArkTxOut[] Outputs(VHTLCv2Contract contract, IReadOnlyList<ArkVtxo> vtxos,
        ArkServerInfo serverInfo, bool refund = false)
    {
        var script = refund ? contract.NonInteractiveRefund?.SenderPkScript : contract.NonInteractiveClaim?.ReceiverPkScript;
        if (script is null || (refund && contract.NonInteractiveRefund is not { WithoutReceiver: true }))
            throw new InvalidOperationException("The lockup has no required non-interactive covenant leaf.");
        if (contract.Asset is not null || vtxos.Any(v => v.Assets is { Count: > 0 }))
            throw new InvalidOperationException("Non-interactive corridor payouts support sat-only lockups.");
        if (vtxos.Select(v => v.OutPoint).Distinct().Count() != vtxos.Count)
            throw new InvalidOperationException("Duplicate lockup outpoints cannot contribute to a payout.");
        // Without a strict floor, the indexed covenant preserves every input's full value, leaving dust as the only minimum.
        var minimum = Math.Max(serverInfo.Dust.Satoshi, refund ? 0 : contract.NonInteractiveClaim?.Strict?.Amount ?? 0);
        if (vtxos.Any(v => v.Amount < (ulong)minimum))
            throw new InvalidOperationException("Each lockup output must cover dust and its per-input covenant floor.");
        var destination = ArkAddress.FromScriptPubKey(new Script(script), serverInfo.SignerKey.ToXOnlyPubKey());
        return vtxos.Select(v => new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(checked((long)v.Amount)), destination)).ToArray();
    }
}
