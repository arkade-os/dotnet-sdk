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

E2E tests require a running regtest stack (bitcoin core + arkd + wallet + boltz + fulmine + nigiri). The stack is managed by scripts under `regtest/`:

```bash
# From the repo root:
./regtest/start-env.sh --clean     # tear down + build + start everything fresh
dotnet test NArk.Tests.End2End
./regtest/stop-env.sh              # shut down when done
```

Useful flags:

- `./regtest/start-env.sh --clean` — wipe volumes and start clean (required on first run or after schema changes)
- `./regtest/start-env.sh` — start/resume without wiping data
- `./regtest/clean-env.sh` — full teardown including nigiri data directories (Docker handles permission-locked container volumes automatically)

> [!IMPORTANT]
> E2E tests run sequentially (`[assembly: NonParallelizable]`) because they share a single arkd instance.

> [!NOTE]
> The regtest overlay adds boltz, boltz-lnd, boltz-fulmine, and nginx-boltz on top of nigiri. All test fixtures expect those services to be up. `SharedArkInfrastructure` and `SharedSwapInfrastructure` perform readiness probes against `/v1/info` (arkd) and boltz before running tests.

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
