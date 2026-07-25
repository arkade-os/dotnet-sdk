#!/usr/bin/env bash
# Regenerates NArk.Swaps.Evm/Contracts/Generated/ from the vendored Contracts/Sol/ (plain
# committed copies of BoltzExchange/boltz-core's contracts + the handful of permit2/
# OpenZeppelin leaf interfaces Router.sol needs — no submodules, matching the
# proto-sync-check.yml convention elsewhere in this repo). Run this after updating
# Contracts/Sol/ contents from upstream. CI (see .github/workflows/evm-bindings-drift.yml)
# diffs the vendored contracts against boltz-core's/permit2's/OpenZeppelin's master and fails
# if they've drifted — that's the signal to pull the new version(s), re-run this script, and
# review the diff before committing.
#
# Generates from both ERC20Swap.sol (the contract this milestone's provider actually calls)
# and Router.sol (for the deferred USDT0-DEX-hop follow-up — executeAndLockERC20WithPermit2 /
# claimERC20ExecuteOft). EtherSwap.sol is vendored for reference but not generated (native-ETH
# sibling contract, not used by our ERC20/tBTC flow).
set -euo pipefail

cd "$(dirname "$0")"

SOL_DIR="Contracts/Sol"
OUT_DIR="Contracts/Generated"
NAMESPACE="NArk.Swaps.Evm.Contracts"
BUILD_DIR="$(mktemp -d)"
trap 'rm -rf "$BUILD_DIR"' EXIT

cp -R "$SOL_DIR/." "$BUILD_DIR/"

echo "Compiling ERC20Swap.sol with solc..."
# solc names output files after the (sanitized) input path it was given, so cd in and pass
# a bare relative filename — an absolute path produces an unpredictable output filename.
(cd "$BUILD_DIR" && npx --yes solc@0.8.33 --abi -o out ERC20Swap.sol)

echo "Compiling Router.sol with solc..."
(cd "$BUILD_DIR" && npx --yes solc@0.8.33 --abi -o out Router.sol)

echo "Compiling IERC20.sol with solc..."
(cd "$BUILD_DIR" && npx --yes solc@0.8.33 --abi -o out "@openzeppelin/contracts/token/ERC20/IERC20.sol")

echo "Generating C# bindings..."
# NOT "dotnet run tools/AbiGen.cs" — with NArk.Swaps.Evm.csproj sitting right here in cwd,
# `dotnet run` treats the .cs path as an argument to that ambient project instead of as a
# file-based app target. Plain `dotnet <file>.cs` is what actually invokes it directly.
dotnet tools/AbiGen.cs -- \
    "$BUILD_DIR/out/ERC20Swap_sol_ERC20Swap.abi" "$OUT_DIR" "$NAMESPACE"
dotnet tools/AbiGen.cs -- \
    "$BUILD_DIR/out/Router_sol_Router.abi" "$OUT_DIR/Router" "$NAMESPACE.Router"
dotnet tools/AbiGen.cs -- \
    "$BUILD_DIR/out/@openzeppelin_contracts_token_ERC20_IERC20_sol_IERC20.abi" "$OUT_DIR/Erc20" "$NAMESPACE.Erc20"

echo "Done. Review the diff in $OUT_DIR before committing."
