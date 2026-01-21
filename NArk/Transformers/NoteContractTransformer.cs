using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Contracts;
using NBitcoin;

namespace NArk.Transformers;

public class NoteContractTransformer : IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
        => contract is ArkNoteContract;
    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var contractObj = contract as ArkNoteContract;
        return new ArkCoin(walletIdentifier, contractObj!, vtxo?.CreatedAt ?? DateTime.MinValue, vtxo?.ExpiresAt ?? DateTime.MaxValue, vtxo?.ExpiresAtHeight, contractObj!.Outpoint, vtxo?.TxOut ?? new TxOut(Money.Satoshis(contractObj.Amount), contractObj.CreateClaimScript().Build().Script), null,
            contractObj.CreateClaimScript(), new WitScript(Op.GetPushOp(contractObj.Preimage)), null, null, true);
    }
}