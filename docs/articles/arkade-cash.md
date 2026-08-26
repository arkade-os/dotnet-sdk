# ArkadeCash

`ArkadeCash` is a **bearer instrument**. It packs a fresh private key together with the
contract parameters needed to rebuild the Arkade payment contract it funds into a single
bech32m string. Whoever holds that string controls the funds, so value can change hands
without the recipient first sharing an Arkade address.

## Encoding

| Prefix | Network |
| --- | --- |
| `arkadecash1...` | mainnet |
| `tarkadecash1...` | testnet / regtest |

The bech32m payload is 69 bytes:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 1 | version (`0x00`) |
| 1 | 32 | note private key |
| 33 | 32 | Arkade server x-only public key |
| 65 | 4 | BIP68 CSV sequence, big-endian |

This is the same format the TypeScript SDK's `ArkadeCash` uses, so a note created by one
SDK can be claimed by the other.

## Creating and funding a note

```csharp
using NArk.Abstractions;
using NArk.Core.Extensions;

var serverInfo = await transport.GetServerInfoAsync();

var cash = ArkadeCash.Generate(
    serverInfo.SignerKey.ToXOnlyPubKey(),
    serverInfo.UnilateralExit,
    "tarkadecash");

var address = cash.GetAddress(serverInfo.Network);

await spendingService.Spend(
    walletId,
    [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(10_000), address)]);

string note = cash.ToString(); // hand this to the recipient
```

`ArkadeCash.Generate` derives a random key; the constructor overload takes an existing
`ECPrivKey` when you want to control key material yourself. The HRP argument must be
`arkadecash` or `tarkadecash` — anything else throws `ArgumentException`.

## Claiming a note

Parse the string, import the contract it describes, then spend the VTXOs sitting at its
address into the claiming wallet:

```csharp
if (!ArkadeCash.TryParse(note, out var claimed) || claimed is null)
    throw new FormatException("Not a valid ArkadeCash note");

using (claimed)
{
    var contract = claimed.ToContract(serverInfo.Network);
    await contractService.ImportContract(walletId, contract);

    var script = claimed.GetAddress(serverInfo.Network).ScriptPubKey.ToHex();
    await vtxoSynchronizationService.PollScriptsForVtxos(new HashSet<string> { script });
    // the VTXOs are now spendable by walletId
}
```

`Parse` throws `FormatException` on a bad prefix, checksum, payload length, or version;
`TryParse` returns `false` instead.

## Handling notes safely

- The encoded string **is** the secret. Anyone who sees it can sweep the funds, so treat
  it like a private key: no logs, no analytics, no URL query strings.
- A note is a short-lived instrument. Claim it promptly — until it is claimed, the sender
  still holds a copy of the key too.
- `ArkadeCash` owns its `ECPrivKey` and implements `IDisposable`. Dispose it once the note
  has been claimed or persisted so the key is zeroed.

## API surface

| Member | Purpose |
| --- | --- |
| `ArkadeCash.Generate(serverPubkey, locktime, hrp?)` | New note with a random key |
| `new ArkadeCash(privKey, serverPubkey, lockTime, hrp?)` | Note around an existing key |
| `ToString()` | Encode as `arkadecash1...` / `tarkadecash1...` |
| `ArkadeCash.Parse(encoded)` / `TryParse(encoded, out cash)` | Decode |
| `ToContract(network)` | Rebuild the `ArkPaymentContract` the funds are locked to |
| `GetAddress(network)` | Derive the Arkade address of that contract |

`ToContract` and `GetAddress` are extension methods in `NArk.Core.Extensions`.
