# Boarding Support Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add boarding (onchain-to-VTXO onboarding) support to NNark, including contract model, UTXO tracking, batch session commitment tx signing, and sweep logic.

**Architecture:** Hybrid approach — `ArkBoardingContract` plugs into the existing contract/transformer system for script construction and intent integration. A dedicated `BoardingUtxoSyncService` tracks onchain UTXOs via NBXplorer. Boarding and unrolled VTXOs share batch behavior (`Unrolled = true`): skip forfeits, sign commitment tx instead. See `docs/plans/2026-03-06-boarding-design.md` for full design.

**Tech Stack:** C# / .NET 8, NBitcoin, NSubstitute (mocking), NUnit (tests), gRPC, NBXplorer

---

## Task 1: Add `Unrolled` property to `ArkCoin`

**Files:**
- Modify: `NArk.Abstractions/ArkCoin.cs`

**Step 1: Add Unrolled to constructor and property**

In `NArk.Abstractions/ArkCoin.cs`, add `bool unrolled` parameter after `bool swept` and a corresponding property. Update `RequiresForfeit()`:

```csharp
// Constructor — add after "bool swept" parameter (line 25):
bool unrolled,
// Keep existing: IReadOnlyList<VtxoAsset>? assets = null

// In constructor body, add:
Unrolled = unrolled;

// New property (after Swept):
public bool Unrolled { get; }

// Update RequiresForfeit (line 90):
public bool RequiresForfeit()
{
    return !Swept && !Unrolled;
}
```

**Step 2: Update copy constructor**

The copy constructor at line 48 must pass `other.Unrolled`.

**Step 3: Fix all callers**

Every `new ArkCoin(...)` call must now include the `unrolled` parameter. Search all callers:
- `NArk.Core/Transformers/PaymentContractTransformer.cs:41` — pass `vtxo.Unrolled` (or `false`)
- `NArk.Core/Transformers/HashLockedContractTransformer.cs` — pass `false`
- `NArk.Core/Transformers/NoteContractTransformer.cs` — pass `false`
- `NArk.Swaps/` — any VHTLC transformer, pass `false`
- `NArk.Tests/` — any test constructing ArkCoin, pass `false`

Search: `new ArkCoin(` across all `.cs` files to find every call site.

**Step 4: Build to verify**

Run: `dotnet build NArk.sln`
Expected: SUCCESS

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add Unrolled property to ArkCoin"
```

---

## Task 2: Update `SubmitSignedForfeitTxsRequest` and transport

**Files:**
- Modify: `NArk.Abstractions/Batches/SubmitSignedForfeitTxsRequest.cs`
- Modify: `NArk.Core/Transport/GrpcClient/GrpcClientTransport.Batch.cs`
- Modify: `NArk.Core/Transport/IClientTransport.cs` (if signature changes)

**Step 1: Add SignedCommitmentTx to request record**

In `NArk.Abstractions/Batches/SubmitSignedForfeitTxsRequest.cs`:

```csharp
public record SubmitSignedForfeitTxsRequest(string[] SignedForfeitTxs, string? SignedCommitmentTx = null);
```

**Step 2: Update GrpcClientTransport to include commitment tx**

In `NArk.Core/Transport/GrpcClient/GrpcClientTransport.Batch.cs`, update `SubmitSignedForfeitTxsAsync`:

```csharp
public async Task SubmitSignedForfeitTxsAsync(
    SubmitSignedForfeitTxsRequest req,
    CancellationToken cancellationToken)
{
    var grpcReq = new Ark.V1.SubmitSignedForfeitTxsRequest
    {
        SignedForfeitTxs = { req.SignedForfeitTxs }
    };
    if (req.SignedCommitmentTx is not null)
        grpcReq.SignedCommitmentTx = req.SignedCommitmentTx;

    await _serviceClient.SubmitSignedForfeitTxsAsync(grpcReq, cancellationToken: cancellationToken);
}
```

**Step 3: Build to verify**

Run: `dotnet build NArk.sln`
Expected: SUCCESS

**Step 4: Commit**

```bash
git add -A && git commit -m "feat: add SignedCommitmentTx to SubmitSignedForfeitTxsRequest"
```

---

## Task 3: Create `ArkBoardingContract`

**Files:**
- Create: `NArk.Core/Contracts/ArkBoardingContract.cs`
- Modify: `NArk.Core/Hosting/ServiceCollectionExtensions.cs` (register parser)

**Step 1: Write the failing test**

Create `NArk.Tests/BoardingContractTests.cs`:

```csharp
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests;

