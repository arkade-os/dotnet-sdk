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

Hand the note and a destination address to `ArkadeCashService`. Every spendable VTXO at the
note's address is swept to that destination; anything that cannot be swept is reported.

```csharp
var cashService = sp.GetRequiredService<ArkadeCashService>();

if (!ArkadeCash.TryParse(note, out var claimed) || claimed is null)
    throw new FormatException("Not a valid ArkadeCash note");

using (claimed)
{
    var destination = (await contractService.DeriveContract(walletId, NextContractPurpose.Receive))
        .GetArkAddress();

    var result = await cashService.ClaimAsync(claimed, destination);
    Console.WriteLine($"Swept {result.Swept} sat, {result.UnclaimedAmount} sat left behind");

    foreach (var left in result.Unclaimed)
        Console.WriteLine($"{left.Outpoint} ({left.Amount} sat): {left.Reason}");
}
```

`Parse` throws `FormatException` on a bad prefix, checksum, payload length, or version;
`TryParse` returns `false` instead.

### Why the claim persists nothing

No contract is imported and the note's key never reaches wallet storage — `ClaimAsync` signs one
offchain transaction per VTXO in memory, paying straight to the destination.

This is not just tidiness. Importing the note's contract would register a script for the wallet to
**watch** and nothing more: the wallet holds no key matching the note's descriptor, so its signer
could never sign for it. A note is spendable only by whoever holds the string, which is the whole
point of a bearer instrument — and it is why the note is invisible to wallet recovery too (see
below).

Two consequences worth knowing:

- **A failed sweep is reported, not thrown.** One transaction per VTXO means a stale or rejected
  input dents only its own sweep. Everything left behind comes back in `Unclaimed` with a reason:
  `AlreadySpent`, `ServerSwept`, `Subdust`, `AssetBearing`, or `SweepFailed`. Claiming a note that
  was already claimed is therefore a report, not a failure.
- **Signer rotation does not block a claim.** The claim looks the funds up under the signer the note
  encodes, not the one the server currently advertises, and spends them on that contract's
  collaborative path. The operator keeps co-signing that key until its deprecation cutoff passes.

## Handling notes safely

- The encoded string **is** the secret. Anyone who sees it can sweep the funds, so treat
  it like a private key: no logs, no analytics, no URL query strings.
- A note is a short-lived instrument. Claim it promptly — until it is claimed, the sender
  still holds a copy of the key too.
- `ArkadeCash` owns its `ECPrivKey` and implements `IDisposable`. Dispose it once the note
  has been claimed or persisted so the key is zeroed.
- **Wallet recovery cannot find a note.** Contract discovery probes contracts derived from a
  wallet's key at each HD index; a note's key is random and derivable from nothing, so no scan will
  ever turn it up. The string is the only copy — losing it loses the funds, however well synced the
  wallet is.

## API surface

| Member | Purpose |
| --- | --- |
| `ArkadeCash.Generate(serverPubkey, locktime, hrp?)` | New note with a random key |
| `new ArkadeCash(privKey, serverPubkey, lockTime, hrp?)` | Note around an existing key |
| `ToString()` | Encode as `arkadecash1...` / `tarkadecash1...` |
| `ArkadeCash.Parse(encoded)` / `TryParse(encoded, out cash)` | Decode |
| `ArkadeCashService.ClaimAsync(cash, destination)` | Sweep the note to an address; report what could not be swept |
| `ToContract(serverInfo)` / `ToContract(network)` | Rebuild the `ArkPaymentContract` the funds are locked to |
| `GetAddress(network)` | Derive the Arkade address of that contract |

`ToContract` and `GetAddress` are extension methods in `NArk.Core.Extensions`.
