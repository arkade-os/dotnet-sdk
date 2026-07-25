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
#
# Windows/PowerShell counterpart of generate-bindings.sh — keep both in sync.

$ErrorActionPreference = "Stop"

# $ErrorActionPreference only makes PowerShell cmdlets throw on failure — it does NOT stop the
# script if an external executable (npx, dotnet) exits non-zero, unlike bash's "set -e". Check
# $LASTEXITCODE after every native call so a failed compile/generate step actually aborts the
# script instead of silently continuing with stale output.
function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$step failed with exit code $LASTEXITCODE"
    }
}

Set-Location $PSScriptRoot

$SolDir = "Contracts/Sol"
$OutDir = "Contracts/Generated"
$Namespace = "NArk.Swaps.Evm.Contracts"
$BuildDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $BuildDir | Out-Null

try {
    Copy-Item -Path (Join-Path $SolDir "*") -Destination $BuildDir -Recurse -Force

    Push-Location $BuildDir
    try {
        Write-Host "Compiling ERC20Swap.sol with solc..."
        # solc names output files after the (sanitized) input path it was given, so run from
        # inside $BuildDir and pass a bare relative filename — an absolute path produces an
        # unpredictable output filename.
        npx --yes solc@0.8.33 --abi -o out ERC20Swap.sol
        Assert-LastExitCode "solc ERC20Swap.sol"

        Write-Host "Compiling Router.sol with solc..."
        npx --yes solc@0.8.33 --abi -o out Router.sol
        Assert-LastExitCode "solc Router.sol"

        Write-Host "Compiling IERC20.sol with solc..."
        npx --yes solc@0.8.33 --abi -o out "@openzeppelin/contracts/token/ERC20/IERC20.sol"
        Assert-LastExitCode "solc IERC20.sol"
    }
    finally {
        Pop-Location
    }

    Write-Host "Generating C# bindings..."
    # NOT "dotnet run tools/AbiGen.cs" — with NArk.Swaps.Evm.csproj sitting right here in cwd,
    # `dotnet run` treats the .cs path as an argument to that ambient project instead of as a
    # file-based app target. Plain `dotnet <file>.cs` is what actually invokes it directly.
    #
    # Each call kept on a single line on purpose — PowerShell's backtick line-continuation
    # silently breaks if any trailing whitespace sneaks in after the backtick, so it's not
    # worth the risk here.
    $erc20SwapAbi = Join-Path $BuildDir "out/ERC20Swap_sol_ERC20Swap.abi"
    $routerAbi = Join-Path $BuildDir "out/Router_sol_Router.abi"
    $ierc20Abi = Join-Path $BuildDir "out/@openzeppelin_contracts_token_ERC20_IERC20_sol_IERC20.abi"

    dotnet tools/AbiGen.cs -- $erc20SwapAbi $OutDir $Namespace
    Assert-LastExitCode "AbiGen (ERC20Swap)"
    dotnet tools/AbiGen.cs -- $routerAbi "$OutDir/Router" "$Namespace.Router"
    Assert-LastExitCode "AbiGen (Router)"
    dotnet tools/AbiGen.cs -- $ierc20Abi "$OutDir/Erc20" "$Namespace.Erc20"
    Assert-LastExitCode "AbiGen (IERC20)"

    Write-Host "Done. Review the diff in $OutDir before committing."
}
finally {
    Remove-Item -Path $BuildDir -Recurse -Force -ErrorAction SilentlyContinue
}