[TestFixture]
public class BoardingContractTests
{
    private static readonly Network Network = Network.RegTest;

    private static OutputDescriptor MakeDescriptor(ECXOnlyPubKey pubKey) =>
        OutputDescriptor.NewRawTR(pubKey, Network);

    [Test]
    public void BoardingContract_GeneratesCorrectTapscriptTree()
    {
        var serverKey = new Key().PubKey.GetTaprootFullPubKey().OutputKey;
        var userKey = new Key().PubKey.GetTaprootFullPubKey().OutputKey;
        var exitDelay = new Sequence(144);

        var contract = new ArkBoardingContract(
            MakeDescriptor(serverKey), exitDelay, MakeDescriptor(userKey));

        Assert.That(contract.Type, Is.EqualTo("Boarding"));

        var scripts = contract.GetTapScriptList();
        Assert.That(scripts, Has.Length.EqualTo(2));

        // Collaborative path should contain both keys
        var collabScript = scripts[0].Script;
        Assert.That(collabScript.ToString(), Does.Contain("OP_CHECKSIGVERIFY"));
        Assert.That(collabScript.ToString(), Does.Contain("OP_CHECKSIG"));

        // Unilateral path should contain CSV
        var exitScript = scripts[1].Script;
        Assert.That(exitScript.ToString(), Does.Contain("OP_CHECKSEQUENCEVERIFY"));
    }

    [Test]
    public void BoardingContract_ProducesDeterministicAddress()
    {
        var serverKey = new Key().PubKey.GetTaprootFullPubKey().OutputKey;
        var userKey = new Key().PubKey.GetTaprootFullPubKey().OutputKey;
        var exitDelay = new Sequence(144);

        var contract1 = new ArkBoardingContract(
            MakeDescriptor(serverKey), exitDelay, MakeDescriptor(userKey));
        var contract2 = new ArkBoardingContract(
            MakeDescriptor(serverKey), exitDelay, MakeDescriptor(userKey));

        var addr1 = contract1.GetArkAddress();
        var addr2 = contract2.GetArkAddress();
        Assert.That(addr1.ToString(), Is.EqualTo(addr2.ToString()));
    }

    [Test]
    public void BoardingContract_DifferentKeys_DifferentAddresses()
    {
        var serverKey = new Key().PubKey.GetTaprootFullPubKey().OutputKey;
        var userKey1 = new Key().PubKey.GetTaprootFullPubKey().OutputKey;
        var userKey2 = new Key().PubKey.GetTaprootFullPubKey().OutputKey;
        var exitDelay = new Sequence(144);

        var contract1 = new ArkBoardingContract(
            MakeDescriptor(serverKey), exitDelay, MakeDescriptor(userKey1));
        var contract2 = new ArkBoardingContract(
            MakeDescriptor(serverKey), exitDelay, MakeDescriptor(userKey2));

        Assert.That(contract1.GetArkAddress().ToString(),
            Is.Not.EqualTo(contract2.GetArkAddress().ToString()));
    }

