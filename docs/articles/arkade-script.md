# ArkadeScript & the Emulator (`NArk.Arkade`)

`NArk.Arkade` is an optional package that adds client-side support for **ArkadeScript** — a Bitcoin Script superset with extension opcodes for transaction introspection, asset queries, EC operations and streaming SHA-256.

A leaf carrying an ArkadeScript names a *tweaked emulator key* as one of its required signers. The [emulator](https://github.com/arkade-os/emulator) executes the attached script and only co-signs when it passes, which is what turns a script into an enforceable covenant. Consumers that don't use ArkadeScript carry no extra dependency.

```bash
dotnet add package NArk.Arkade
```

## Setup

```csharp
using NArk.Arkade.Hosting;

// Registers the emulator REST client AND the batch-flow co-signing extension,
// the spend-path packet provider, and the covenant submit handler.
services.AddArkadeEmulator(opts => opts.ServerUrl = "http://localhost:7073");

// Or register just the REST client and wire the pieces yourself:
services.AddEmulatorClient(opts => opts.ServerUrl = "http://localhost:7073");
```

Inject `IEmulatorProvider` to talk to the emulator directly:

```csharp
var info   = await emulator.GetInfoAsync();               // GET  /v1/info
var signed = await emulator.SubmitTxAsync(...);           // POST /v1/tx
var sig    = await emulator.SubmitIntentAsync(...);       // POST /v1/intent
var fin    = await emulator.SubmitFinalizationAsync(...); // POST /v1/finalization
var onchn  = await emulator.SubmitOnchainTxAsync(...);    // POST /v1/onchain-tx
```

## Composing a script

`ArkadeScript` converts between ASM text and bytes. `ArkadeTweak` derives the x-only key the emulator will co-sign a given script with:

```csharp
using NArk.Arkade.Crypto;
using NArk.Arkade.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

var bytes = ArkadeScript.AsmToBytes(
    "OP_0 OP_INSPECTOUTPUTSCRIPTPUBKEY 1 OP_EQUALVERIFY deadbeef OP_EQUAL");

// /v1/info returns a compressed (33-byte) signer pubkey; tweak it for this script.
var info = await emulator.GetInfoAsync();
var emulatorPubKey = ECPubKey.Create(Convert.FromHexString(info.SignerPubkey));
TaprootPubKey signingKey = ArkadeTweak.Tweak(emulatorPubKey, bytes);
```

Pin that leaf onto an `ArkContract` with the multisig wrapper:

```csharp
protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
{
    // N+1-of-N+1: your owners plus the tweaked emulator key.
    yield return new ArkadeNofNMultisigTapScript(
        arkadeScript: bytes,
        baseOwners: [aliceXOnly, bobXOnly],
        emulatorKeys: [emulatorPubKey]);

    yield return new UnilateralPathArkTapScript(...); // your other leaves
}
```

## Programs

Rather than hand-assembling leaves, a **program artifact** (the same JSON the TypeScript and Go SDKs consume) declares typed params and one function per spending path. `ArkProgramContract` compiles it into a contract:

```csharp
using System.Text.Json.Nodes;
using NArk.Arkade.Contracts;
using NArk.Arkade.Program;
using NArk.Arkade.Program.Models;

var program  = new ArkadeArtifactParser().ParseArtifact(JsonNode.Parse(artifactJson)!.AsObject());
var contract = new ArkProgramContract(
    server: serverDescriptor,
    program: program,
    args: new Dictionary<string, AsmToken>
    {
        ["hash"] = AsmToken.FromBytes(paymentHash),
    },
    user: walletDescriptor,
    emulatorKey: emulatorXOnly);

var address = contract.GetArkAddress();
```

`$server` and `$user` are bound automatically from the contract's own keys when the program declares them, so a caller only supplies the program-specific params. `ArkadeProgramValidator` type-checks the supplied args against the declared parameter types before compilation.

To spend a specific path, `ArkProgramContractTransformer` selects the function by name and attaches any call-time witness:

```csharp
var coin = await new ArkProgramContractTransformer(walletProvider)
    .Transform(walletId, contract, vtxo, "claim");

await spendingService.Spend(walletId, [coin], [payout], cancellationToken);
```

## How a covenant spend is submitted

Spending a covenant VTXO differs from an ordinary spend in three places, all wired by `AddArkadeEmulator`:

1. `ArkadeEmulatorPacketProvider` attaches an `EmulatorPacket` to the spend's single Extension `OP_RETURN`, carrying each Arkade-bound input's script and witness so the emulator can validate them. This shares the output with the asset packet, and must be attached before signing since signatures commit to the output set.
2. `ArkadeEmulatorSpendSubmitter` annotates every input of the signed transaction with the `prevarktx` ark PSBT field (see below), then routes it to the emulator instead of arkd. The emulator validates, co-signs, forwards to arkd and finalizes.

Spends with no Arkade-bound input are untouched and follow the normal arkd flow.

## Previous-transaction fields

Emulator `v0.0.7`+ (`validate checkpoints and prevouts`) requires every submitted input to carry the transaction that funded it — unconditionally, not only the inputs whose ArkadeScript introspects a previous output. Submissions missing it are rejected with `missing prevout tx for input N`.

The emulator only reads that transaction's *outputs*: it checks the txid, reconciles value and `scriptPubKey` against the declared witness utxo, and exposes the outputs to introspection opcodes. An unsigned copy therefore resolves correctly, which is why the SDK can serve a PSBT's global transaction without waiting for signatures to propagate. The field also lives in the PSBT `unknown` map, which no sighash covers, so it is attached after signing.

`IPrevArkTxProvider` resolves those transactions. The default `PrevArkTxProvider` reads from `IVirtualTxStorage` when the wallet already holds the VTXO's branch, then arkd's indexer (`GetVirtualTxs`), then — when an `IBitcoinBlockchain` is registered — from chain. Every fetched body is keyed by the txid parsed out of it, since arkd returns them in database order rather than request order.

The on-chain step exists because boarding and commitment transactions have no off-chain body. Offchain Arkade spends never need it (every input is a VTXO with a virtual parent), but an intent proof registering a boarding input does. It uses `IBitcoinBlockchain.GetRawTransactionAsync`, implemented by the Esplora and NBXplorer backends; a custom backend that does not override it throws `NotSupportedException` and the provider treats that as a miss.

An input already carrying the field is left alone rather than overwritten — the emulator rejects an input bearing two, so a caller-supplied value (a recursive covenant spending an Arkade transaction the indexer cannot serve yet) wins outright.

```csharp
using NArk.Arkade.Emulator;

// Offchain Arkade transaction. The transaction attached to Arkade input i is the one
// funding that input's *checkpoint* — not the checkpoint itself, which the emulator
// already holds — so the checkpoints must be supplied.
await arkTx.AttachPrevArkTxsAsync(checkpoints, prevArkTxProvider);

// BIP322 intent proof. Input 0 is the message input, whose prevout the emulator
// synthesises; inputs 1..N each get the transaction that created their outpoint.
await intentProof.AttachIntentPrevArkTxsAsync(prevArkTxProvider);
```

Both throw `InvalidOperationException` naming the input index and txid when a previous transaction cannot be resolved, rather than letting the emulator reject the submission.

`POST /v1/onchain-tx` uses the sibling `prevouttx` field. Attach each input's previous transaction with `PsbtHelpers.SetArkFieldPrevoutTx(input, prevTx)`, fetching it via `IBitcoinBlockchain.GetRawTransactionAsync`.

## Notes and current limits

- **Opcode values track the deployed VM.** Byte values follow `arkade-os/emulator` (`pkg/arkade/opcode.go`), which is authoritative and diverges from the ts-sdk table at `0xd7`–`0xe2`. A script built against the wrong table executes as a *different* opcode.
- **The batch tree-signing hop does not carry checkpoints.** `ArkadeBatchSessionExtension` co-signs through `POST /v1/tx` with an empty checkpoint list, and emulator `v0.0.7`+ requires exactly one checkpoint per input before it looks at anything else. That hop therefore needs the checkpoint-carrying shape (or its own endpoint) before it can work against a current emulator; the offchain spend path is unaffected.
- **Batch co-signing covers tree signing only.** `ArkadeBatchSessionExtension` handles the post-tree-signing phase. Forfeit signing throws `NotSupportedException`: the emulator signs forfeits through `POST /v1/finalization`, which requires the emulator-co-signed intent proof from intent-registration time plus the connector tree and commitment transaction. Submitting forfeits to `POST /v1/tx` would return them unsigned, so this fails loudly instead.
- **Resolving an on-chain parent needs a blockchain backend.** Without an `IBitcoinBlockchain` registered, `PrevArkTxProvider` sees only virtual transactions, and a coin whose parent came from chain fails with a named unresolved txid rather than a silent submission.
- **Packet bounds are enforced on parse**, matching the emulator's decoder: at most 1000 entries, script 1–10000 bytes, encoded witness at most 1,000,000 bytes.

## See also

- [Spending](spending.md) — the generic spend path these hooks extend
- [Assets](assets.md) — the asset packet that shares the Extension `OP_RETURN`
