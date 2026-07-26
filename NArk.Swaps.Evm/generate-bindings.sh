#!/usr/bin/env bash
# Regenerates NArk.Swaps.Evm/Contracts/Generated/ from the vendored Contracts/Sol/ (plain
# committed copies of BoltzExchange/boltz-core's contracts + the handful of permit2/
# OpenZeppelin leaf interfaces Router.sol/TestERC20.sol need — no submodules, matching the
# proto-sync-check.yml convention elsewhere in this repo). Run this after updating
# Contracts/Sol/ contents from upstream. CI (see .github/workflows/evm-bindings-drift.yml)
# diffs the vendored contracts against boltz-core's/permit2's/OpenZeppelin's master and fails
# if they've drifted — that's the signal to pull the new version(s), re-run this script, and
# review the diff before committing.
#
# Generates from ERC20Swap.sol (the contract this milestone's provider actually calls),
# Router.sol (Milestone 4's USDT/generic-ERC20 DEX-hop — executeAndLockERC20WithPermit2 /
# claimERC20Execute), IERC20.sol (generic ERC20 calls, e.g. approve), TestERC20.sol (a
# deployable bytecode + typed constructor, for local Anvil test fixtures that need their own
# throwaway token — not used by the production provider, which only ever calls already-deployed
# contracts), and test-fixtures/MockErc20Dex.sol (Milestone 4's DEX-hop test double — vendored
# verbatim from BoltzExchange/boltz-core's own Router.sol test suite, see that file's header).
# EtherSwap.sol is vendored for reference but not generated (native-ETH sibling contract, not
# used by our ERC20/tBTC flow). Permit2 itself is never compiled from source here — see
# Contracts/Sol/permit2/test/utils/DeployPermit2.sol (vendored verbatim from Uniswap/permit2):
# tests deploy the real canonical Permit2 runtime bytecode via Anvil's anvil_setCode instead
# (same technique that file's vm.etch() uses under Foundry), so only its interface
# (permit2/interfaces/*.sol) needs to compile against Router.sol.
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
(cd "$BUILD_DIR" && npx --yes solc@0.8.33 --abi --bin -o out ERC20Swap.sol)

echo "Compiling Router.sol with solc..."
(cd "$BUILD_DIR" && npx --yes solc@0.8.33 --abi --bin -o out Router.sol)

echo "Compiling IERC20.sol with solc..."
(cd "$BUILD_DIR" && npx --yes solc@0.8.33 --abi -o out "@openzeppelin/contracts/token/ERC20/IERC20.sol")

echo "Compiling TestERC20.sol with solc..."
(cd "$BUILD_DIR" && npx --yes solc@0.8.33 --abi --bin -o out TestERC20.sol)

echo "Compiling MockErc20Dex.sol with solc..."
(cd "$BUILD_DIR" && npx --yes solc@0.8.33 --abi --bin -o out test-fixtures/MockErc20Dex.sol)

echo "Generating C# bindings..."
# NOT "dotnet run tools/AbiGen.cs" — with NArk.Swaps.Evm.csproj sitting right here in cwd,
# `dotnet run` treats the .cs path as an argument to that ambient project instead of as a
# file-based app target. Plain `dotnet <file>.cs` is what actually invokes it directly.
dotnet tools/AbiGen.cs -- \
    "$BUILD_DIR/out/ERC20Swap_sol_ERC20Swap.abi" "$OUT_DIR" "$NAMESPACE" \
    "$BUILD_DIR/out/ERC20Swap_sol_ERC20Swap.bin" ERC20Swap
dotnet tools/AbiGen.cs -- \
    "$BUILD_DIR/out/Router_sol_Router.abi" "$OUT_DIR/Router" "$NAMESPACE.Router" \
    "$BUILD_DIR/out/Router_sol_Router.bin" Router
dotnet tools/AbiGen.cs -- \
    "$BUILD_DIR/out/@openzeppelin_contracts_token_ERC20_IERC20_sol_IERC20.abi" "$OUT_DIR/Erc20" "$NAMESPACE.Erc20"
dotnet tools/AbiGen.cs -- \
    "$BUILD_DIR/out/TestERC20_sol_TestERC20.abi" "$OUT_DIR/Test" "$NAMESPACE.Test" \
    "$BUILD_DIR/out/TestERC20_sol_TestERC20.bin" TestERC20
dotnet tools/AbiGen.cs -- \
    "$BUILD_DIR/out/test-fixtures_MockErc20Dex_sol_MockERC20Dex.abi" "$OUT_DIR/TestFixtures" "$NAMESPACE.TestFixtures" \
    "$BUILD_DIR/out/test-fixtures_MockErc20Dex_sol_MockERC20Dex.bin" MockERC20Dex

echo "Extracting Permit2's canonical address + runtime bytecode from the vendored DeployPermit2.sol..."
# Pulled straight from the vendored file rather than retyped by hand: DeployPermit2.sol's own
# deployPermit2() body is the single source of truth for both values, so this can't drift from
# it (and re-running this script after an upstream update, per evm-bindings-drift.yml, keeps it
# current). Do not hardcode this address from memory elsewhere — see the comment in the
# generated file for why.
PERMIT2_SOL="$SOL_DIR/permit2/test/utils/DeployPermit2.sol"
PERMIT2_ADDRESS=$(grep -oE '0x[0-9A-Fa-f]{40}' "$PERMIT2_SOL" | head -1)
PERMIT2_BYTECODE=$(grep -o 'hex"[0-9a-fA-F]*"' "$PERMIT2_SOL" | sed -E 's/^hex"//; s/"$//')
if [ -z "$PERMIT2_ADDRESS" ] || [ -z "$PERMIT2_BYTECODE" ]; then
    echo "Failed to extract Permit2 address/bytecode from $PERMIT2_SOL — has its format changed upstream?" >&2
    exit 1
fi
mkdir -p "$OUT_DIR/Permit2"
cat > "$OUT_DIR/Permit2/Permit2Deployment.cs" <<EOF
// <auto-generated>
//     Extracted by generate-bindings.sh from the vendored Contracts/Sol/permit2/test/utils/
//     DeployPermit2.sol (Uniswap/permit2) — that file's deployPermit2() vm.etch()'s this same
//     runtime bytecode at this same address under Foundry; RouterDexHopTests.cs does the
//     equivalent against a plain Anvil node via the anvil_setCode RPC instead (see
//     Permit2TestDeployment.DeployAsync). Do not hand-edit or hand-copy this value elsewhere —
//     re-run generate-bindings.sh instead.
// </auto-generated>
#pragma warning disable CS1591
namespace ${NAMESPACE}.Permit2
{
    public static class Permit2Deployment
    {
        public const string CanonicalAddress = "${PERMIT2_ADDRESS}";
        public const string RuntimeBytecodeHex = "${PERMIT2_BYTECODE}";
    }
}
EOF
echo "Generated Permit2Deployment.cs (address ${PERMIT2_ADDRESS})"

echo "Done. Review the diff in $OUT_DIR before committing."
