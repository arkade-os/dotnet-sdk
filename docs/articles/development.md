# Development

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for libraries)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (for tests)
- [Docker](https://docs.docker.com/get-docker/) (for the E2E regtest stack)
- [Node.js >= 18](https://nodejs.org/) (drives the arkade-regtest CLI; stdlib only — no `npm install`)

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

E2E tests require a running regtest stack (bitcoin core + arkd + wallet + boltz + fulmine + mempool/Fulcrum). The stack is managed by the arkade-regtest Node CLI under `regtest/` (Node >= 18, stdlib only — no `npm install`):

```bash
# From the repo root:
node regtest/regtest.mjs clean     # tear down + wipe volumes (run before a fresh start)
node regtest/regtest.mjs start     # start everything
dotnet test NArk.Tests.End2End
node regtest/regtest.mjs stop      # shut down when done
```

Useful commands:

- `node regtest/regtest.mjs start` — start/resume the stack
- `node regtest/regtest.mjs stop` — stop containers without wiping data
- `node regtest/regtest.mjs clean` — full teardown including data directories

> [!IMPORTANT]
> E2E tests run sequentially (`[assembly: NonParallelizable]`) because they share a single arkd instance.

> [!NOTE]
> The arkade-regtest stack includes boltz, boltz-lnd, boltz-fulmine, and nginx-boltz alongside bitcoin core, arkd, and the mempool/Fulcrum indexer. All test fixtures expect those services to be up. `SharedArkInfrastructure` and `SharedSwapInfrastructure` perform readiness probes against `/v1/info` (arkd) and boltz before running tests.

## Project Structure

```
dotnet-sdk/
├── NArk.Abstractions/     # Interfaces, domain types, vendored NBitcoin.Scripting
├── NArk.Core/             # Core services and transport
├── NArk.Swaps/            # Boltz swap integration
├── NArk.Storage.EfCore/   # EF Core persistence (opt-in payment tracking)
├── NArk/                  # Meta-package
├── NArk.Tests/            # Unit tests
├── NArk.Tests.End2End/    # E2E tests (require the regtest stack)
├── regtest/               # arkade-regtest submodule (Docker Compose stack + Node CLI)
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
