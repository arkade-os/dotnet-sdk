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
using NArk.Arkade.Contracts;
using NArk.Arkade.Program;

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

Spending a covenant VTXO differs from an ordinary spend in two places, both wired by `AddArkadeEmulator`:

1. `ArkadeEmulatorPacketProvider` attaches an `EmulatorPacket` to the spend's single Extension `OP_RETURN`, carrying each arkade-bound input's script and witness so the emulator can validate them. This shares the output with the asset packet, and must be attached before signing since signatures commit to the output set.
2. `ArkadeEmulatorSpendSubmitter` routes the signed transaction to the emulator instead of arkd. The emulator validates, co-signs, forwards to arkd and finalizes.

Spends with no arkade-bound input are untouched and follow the normal arkd flow.

## Notes and current limits

- **Opcode values track the deployed VM.** Byte values follow `arkade-os/emulator` (`pkg/arkade/opcode.go`), which is authoritative and diverges from the ts-sdk table at `0xd7`–`0xe2`. A script built against the wrong table executes as a *different* opcode.
- **Batch co-signing covers tree signing only.** `ArkadeBatchSessionExtension` handles the post-tree-signing phase. Forfeit signing throws `NotSupportedException`: the emulator signs forfeits through `POST /v1/finalization`, which requires the emulator-co-signed intent proof from intent-registration time plus the connector tree and commitment transaction. Submitting forfeits to `POST /v1/tx` would return them unsigned, so this fails loudly instead.
- **Packet bounds are enforced on parse**, matching the emulator's decoder: at most 1000 entries, script 1–10000 bytes, encoded witness at most 1,000,000 bytes.

## See also

- [Spending](spending.md) — the generic spend path these hooks extend
- [Assets](assets.md) — the asset packet that shares the Extension `OP_RETURN`
