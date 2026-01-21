namespace NArk.Core.Transport.Models;

public record SubmitTxResponse(string ArkTxId, string FinalArkTx, string[] SignedCheckpointTxs);