    [Test]
    public void BoardingContract_ParseRoundTrip()
    {
        var serverKey = new Key().PubKey.GetTaprootFullPubKey().OutputKey;
        var userKey = new Key().PubKey.GetTaprootFullPubKey().OutputKey;
        var exitDelay = new Sequence(144);

        var original = new ArkBoardingContract(
            MakeDescriptor(serverKey), exitDelay, MakeDescriptor(userKey));
        var entity = original.ToEntity("wallet-1", ContractActivityState.Active);

        var parsed = ArkBoardingContract.Parse(entity.AdditionalData, Network);

        Assert.That(parsed, Is.InstanceOf<ArkBoardingContract>());
        Assert.That(parsed.GetArkAddress().ToString(),
            Is.EqualTo(original.GetArkAddress().ToString()));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test NArk.Tests --filter "BoardingContractTests" -v n`
Expected: FAIL — `ArkBoardingContract` does not exist

**Step 3: Implement `ArkBoardingContract`**

Create `NArk.Core/Contracts/ArkBoardingContract.cs`:

```csharp
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Core.Extensions;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Contracts;

public class ArkBoardingContract(OutputDescriptor server, Sequence exitDelay, OutputDescriptor userDescriptor)
    : ArkContract(server)
{
    private readonly Sequence _exitDelay = exitDelay;

    public OutputDescriptor User { get; } = userDescriptor;

    public override string Type => ContractType;
    public const string ContractType = "Boarding";

    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return [CollaborativePath(), UnilateralPath()];
    }

    public ScriptBuilder CollaborativePath()
    {
        var ownerScript = new NofNMultisigTapScript([User.ToXOnlyPubKey()]);
        return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(), ownerScript);
    }

    public ScriptBuilder UnilateralPath()
    {
        var ownerScript = new NofNMultisigTapScript([User.ToXOnlyPubKey()]);
        return new UnilateralPathArkTapScript(_exitDelay, ownerScript);
    }

    protected override Dictionary<string, string> GetContractData()
    {
        return new Dictionary<string, string>
        {
            ["exit_delay"] = _exitDelay.Value.ToString(),
            ["user"] = User.ToString(),
            ["server"] = Server!.ToString()
        };
    }

    public static ArkContract Parse(Dictionary<string, string> contractData, Network network)
    {
        var server = KeyExtensions.ParseOutputDescriptor(contractData["server"], network);
        var exitDelay = new Sequence(uint.Parse(contractData["exit_delay"]));
        var userDescriptor = KeyExtensions.ParseOutputDescriptor(contractData["user"], network);
        return new ArkBoardingContract(server, exitDelay, userDescriptor);
    }
}
```

**Step 4: Register parser in DI**

In `NArk.Core/Hosting/ServiceCollectionExtensions.cs`, in `AddArkCoreServices()`, add after existing parser registrations:

```csharp
services.AddTransient<IArkContractParser>(_ =>
    new GenericArkContractParser(ArkBoardingContract.ContractType, ArkBoardingContract.Parse));
```

Also check if `ArkPaymentContract` parser is registered; if so, follow the same pattern for boarding.

**Step 5: Run tests**

Run: `dotnet test NArk.Tests --filter "BoardingContractTests" -v n`
Expected: PASS

**Step 6: Commit**

```bash
git add -A && git commit -m "feat: add ArkBoardingContract"
```

---

## Task 4: Create `BoardingContractTransformer`

**Files:**
- Create: `NArk.Core/Transformers/BoardingContractTransformer.cs`
- Modify: `NArk.Core/Hosting/ServiceCollectionExtensions.cs` (register transformer)

**Step 1: Write the failing test**

Add to `NArk.Tests/BoardingContractTests.cs` (or create `NArk.Tests/BoardingContractTransformerTests.cs`):

```csharp
using NArk.Core.Transformers;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NSubstitute;

