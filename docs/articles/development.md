# Development

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for libraries)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (for tests)
- [Docker](https://docs.docker.com/get-docker/) (for the E2E regtest stack)
- Bash/WSL on Windows (the regtest scripts are POSIX shell)

## Building

```bash
git clone --recurse-submodules https://github.com/arkade-os/dotnet-sdk.git
cd dotnet-sdk
dotnet build
```

## Running Tests

### Unit Tests

```bash
dotnet test NArk.Tests
```

### End-to-End Tests

E2E tests require a running regtest stack (bitcoin core + nbxplorer + mempool + arkd). The stack lives in the `regtest/` submodule ([ArkLabsHQ/arkade-regtest](https://github.com/ArkLabsHQ/arkade-regtest)) and is driven by its Node CLI:

```bash
# From the repo root:
node regtest/regtest.mjs start --profile ark --clean
dotnet test NArk.Tests.End2End
node regtest/regtest.mjs clean     # tear down containers and volumes when done
```

Profiles let you start only the tier a suite needs. `ark` (bitcoind + nbxplorer + mempool +
arkd) is enough for most of the suite, because that is where the faucet lives. Add a profile
only for the service you are actually exercising:

- `delegate` — fulmine-delegator, for `DelegationTests`
- `emulator` — the covenant (`ArkadeScript`) suite co-signs against the emulator
- `boltz` — Boltz and the Lightning corridors; nothing else needs it

Useful commands:

- `node regtest/regtest.mjs mine [n]` — mine regtest blocks
- `node regtest/regtest.mjs faucet <address> <amountBtc> [--confirm]` — send on-chain BTC
- `node regtest/regtest.mjs rpc <args...>` — `bitcoin-cli` passthrough
- `node regtest/regtest.mjs stop` — stop without wiping data

> [!IMPORTANT]
> E2E tests run sequentially (`[assembly: NonParallelizable]`) because they share a single arkd instance.

> [!NOTE]
> Tests get their VTXOs from `ArkadeFaucet`, which spends the `ark` client wallet that arkade-regtest seeds with 1 BTC inside the arkd container at start-up. That wallet comes with any profile that starts arkd, so funding costs no extra services. Fulmine still runs under the `delegate` profile, but only as the delegator the delegation suite exercises.

## Project Structure

```
dotnet-sdk/
├── NArk.Abstractions/     # Interfaces, domain types, vendored NBitcoin.Scripting
├── NArk.Core/             # Core services and transport
├── NArk.Storage.EfCore/   # EF Core persistence (opt-in payment tracking)
├── NArk/                  # Meta-package
├── NArk.Tests/            # Unit tests
├── NArk.Tests.End2End/    # E2E tests (require the regtest stack)
├── regtest/               # Docker-compose overlay + start/stop scripts
├── samples/
│   └── NArk.Wallet/       # Blazor WASM sample wallet
└── docs/                  # Documentation (DocFX)
```

## Building Documentation

```bash
dotnet tool restore
dotnet docfx docfx.json                # Build
dotnet docfx docfx.json --serve        # Build + serve at localhost:8080
```

## Publishing

NuGet packages are published automatically by CI when pushing to `master` or creating a version tag. Each package is tagged independently as `{PackageName}/{Version}` (e.g. `NArk.Core/1.0.250`).
