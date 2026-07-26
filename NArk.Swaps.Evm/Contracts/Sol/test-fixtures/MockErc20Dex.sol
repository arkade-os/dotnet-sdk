// SPDX-License-Identifier: MIT

pragma solidity ^0.8.33;

// Vendored (contract body copied verbatim, byte-for-byte) from BoltzExchange/boltz-core's
// contracts/test/RouterTestBase.sol — this is Boltz's OWN test double for Router.sol's
// executeAndLockERC20/claimERC20Execute DEX-hop path (see e.g. RouterTest.sol's
// testClaimERC20ExecuteSwapTokens: calls = [TOKEN.approve(dex, amount), dex.swap(amount)]).
// Reused here for the exact same purpose: proving our own Permit2/Router calldata plumbing
// against a real Router+real Permit2 without needing a real DEX deployment. Production code
// targets a real DEX (Uniswap V3) instead — see DEXSwapService.
//
// Tracked in CI (.github/workflows/evm-bindings-drift.yml) against boltz-core master so this
// stays honest if their own test double's behavior ever changes.
import {TestERC20} from "../TestERC20.sol";

contract MockERC20Dex {
    TestERC20 internal immutable INPUT_TOKEN;
    TestERC20 internal immutable OUTPUT_TOKEN;

    constructor(TestERC20 inputToken, TestERC20 outputToken) {
        INPUT_TOKEN = inputToken;
        OUTPUT_TOKEN = outputToken;
    }

    function swap(uint256 amount) public {
        require(INPUT_TOKEN.transferFrom(msg.sender, address(this), amount));
        require(OUTPUT_TOKEN.transfer(msg.sender, amount));
    }
}
