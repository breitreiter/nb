#!/bin/bash
set -uo pipefail
# Note: -e removed to allow individual test failures without stopping the script

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NB_DIR="$(dirname "$SCRIPT_DIR")/bin/Debug/net10.0"
NB="$NB_DIR/nb"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
DIM='\033[2m'
NC='\033[0m'

PASSED=0
FAILED=0

# Mock-provider config, passed to every invocation via --config (hermetic: the
# real bin/appsettings.json is ignored, never mutated). The run_test* helpers
# inject "--config $MOCK_CONFIG" right after the binary, so tests just pass "$NB".
MOCK_CONFIG="$SCRIPT_DIR/test-appsettings.json"

# Run a test (always redirects stdin from /dev/null, runs from NB_DIR)
# Args: test_name expected_exit_code command...
run_test() {
    local name="$1"
    local expected_exit="$2"
    shift 2

    local output
    local actual_exit=0

    output=$(cd "$NB_DIR" && "$1" --config "$MOCK_CONFIG" "${@:2}" < /dev/null 2>&1) || actual_exit=$?

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

    output=$(cd "$NB_DIR" && "$1" --config "$MOCK_CONFIG" "${@:2}" < /dev/null 2>&1) || actual_exit=$?

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

# Assert a stdout-only substring (stderr discarded). For porcelain, where the
# parseable contract lives on stdout and chrome is on stderr.
# Args: test_name expected_substring command...
run_test_stdout_contains() {
    local name="$1"
    local expected="$2"
    shift 2

    local out
    out=$(cd "$NB_DIR" && "$1" --config "$MOCK_CONFIG" "${@:2}" < /dev/null 2>/dev/null)

    if [[ "$out" == *"$expected"* ]]; then
        echo -e "${GREEN}PASS${NC}: $name"
        PASSED=$((PASSED + 1))
        return 0
    else
        echo -e "${RED}FAIL${NC}: $name"
        echo "  Expected stdout to contain: $expected"
        echo "  Output: ${out:0:300}"
        FAILED=$((FAILED + 1))
        return 1
    fi
}

# Validate --output jsonl: stdout alone must be well-formed JSONL, and a slurped
# jq filter (input is the array of events) must equal the expected value. stderr
# is discarded, so this asserts the "clean stdout" contract.
# Args: test_name jq_filter expected_value command...
run_test_jsonl_stdout() {
    local name="$1"
    local filter="$2"
    local expected="$3"
    shift 3

    local out
    out=$(cd "$NB_DIR" && "$1" --config "$MOCK_CONFIG" "${@:2}" < /dev/null 2>/dev/null)

    if ! echo "$out" | jq . >/dev/null 2>&1; then
        echo -e "${RED}FAIL${NC}: $name (stdout is not valid JSONL)"
        echo "  Output: ${out:0:200}"
        FAILED=$((FAILED + 1))
        return 1
    fi

    local got
    got=$(echo "$out" | jq -rs "$filter" 2>/dev/null)
    if [[ "$got" == "$expected" ]]; then
        echo -e "${GREEN}PASS${NC}: $name"
        PASSED=$((PASSED + 1))
        return 0
    else
        echo -e "${RED}FAIL${NC}: $name"
        echo "  jq '$filter' => '$got', expected '$expected'"
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

echo -e "${DIM}nb: $NB --config $MOCK_CONFIG${NC}"
echo ""

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

# ? is no longer intercepted — it goes to the LLM as a normal prompt

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

echo ""
echo "--- Transcript Schema (--output jsonl / --seed) ---"
echo ""

# jsonl stdout is clean and the answer is extractable (retires the brace-scan)
run_test_jsonl_stdout \
    "jsonl emit: answer extractable from stdout" \
    '[.[]|select(.type=="assistant_text").text]|last' \
    "OK" \
    "$NB" --output jsonl "any prompt"

# first emitted event is the system message
run_test_jsonl_stdout \
    "jsonl emit: leads with a system event" \
    '.[0].type' \
    "system" \
    "$NB" --output jsonl "any prompt"

# result trailer is present and last
run_test_jsonl_stdout \
    "jsonl emit: ends with a result trailer" \
    '.[-1].type' \
    "result" \
    "$NB" --output jsonl "any prompt"

# --seed replays fabricated history: both seeded user turns + the new one = 2 users
run_test_jsonl_stdout \
    "seed: fabricated turns become premise" \
    '[.[]|select(.type=="user")]|length' \
    "2" \
    "$NB" --seed "$SCRIPT_DIR/fixtures/seed-basic.jsonl" --output jsonl "and 3+3?"

# --seed carries the fabricated assistant answer forward
run_test_jsonl_stdout \
    "seed: fabricated assistant turn preserved" \
    '[.[]|select(.type=="assistant_text").text]|any(.=="4")' \
    "true" \
    "$NB" --seed "$SCRIPT_DIR/fixtures/seed-basic.jsonl" --output jsonl "and 3+3?"

# an invalid seed (orphan tool_result) fails fast with exit 1
run_test \
    "seed: orphan tool_result rejected" \
    1 \
    "$NB" --seed "$SCRIPT_DIR/fixtures/seed-orphan.jsonl" "go"

# a missing seed file fails fast with exit 1
run_test \
    "seed: missing file rejected" \
    1 \
    "$NB" --seed /nonexistent/seed.jsonl "go"

echo ""
echo "--- Exit-code contract (Phase 0) ---"
echo ""

# a clean single-shot run reports success in $?
run_test \
    "exit code: ok run exits 0" \
    0 \
    "$NB" "any prompt"

# a mid-turn provider failure surfaces as exit 2 (was silently 0)
run_test \
    "exit code: provider error exits 2" \
    2 \
    "$NB" "MOCK:throw"

# and the jsonl trailer names the reason
run_test_jsonl_stdout \
    "exit reason: provider error tagged in trailer" \
    '.[-1].exit_reason' \
    "provider_error" \
    "$NB" --output jsonl "MOCK:throw"

echo ""
echo "--- Non-TTY approval (Phase 0) ---"
echo ""

# A tool needing approval, run headless (stdin is /dev/null) with no --trust,
# is denied by policy — not a thrown ReadKey. The turn completes (exit 0) and
# the model gets a structured denial it can route around.
run_test_jsonl_stdout \
    "non-tty approval: unapproved bash is denied, not hung" \
    '[.[]|select(.type=="tool_result").output]|last|contains("non-interactive")' \
    "true" \
    "$NB" --output jsonl "MOCK:tool=bash touch approval_probe.txt"

run_test \
    "non-tty approval: denied turn still exits 0" \
    0 \
    "$NB" "MOCK:tool=bash touch approval_probe.txt"

# --trust lifts the denial for a sandbox-safe command: it executes instead.
run_test_jsonl_stdout \
    "non-tty approval: --trust auto-approves the same call" \
    '[.[]|select(.type=="tool_result").output]|last|contains("non-interactive")' \
    "false" \
    "$NB" --output jsonl --trust "MOCK:tool=bash touch approval_probe.txt"

rm -f "$NB_DIR/approval_probe.txt"

echo ""
echo "--- Porcelain output (Phase 1) ---"
echo ""

# A plain answer: stdout carries the answer (chrome is on stderr).
run_test_stdout_contains \
    "porcelain: plain answer on stdout" \
    "hello world" \
    "$NB" --output porcelain "MOCK:response=hello world"

# A tool round emits a stable TOOL line on stdout.
run_test_stdout_contains \
    "porcelain: tool call becomes a TOOL line" \
    "TOOL bash echo hi" \
    "$NB" --output porcelain --trust "MOCK:tool=bash echo hi"

# The headline: a fenced answer passes through verbatim, so sed extracts it.
FENCE_RESP=$'answer:\n```json\n{"root_cause":"timeout"}\n```'
run_test_stdout_contains \
    "porcelain: fenced answer is verbatim (sed-extractable)" \
    '```json' \
    "$NB" --output porcelain "MOCK:response=$FENCE_RESP"

echo ""
echo "--- Conversation-program evaluator (Phase 3.3) ---"
echo ""

# A source-syntax program: system directive + run produce the transcript.
run_test_jsonl_stdout \
    "program: source syntax runs the directives" \
    '[.[]|select(.type=="assistant_text").text]|last' \
    "hi there" \
    "$NB" --program "$SCRIPT_DIR/fixtures/prog-basic.nb"

# A bare program gets NO system message (no persona injected).
run_test_jsonl_stdout \
    "program: bare program has no system message" \
    '[.[]|select(.type=="system")]|length' \
    "0" \
    "$NB" --program "$SCRIPT_DIR/fixtures/prog-bare.nb"

# Mid-stream model swap: two runs, two models, one invocation.
run_test_jsonl_stdout \
    "program: model swaps between runs" \
    '[.[]|select(.type=="assistant_text").text]|join(",")' \
    "alpha,beta" \
    "$NB" --program "$SCRIPT_DIR/fixtures/prog-swap.nb" --output jsonl

# A program whose first line is '{' is read as jsonl bytecode.
run_test_jsonl_stdout \
    "program: jsonl bytecode is sniffed and evaluated" \
    '[.[]|select(.type=="assistant_text").text]|last' \
    "from bytecode" \
    "$NB" --program "$SCRIPT_DIR/fixtures/prog.jsonl"

# An invalid program fails fast with exit 1.
run_test \
    "program: invalid directive exits 1" \
    1 \
    "$NB" --program "$SCRIPT_DIR/fixtures/prog-bad.nb"

echo ""

# ----------------------------------------
# Layer 3: LLM Eval Tests (uses real provider, LLM-as-judge)
# ----------------------------------------

# Check if --skip-llm flag was passed
SKIP_LLM=false
for arg in "$@"; do
    if [[ "$arg" == "--skip-llm" ]]; then
        SKIP_LLM=true
    fi
done

if [[ "$SKIP_LLM" == "true" ]]; then
    echo -e "${DIM}--- LLM Eval Tests (skipped via --skip-llm) ---${NC}"
    echo ""
else
    echo "--- LLM Eval Tests ---"
    echo ""

    # Run an LLM eval test
    # Args: test_name prompt criteria
    run_llm_eval() {
        local name="$1"
        local prompt="$2"
        local criteria="$3"

        echo -e "${DIM}  Running: $name...${NC}"

        # Run nb with the test prompt, capture output
        local transcript
        transcript=$(cd "$NB_DIR" && "$NB" "$prompt" < /dev/null 2>&1) || true

        # Build eval prompt
        local eval_prompt="Criteria: $criteria

Transcript:
User: $prompt
Assistant: $transcript

Did the assistant meet the criteria?"

        # Run judge
        local verdict
        verdict=$(cd "$NB_DIR" && "$NB" --system "$SCRIPT_DIR/judge.md" "$eval_prompt" < /dev/null 2>&1) || true

        # Check for PASS (can appear anywhere in the verdict)
        if [[ "$verdict" == *"PASS:"* ]] || [[ "$verdict" == *"PASS "* ]]; then
            echo -e "${GREEN}PASS${NC}: $name"
            PASSED=$((PASSED + 1))
            return 0
        elif [[ "$verdict" == *"FAIL:"* ]] || [[ "$verdict" == *"FAIL "* ]]; then
            echo -e "${RED}FAIL${NC}: $name"
            echo -e "${DIM}  Transcript: ${transcript:0:200}${NC}"
            echo -e "${DIM}  Verdict: ${verdict:0:300}${NC}"
            FAILED=$((FAILED + 1))
            return 1
        else
            echo -e "${YELLOW}UNCLEAR${NC}: $name (no PASS/FAIL in verdict)"
            echo -e "${DIM}  Verdict: ${verdict:0:300}${NC}"
            FAILED=$((FAILED + 1))
            return 1
        fi
    }

    # Test: Model should answer simple math directly without using tools
    run_llm_eval \
        "answers math without tools" \
        "What is 2+2? Reply with just the number." \
        "The model should answer with the number 4 without calling any tools like bash"

fi

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