// In test class:
[Test]
public async Task BoardingContractTransformer_CanTransform_ReturnsTrueForBoardingContract()
{
    var walletProvider = Substitute.For<IWalletProvider>();
    var addressProvider = Substitute.For<IArkadeAddressProvider>();
    walletProvider.GetAddressProviderAsync("wallet-1", Arg.Any<CancellationToken>())
        .Returns(addressProvider);
    addressProvider.IsOurs(Arg.Any<OutputDescriptor>(), Arg.Any<CancellationToken>())
        .Returns(true);
    walletProvider.GetSignerAsync("wallet-1", Arg.Any<CancellationToken>())
        .Returns(Substitute.For<IArkadeWalletSigner>());

    var serverKey = new Key().PubKey.GetTaprootFullPubKey().OutputKey;
    var userKey = new Key().PubKey.GetTaprootFullPubKey().OutputKey;
    var contract = new ArkBoardingContract(
        MakeDescriptor(serverKey), new Sequence(144), MakeDescriptor(userKey));

    var vtxo = new ArkVtxo(
        Script: contract.GetTaprootSpendInfo().ScriptPubKey.ToHex(),
        TransactionId: uint256.One.ToString(),
        TransactionOutputIndex: 0,
        Amount: 100000,
        SpentByTransactionId: null,
        SettledByTransactionId: null,
        Swept: false,
        CreatedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddDays(90),
        ExpiresAtHeight: null,
        Unrolled: true);

    var transformer = new BoardingContractTransformer(walletProvider);
    var result = await transformer.CanTransform("wallet-1", contract, vtxo);
    Assert.That(result, Is.True);
}

[Test]
public async Task BoardingContractTransformer_Transform_ProducesCorrectCoin()
{
    // Same setup as above...
    var transformer = new BoardingContractTransformer(walletProvider);
    var coin = await transformer.Transform("wallet-1", contract, vtxo);

    Assert.That(coin.Unrolled, Is.True);
    Assert.That(coin.RequiresForfeit(), Is.False);
    Assert.That(coin.Swept, Is.False);
}

