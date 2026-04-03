# Development

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for libraries)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (for tests)
- [Docker](https://docs.docker.com/get-docker/) (for E2E tests)

## Building

```bash
git clone --recurse-submodules https://github.com/arkade-os/dotnet-sdk.git
cd dotnet-sdk
dotnet build
```

## Running Tests

### Unit Tests

```bash
dotnet test --filter "FullyQualifiedName!~End2End"
```

### End-to-End Tests

E2E tests require a regtest environment:

```bash
./NArk.Tests.End2End/Infrastructure/start-env.sh --clean
dotnet test NArk.Tests.End2End
```

> [!IMPORTANT]
> E2E tests must run sequentially — they share a single arkd instance.

## Project Structure

```
dotnet-sdk/
├── NArk.Abstractions/     # Interfaces and domain types
├── NArk.Core/             # Core services and transport
├── NArk.Swaps/            # Boltz swap integration
├── NArk.Storage.EfCore/   # EF Core persistence
├── NArk/                  # Meta-package
├── NArk.Tests/            # Unit tests
├── NArk.Tests.End2End/    # E2E tests with nigiri
├── samples/
│   └── NArk.Wallet/       # Blazor WASM sample wallet
└── docs/                  # Documentation (DocFX)
```

## Building Documentation

```bash
dotnet tool install -g docfx
docfx docfx.json                # Build
docfx docfx.json --serve        # Build + serve at localhost:8080
```

## Publishing

NuGet packages are published automatically by CI when pushing to `master` or creating a version tag. Each package is tagged independently as `{PackageName}/{Version}`.
