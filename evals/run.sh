#!/bin/bash
set -uo pipefail
# Note: -e removed to allow individual test failures without stopping the script

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NB_DIR="$(dirname "$SCRIPT_DIR")/bin/Debug/net8.0"
NB="$NB_DIR/nb"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
DIM='\033[2m'
NC='\033[0m'

PASSED=0
FAILED=0

# Test appsettings for mock provider
MOCK_CONFIG="$SCRIPT_DIR/test-appsettings.json"
ORIG_CONFIG="$NB_DIR/appsettings.json"
BACKUP_CONFIG="$NB_DIR/appsettings.backup.json"

setup_mock_provider() {
    cp "$ORIG_CONFIG" "$BACKUP_CONFIG"
    cp "$MOCK_CONFIG" "$ORIG_CONFIG"
}

restore_config() {
    if [[ -f "$BACKUP_CONFIG" ]]; then
        cp "$BACKUP_CONFIG" "$ORIG_CONFIG"
        rm "$BACKUP_CONFIG"
    fi
}

# Ensure cleanup on exit
trap restore_config EXIT

# Run a test (always redirects stdin from /dev/null, runs from NB_DIR)
# Args: test_name expected_exit_code command...
run_test() {
    local name="$1"
    local expected_exit="$2"
    shift 2

    local output
    local actual_exit=0

    output=$(cd "$NB_DIR" && "$@" < /dev/null 2>&1) || actual_exit=$?

    if [[ "$actual_exit" -eq "$expected_exit" ]]; then
        echo -e "${GREEN}PASS${NC}: $name"
        PASSED=$((PASSED + 1))
        return 0
    else
        echo -e "${RED}FAIL${NC}: $name"
        echo "  Expected exit: $expected_exit, got: $actual_exit"
        echo "  Output: ${output:0:200}"
        FAILED=$((FAILED + 1))
        return 1
    fi
}

# Run test checking output contains a string (runs from NB_DIR)
# Args: test_name expected_exit_code expected_substring command...
run_test_contains() {
    local name="$1"
    local expected_exit="$2"
    local expected_string="$3"
    shift 3

    local output
    local actual_exit=0

    output=$(cd "$NB_DIR" && "$@" < /dev/null 2>&1) || actual_exit=$?

    if [[ "$actual_exit" -eq "$expected_exit" ]] && [[ "$output" == *"$expected_string"* ]]; then
        echo -e "${GREEN}PASS${NC}: $name"
        PASSED=$((PASSED + 1))
        return 0
    else
        echo -e "${RED}FAIL${NC}: $name"
        echo "  Expected exit: $expected_exit, got: $actual_exit"
        echo "  Expected to contain: $expected_string"
        echo "  Output: ${output:0:300}"
        FAILED=$((FAILED + 1))
        return 1
    fi
}

echo "========================================"
echo "nb test suite"
echo "========================================"
echo ""

# Check nb exists
if [[ ! -x "$NB" ]]; then
    echo -e "${RED}ERROR${NC}: nb not found at $NB"
    echo "Run 'dotnet build' first"
    exit 1
fi

echo -e "${DIM}nb: $NB${NC}"
echo ""

# Use mock provider for all tests
setup_mock_provider

# ----------------------------------------
# Layer 1: Basic sanity tests
# ----------------------------------------
echo "--- Basic Sanity Tests ---"
echo ""

# Arg parsing: --system with missing file (exits before LLM call)
run_test_contains \
    "--system with missing file errors" \
    1 \
    "System prompt file not found" \
    "$NB" --system /nonexistent/file.md "test"

# Command interception: ? shows help (intercepted before LLM)
run_test_contains \
    "? shows help" \
    0 \
    "exit" \
    "$NB" "?"

# Command interception: /clear doesn't crash
run_test \
    "/clear command works" \
    0 \
    "$NB" "/clear"

echo ""

# ----------------------------------------
# Layer 2: Mock provider tests (fast, deterministic)
# ----------------------------------------
echo "--- Mock Provider Tests ---"
echo ""

# Mock returns default response
run_test_contains \
    "mock provider returns response" \
    0 \
    "OK" \
    "$NB" "any prompt"

# Mock with custom response instruction
run_test_contains \
    "mock respects MOCK:response instruction" \
    0 \
    "custom response here" \
    "$NB" "MOCK:response=custom response here"

# --system flag works with mock
run_test \
    "--system with mock provider" \
    0 \
    "$NB" --system "$SCRIPT_DIR/judge.md" "test"

# Config is restored by trap, but do it explicitly for clarity
restore_config

echo ""

# ----------------------------------------
# Summary
# ----------------------------------------
echo "========================================"
echo -e "Results: ${GREEN}$PASSED passed${NC}, ${RED}$FAILED failed${NC}"
echo "========================================"

if [[ "$FAILED" -gt 0 ]]; then
    exit 1
fi