[Test]
public async Task BoardingContractTransformer_CanTransform_ReturnsFalseForPaymentContract()
{
    var walletProvider = Substitute.For<IWalletProvider>();
    var contract = new ArkPaymentContract(/* ... */);
    var transformer = new BoardingContractTransformer(walletProvider);

    var result = await transformer.CanTransform("wallet-1", contract, /* vtxo */);
    Assert.That(result, Is.False);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test NArk.Tests --filter "BoardingContractTransformer" -v n`
Expected: FAIL

**Step 3: Implement `BoardingContractTransformer`**

Create `NArk.Core/Transformers/BoardingContractTransformer.cs`:

```csharp
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;

namespace NArk.Core.Transformers;

public class BoardingContractTransformer(
    IWalletProvider walletProvider,
    ILogger<BoardingContractTransformer>? logger = null) : IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        if (contract is not ArkBoardingContract boardingContract)
            return false;

        if (boardingContract.User is null)
            return false;

        if (await walletProvider.GetAddressProviderAsync(walletIdentifier) is not { } addressProvider)
            return false;

        if (!await addressProvider.IsOurs(boardingContract.User))
        {
            logger?.LogWarning(
                "BoardingContract user descriptor not ours: wallet={WalletId}, userDescriptor={UserDescriptor}",
                walletIdentifier, boardingContract.User);
            return false;
        }

        if (await walletProvider.GetSignerAsync(walletIdentifier) is null)
            return false;

        return true;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var boardingContract = (contract as ArkBoardingContract)!;
        return new ArkCoin(
            walletIdentifier, contract,
            vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut,
            boardingContract.User ?? throw new InvalidOperationException("User is required"),
            boardingContract.CollaborativePath(),
            null, null, null,
            vtxo.Swept,
            vtxo.Unrolled,
            assets: vtxo.Assets);
    }
}
```

**Step 4: Register transformer in DI**

In `NArk.Core/Hosting/ServiceCollectionExtensions.cs`, in `AddArkCoreServices()`:

```csharp
services.AddTransient<IContractTransformer, BoardingContractTransformer>();
```

**Step 5: Run tests**

Run: `dotnet test NArk.Tests --filter "BoardingContractTransformer" -v n`
Expected: PASS

**Step 6: Commit**

```bash
git add -A && git commit -m "feat: add BoardingContractTransformer"
```

---

## Task 5: Add `Boarding` to `NextContractPurpose` and address generation

**Files:**
- Modify: `NArk.Abstractions/Wallets/IArkadeAddressProvider.cs`
- Modify: `NArk.Core/Wallet/SingleKeyAddressProvider.cs`
- Modify: `NArk.Core/Wallet/HierarchicalDeterministicAddressProvider.cs`
- Modify: `NArk.Tests.End2End/Wallets/SimpleSeedWallet.cs`
- Modify: `NArk.Core/Services/ContractService.cs` (or `IContractService`)

**Step 1: Add `Boarding` enum value**

In `NArk.Abstractions/Wallets/IArkadeAddressProvider.cs`:

```csharp
public enum NextContractPurpose
{
    Receive,
    SendToSelf,
    Boarding
}
```

**Step 2: Handle `Boarding` in address providers**

Each `GetNextContract` implementation needs to handle `NextContractPurpose.Boarding`. The boarding contract uses `boarding_exit_delay` from server info instead of `unilateral_exit_delay`.

The address providers need access to `ArkServerInfo` to get:
- `BoardingExit` (the CSV delay)
- Server signer pubkey

This means `GetNextContract` for `Boarding` purpose needs server info. Check how `IContractService.DeriveContract` passes this — it may need to fetch server info via `IClientTransport.GetServerInfoAsync()`.

In `SingleKeyAddressProvider.cs` and `HierarchicalDeterministicAddressProvider.cs`, add a case for `Boarding`:

```csharp
if (purpose == NextContractPurpose.Boarding)
{
    // The caller (ContractService) should pass serverInfo or the address provider
    // needs IClientTransport. Check existing patterns and adapt.
    // Create ArkBoardingContract instead of ArkPaymentContract.
}
```

**Important:** This task requires careful review of how `ContractService.DeriveContract` works. It may be cleaner to add a dedicated `DeriveBoardingContract` method to `IContractService` that explicitly takes/fetches `ArkServerInfo`.

**Step 3: Build and run all tests**

Run: `dotnet build NArk.sln && dotnet test NArk.Tests -v n`
Expected: PASS (existing tests shouldn't break)

**Step 4: Commit**

```bash
git add -A && git commit -m "feat: add Boarding purpose to address provider"
```

---

## Task 6: Update `BatchSession.HandleBatchFinalizationAsync` for boarding

**Files:**
- Modify: `NArk.Core/Batches/BatchSession.cs`

**Step 1: Write failing test**

Add test to `NArk.Tests/BatchSessionTests.cs` (or create new test file) that verifies:
- When `ins` contains an unrolled coin, `HandleBatchFinalizationAsync` signs the commitment tx
- When `ins` contains an unrolled coin, forfeit txs are skipped for that coin
- `SubmitSignedForfeitTxsAsync` is called with `SignedCommitmentTx` populated

This test will need to mock `IClientTransport`, `IWalletProvider`, and provide a realistic `BatchFinalizationEvent` with a commitment PSBT that includes the boarding input.

**Step 2: Implement commitment tx signing in HandleBatchFinalizationAsync**

After the existing forfeit loop in `HandleBatchFinalizationAsync` (after line 274 in `BatchSession.cs`):

```csharp
// Sign commitment tx for boarding/unrolled inputs
string? signedCommitmentTx = null;
var boardingCoins = ins.Where(c => c.Unrolled).ToList();
if (boardingCoins.Count > 0)
{
    var commitmentPsbt = PSBT.Parse(finalizationEvent.CommitmentTx, network);

    foreach (var boardingCoin in boardingCoins)
    {
        var psbtInput = boardingCoin.FillPsbtInput(commitmentPsbt);
        if (psbtInput is null)
        {
            throw new InvalidOperationException(
                $"Boarding input {boardingCoin.Outpoint} not found in commitment tx");
        }

        var signer = await walletProvider.GetSignerAsync(
            boardingCoin.WalletIdentifier, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No signer for wallet {boardingCoin.WalletIdentifier}");

        var precomputedData = commitmentPsbt.GetGlobalTransaction()
            .PrecomputeTransactionData(
                commitmentPsbt.Inputs.Select(i => i.GetTxOut()).ToArray());

        await PsbtHelpers.SignAndFillPsbt(
            signer, boardingCoin, commitmentPsbt,
            precomputedData, cancellationToken: cancellationToken);
    }

    signedCommitmentTx = commitmentPsbt.ToBase64();
}
```

Then update the submit call:

```csharp
if (signedForfeits.Count > 0 || signedCommitmentTx is not null)
{
    await clientTransport.SubmitSignedForfeitTxsAsync(
        new SubmitSignedForfeitTxsRequest(signedForfeits.ToArray(), signedCommitmentTx),
        cancellationToken);
}
```

**Step 3: Run tests**

Run: `dotnet test NArk.Tests -v n`
Expected: PASS

**Step 4: Commit**

```bash
git add -A && git commit -m "feat: sign commitment tx for boarding inputs in BatchSession"
```

---

## Task 7: Add boarding E2E test — full flow

**Files:**
- Create: `NArk.Tests.End2End/BoardingTests.cs`

**Step 1: Write the E2E test**

Reference `FulmineLiquidityHelper.cs` for the pattern of funding a boarding address. Reference `BatchSessionTests.cs` for the full batch flow pattern.

```csharp
using NArk.Core.Contracts;

namespace NArk.Tests.End2End;

[TestFixture]
public class BoardingTests
{
    [Test]
    public async Task CanBoardFromOnchainToVtxo()
    {
        // 1. Create wallet, get server info
        // 2. Derive a boarding contract (ArkBoardingContract)
        // 3. Get the P2TR boarding address
        // 4. Fund it via bitcoin-cli sendtoaddress
        // 5. Mine blocks to confirm
        // 6. Store the boarding UTXO as ArkVtxo with Unrolled=true
        // 7. Generate intent (via IntentGenerationService or manually)
        // 8. Run batch session
        // 9. Verify: VTXO appears in storage, boarding UTXO marked spent
    }

    [Test]
    public async Task BoardingWithUnconfirmedUtxo_FailsToRegister()
    {
        // Fund boarding address but DO NOT mine
        // Attempt to register intent
        // Assert: server rejects (UTXO not confirmed)
    }

    [Test]
    public async Task BoardingWithExpiredCsv_CanSweepOnchain()
    {
        // Fund boarding address, mine to confirm
        // Mine enough blocks to expire the CSV
        // Attempt to register intent — should fail
        // Sweep via unilateral exit leaf
        // Verify: new UTXO at fresh boarding address
    }
}
```

**Step 2: Run to verify it fails**

Run: `dotnet test NArk.Tests.End2End --filter "BoardingTests" -v n`
Expected: FAIL (tests not fully implemented yet, or infrastructure not set up)

**Step 3: Implement the tests**

Use the existing test infrastructure:
- `SharedArkInfrastructure` for Docker setup
- `DockerHelper.Exec("bitcoin", ...)` for bitcoin-cli commands
- `DockerHelper.MineBlocks(n)` for block generation
- `InMemoryVtxoStorage`, `InMemoryContractStorage` for storage
- `SimpleSeedWallet` or `InMemoryWalletProvider` for wallet

**Step 4: Run tests**

Run: `dotnet test NArk.Tests.End2End --filter "BoardingTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add -A && git commit -m "test: add boarding E2E tests"
```

---

## Task 8: Add `BoardingUtxoSyncService` (NBXplorer tracking)

**Files:**
- Create: `NArk.Core/Services/BoardingUtxoSyncService.cs`
- Modify: `NArk.Core/Hosting/ServiceCollectionExtensions.cs` (register)

**Step 1: Write failing test**

Test that the sync service:
- Finds boarding contracts in `IContractStorage`
- Queries NBXplorer for UTXOs at those scripts
- Upserts confirmed UTXOs into `IVtxoStorage` with `Unrolled = true`
- Marks spent UTXOs accordingly

**Step 2: Implement**

```csharp
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace NArk.Core.Services;

public class BoardingUtxoSyncService
{
    // Depends on:
    // - IContractStorage (to find boarding contracts)
    // - IVtxoStorage (to store discovered UTXOs)
    // - NBXplorer ExplorerClient or equivalent (to query onchain UTXOs)
    // - ArkServerInfo (for boarding_exit_delay to compute ExpiresAt)

    // Poll loop:
    // 1. Get all contracts where Type == "Boarding"
    // 2. For each, derive the P2TR script
    // 3. Query NBXplorer for UTXOs at that script
    // 4. For confirmed UTXOs not yet in storage, upsert as ArkVtxo(Unrolled: true)
    // 5. For UTXOs in storage that are no longer onchain, mark spent
}
```

**Note:** The exact NBXplorer API integration depends on what's available in the codebase. Check `ChainTimeProvider` for the existing `ExplorerClient` usage pattern. If NBXplorer doesn't directly support UTXO queries by script, we may need to use the Esplora REST API instead (the `ExplorerUri` from `ArkNetworkConfig`).

**Step 3: Run tests and commit**

---

## Task 9: Onchain sweep service (safety net)

**Files:**
- Create: `NArk.Core/Services/IOnchainSweepHandler.cs`
- Create: `NArk.Core/Services/OnchainSweepService.cs`
- Modify: `NArk.Core/Hosting/ServiceCollectionExtensions.cs` (register)

**Step 1: Design the interface**

```csharp
public interface IOnchainSweepHandler
{
    Task<bool> HandleExpiredUtxo(ArkCoin expiredCoin, CancellationToken cancellationToken);
}
```

**Step 2: Implement default sweep handler**

The default handler:
1. Detects `Unrolled = true` VTXOs where `ExpiresAt` has passed
2. Builds a transaction spending via the unilateral exit leaf
3. Sends to a fresh boarding address
4. Broadcasts via NBXplorer/Esplora

**Step 3: Write tests**

Test that:
- Expired unrolled VTXOs trigger sweep
- Non-expired VTXOs are not swept
- Custom `IOnchainSweepHandler` overrides default behavior

**Step 4: Commit**

```bash
git add -A && git commit -m "feat: add onchain sweep service for expired boarding/unrolled UTXOs"
```

---

## Task 10: Run full test suite, open PR

**Step 1: Run all unit tests**

Run: `dotnet test NArk.Tests -v n`
Expected: ALL PASS

**Step 2: Run all E2E tests**

Run: `dotnet test NArk.Tests.End2End -v n`
Expected: ALL PASS

**Step 3: Push and open PR**

```bash
git push -u origin feat/boarding
gh pr create --title "feat: boarding support" --body "$(cat <<'EOF'
## Summary
- Add `ArkBoardingContract` for boarding address script construction
- Add `BoardingContractTransformer` for boarding VTXO → ArkCoin conversion
- Add `Unrolled` property to `ArkCoin`, update `RequiresForfeit()` to skip forfeits for boarding/unrolled coins
- Update `BatchSession` to sign commitment tx for boarding inputs
- Add `SubmitSignedForfeitTxsRequest.SignedCommitmentTx` field
- Add `BoardingUtxoSyncService` for NBXplorer-based onchain UTXO tracking
- Add `OnchainSweepService` for sweeping expired boarding/unrolled UTXOs
- Add boarding E2E tests

## Test plan
- [ ] Unit tests: boarding contract construction, transformer, ArkCoin.Unrolled behavior
- [ ] Integration tests: batch session with boarding inputs
- [ ] E2E tests: full boarding flow (fund → confirm → settle → VTXO)
- [ ] E2E tests: expired boarding sweep
- [ ] CI passes
EOF
)"
```

**Step 4: Wait for CI, iterate on failures**

Check CI status:
```bash
gh pr checks --watch
```

Fix any failures, commit, push, repeat until CI fully passes.
