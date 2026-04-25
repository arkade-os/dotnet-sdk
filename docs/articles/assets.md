# Assets

Arkade supports programmable assets — tokens that live alongside BTC in VTXOs. Asset state is encoded in an `AssetGroup` entry inside an OP_RETURN output attached to each Arkade transaction (an "asset packet"). Asset IDs are derived from `{txid, groupIndex}` after submission.

Asset operations go through `IAssetManager` (registered by `AddArkCoreServices`).

## Issuance

Create a new asset on first issuance:

```csharp
var result = await assetManager.IssueAsync(walletId,
    new IssuanceParams(Amount: 1_000_000));

// result.AssetId — the unique asset identifier
// result.ArkTxId — the Arkade transaction that created it
```

With metadata:

```csharp
var result = await assetManager.IssueAsync(walletId,
    new IssuanceParams(
        Amount: 1_000_000,
        Metadata: new Dictionary<string, string>
        {
            ["name"] = "MyToken",
            ["ticker"] = "MTK",
        }));
```

Metadata is preserved in insertion order (it is stored as a BIP-spec varint-length-prefixed list, not a hashmap).

## Controlled Issuance & Reissuance

A **control asset** acts as a minting key — only the holder can issue more supply of the controlled asset:

```csharp
// 1. Issue a control asset (amount=1, acts as the minting authority)
var control = await assetManager.IssueAsync(walletId,
    new IssuanceParams(Amount: 1));

// 2. Issue a token controlled by that asset
var token = await assetManager.IssueAsync(walletId,
    new IssuanceParams(Amount: 1_000_000, ControlAssetId: control.AssetId));

// 3. Reissue more supply later (requires holding the control asset VTXO)
await assetManager.ReissueAsync(walletId,
    new ReissuanceParams(control.AssetId, Amount: 500_000));
```

Reissuance keeps the control asset VTXO intact — only the supply of the controlled token increases.

## Transfer

Asset transfers go through the standard `SpendingService.Spend()` call — attach `ArkTxOutAsset` entries to an `ArkTxOut`:

```csharp
var serverInfo = await clientTransport.GetServerInfoAsync(ct);

await spendingService.Spend(walletId, new[]
{
    new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, recipientAddress)
    {
        Assets = [new ArkTxOutAsset(assetId, Amount: 500)],
    },
}, ct);
```

The BTC output amount can be the server's dust minimum — the VTXO's *value* is the asset payload, not the sats.

## Burn

Send the asset to a burn output (an OP_RETURN with no destination contract):

```csharp
await assetManager.BurnAsync(walletId,
    new BurnParams(assetId, Amount: 100));
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

Asset metadata can be fetched from arkd's indexer:

```csharp
var assetInfo = await clientTransport.GetAssetAsync(assetId, ct);
// assetInfo.Metadata is already decoded into a Dictionary<string, string>
```

## Binary Format

Assets are encoded in OP_RETURN outputs using a compact binary format:

- `ARK` magic bytes + marker byte + groups
- Presence byte bitfields: `0x01` AssetId, `0x02` ControlAsset, `0x04` Metadata
- Each `AssetOutput` carries a 0x01 type byte + uint16-LE vout + varint amount
- Metadata is a varint-length-prefixed list preserving insertion order
- The asset Merkle tree is aligned with BIP-341 taptree ordering
