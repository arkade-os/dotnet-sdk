# NArk .NET SDK (Arkade)

## Branding & Vocabulary

> **NEVER rename "Arkade" → "Ark" or "batch" → "round" anywhere in this repo.** These are the brand and protocol terminology and recurring AI regressions on PRs. If you see existing "Ark" or "round" usages in user-facing text or docs, default to fixing them, not propagating them.

- The protocol/product brand is **Arkade** (not "Ark" on its own). ALWAYS use "Arkade" in user-facing text, docs, comments, log messages, exception messages, and code XML doc comments. Examples that must be "Arkade": `Arkade transaction`, `Arkade server`, `Arkade address`, `Arkade-issued asset`, `Arkade VTXO tree`, `Lightning↔Arkade`, `BTC↔Arkade`, `Arkade intent`. The only acceptable bare "Ark" is inside compound code identifiers (see next bullet).
- The protocol's settlement primitive is a **batch** (not a "round"). ALWAYS use "batch" in user-facing text, docs, comments, log/exception messages, and code XML doc comments. "Batch round" is wrong — it's just "batch." Examples that must be "batch": `batch settlement`, `next batch`, `batch participation`, `batch session`, `post-batch poll`. The only place a separate "Round" word is acceptable is when transcribing an external system's wire-protocol field name verbatim (e.g. arkd's `RoundId` if it exists in the proto).
- Code identifiers (namespaces, classes, variables, file/folder names) use `Ark` / `NArk` for brevity — this is fine and should not be renamed: `ArkContract`, `ArkVtxo`, `ArkSwap`, `NArk.Core`, `IArkadeAddressProvider`, etc. Do NOT rename these to `Arkade*` — they're just the project shorthand.

## Documentation

> **Every PR that adds, removes, or changes any public surface MUST update**:
> 1. XML doc comments on the touched API
> 2. README.md usage section (or add one) with a minimal example
> 3. The corresponding article under `docs/articles/` (and `toc.yml` if a new article)
> 4. The Blazor sample wallet under `samples/NArk.Wallet/` if the change is something an end-user app would invoke
>
> Treat docs as part of the change, not a follow-up. PRs that ship API changes without doc updates will fail review. The same rule applies to commits that land on `master` directly.

### Inline Doc Comments
- When adding or modifying any public API (class, method, property, enum), ALWAYS add or update the XML doc comment (`<summary>`, `<param>`, `<returns>`, `<remarks>` as appropriate).
- When changing behavior of an existing public API, update its doc comment to reflect the new behavior.
- Do not add doc comments to private/internal implementation details unless the logic is non-obvious.

### Generated Documentation
- This repo uses DocFX to generate API reference docs, deployed to GitHub Pages via `.github/workflows/docs.yml`.
- When adding new public types or significantly changing existing ones, verify the docs build still succeeds: `dotnet tool restore && dotnet docfx docfx.json`.
- When adding a new feature area, add a conceptual article in `docs/articles/` and register it in `docs/articles/toc.yml`.
- Keep `docs/articles/` content in sync with actual SDK capabilities — if you add, remove, or rename a feature, update the corresponding article.

### README
- When adding any new feature, public API, or significant behavior change, ALWAYS add usage instructions to the README.md with code examples showing how to use the feature.
- README sections should include: what the feature does, how to set it up (DI registration), and a minimal code example.

### Sample Wallet
- The Blazor sample wallet at `samples/NArk.Wallet/` is the user-facing reference for how a real app consumes the SDK. If a change touches a code path the sample wallet exercises (wallet import, send/receive, swaps, asset issuance, recovery, etc.), update the sample to demonstrate the new behaviour or surface the new option.
- Keep the sample buildable and runnable as a smoke check; if the new API needs DI registration, wire it up there too.

### Pre-Merge Checklist
Before opening a PR or pushing to master:
- [ ] All new/changed public APIs have XML doc comments
- [ ] README has a usage section for any new feature (with a minimal example)
- [ ] `docs/articles/` is in sync; new feature areas have a new article + TOC entry
- [ ] DocFX build succeeds locally (`dotnet tool restore && dotnet docfx docfx.json`)
- [ ] Sample wallet still builds and demonstrates the change if applicable

## Project Structure
- `NArk.Abstractions` — Interfaces and base types (no implementation)
- `NArk.Core` — Protocol implementation (wallets, spending, contracts, transport)
- `NArk.Swaps` — Swap providers (Boltz) and swap management
- `NArk.Storage.EfCore` — Entity Framework Core persistence
- `NArk.Tests` — Unit tests
- `NArk.Tests.End2End` — E2E integration tests (require nigiri + arkd)

## Testing
- Unit tests: `dotnet test NArk.Tests`
- E2E tests require external infrastructure (arkd, nigiri, boltz). Do not run locally without setup.
- NEVER skip or disable failing tests to make CI pass. Fix the root cause.

## Build
- Target framework: `net8.0` for libraries, `net10.0` for test projects
- CI: `.github/workflows/build.yml` (build + unit tests + E2E)
