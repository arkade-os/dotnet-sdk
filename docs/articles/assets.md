# Assets

Arkade supports programmable assets — tokens that live alongside BTC in VTXOs.

## Issuance

Create a new asset:

```csharp
var assetOutputs = new[] {
    new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(1000), recipientAddress)
        .WithAssetIssuance(amount: 1_000_000, metadata: new Dictionary<string, string>
        {
            ["name"] = "MyToken",
            ["ticker"] = "MTK"
        })
};

var txId = await spendingService.Spend(walletId, assetOutputs, ct);
```

## Transfer

Send existing assets:

```csharp
var txId = await spendingService.Spend(
    walletId,
    [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(546), recipientAddress)
        .WithAsset(assetId, amount: 500)],
    ct);
```

## Controlled Issuance

Issue assets with a control token for reissuance:

```csharp
// First issuance — creates a control asset
var outputs = new[] {
    ArkTxOut.ControlledIssuance(controlAssetId, amount: 1_000_000, recipientAddress)
};
```

## Querying Assets

```csharp
var vtxos = await vtxoStorage.GetVtxos(walletIds: [walletId]);
foreach (var vtxo in vtxos.Where(v => v.Assets?.Any() == true))
{
    foreach (var asset in vtxo.Assets!)
        Console.WriteLine($"Asset: {asset.AssetId}, Amount: {asset.Amount}");
}
```

## Binary Format

Assets are encoded in OP_RETURN outputs using a compact binary format:

- `ARK` magic bytes + marker byte + groups
- Each group contains asset outputs with vout references and varint amounts
- Metadata is preserved in insertion order
