#!/bin/bash
set -uo pipefail
# Note: -e removed to allow individual test failures without stopping the script

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NB_DIR="$(dirname "$SCRIPT_DIR")/bin/Debug/net10.0"
NB="$NB_DIR/nb"
FIX="$SCRIPT_DIR/fixtures"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
DIM='\033[2m'
NC='\033[0m'

PASSED=0
FAILED=0

# Mock-provider config, passed to every invocation via --config (hermetic: the
# real bin/appsettings.json is ignored, never mutated).
MOCK_CONFIG="$SCRIPT_DIR/test-appsettings.json"

# nb is a program evaluator now: there is no bare-prompt mode. Every test feeds a
# conversation-program on stdin (nb reads a piped stdin as the program). A prompt
# becomes a one-line `run <prompt>` program; a fixture is piped verbatim. Extra
# flags after the program source are appended (a later --config wins over the mock).
#
#   run_prog                name expected_exit         "<program>" [flags...]
#   run_prog_contains       name expected_exit substr  "<program>" [flags...]
#   run_prog_stdout_contains name substr               "<program>" [flags...]   (stderr dropped)
#   run_prog_jsonl          name jq_filter expected     "<program>" [flags...]   (stdout must be jsonl)

run_prog() {
    local name="$1" expected_exit="$2" prog="$3"; shift 3
    local output actual_exit=0
    output=$(cd "$NB_DIR" && printf '%s\n' "$prog" | "$NB" --config "$MOCK_CONFIG" "$@" 2>&1) || actual_exit=$?
    if [[ "$actual_exit" -eq "$expected_exit" ]]; then
        echo -e "${GREEN}PASS${NC}: $name"; PASSED=$((PASSED + 1)); return 0
    fi
    echo -e "${RED}FAIL${NC}: $name"
    echo "  Expected exit: $expected_exit, got: $actual_exit"
    echo "  Output: ${output:0:200}"
    FAILED=$((FAILED + 1)); return 1
}

run_prog_contains() {
    local name="$1" expected_exit="$2" expected_string="$3" prog="$4"; shift 4
    local output actual_exit=0
    output=$(cd "$NB_DIR" && printf '%s\n' "$prog" | "$NB" --config "$MOCK_CONFIG" "$@" 2>&1) || actual_exit=$?
    if [[ "$actual_exit" -eq "$expected_exit" ]] && [[ "$output" == *"$expected_string"* ]]; then
        echo -e "${GREEN}PASS${NC}: $name"; PASSED=$((PASSED + 1)); return 0
    fi
    echo -e "${RED}FAIL${NC}: $name"
    echo "  Expected exit: $expected_exit, got: $actual_exit"
    echo "  Expected to contain: $expected_string"
    echo "  Output: ${output:0:300}"
    FAILED=$((FAILED + 1)); return 1
}

run_prog_stdout_contains() {
    local name="$1" expected="$2" prog="$3"; shift 3
    local out
    out=$(cd "$NB_DIR" && printf '%s\n' "$prog" | "$NB" --config "$MOCK_CONFIG" "$@" 2>/dev/null)
    if [[ "$out" == *"$expected"* ]]; then
        echo -e "${GREEN}PASS${NC}: $name"; PASSED=$((PASSED + 1)); return 0
    fi
    echo -e "${RED}FAIL${NC}: $name"
    echo "  Expected stdout to contain: $expected"
    echo "  Output: ${out:0:300}"
    FAILED=$((FAILED + 1)); return 1
}

run_prog_jsonl() {
    local name="$1" filter="$2" expected="$3" prog="$4"; shift 4
    local out
    out=$(cd "$NB_DIR" && printf '%s\n' "$prog" | "$NB" --config "$MOCK_CONFIG" "$@" 2>/dev/null)
    if ! echo "$out" | jq . >/dev/null 2>&1; then
        echo -e "${RED}FAIL${NC}: $name (stdout is not valid JSONL)"
        echo "  Output: ${out:0:200}"
        FAILED=$((FAILED + 1)); return 1
    fi
    local got
    got=$(echo "$out" | jq -rs "$filter" 2>/dev/null)
    if [[ "$got" == "$expected" ]]; then
        echo -e "${GREEN}PASS${NC}: $name"; PASSED=$((PASSED + 1)); return 0
    fi
    echo -e "${RED}FAIL${NC}: $name"
    echo "  jq '$filter' => '$got', expected '$expected'"
    FAILED=$((FAILED + 1)); return 1
}

echo "========================================"
echo "nb test suite"
echo "========================================"
echo ""

if [[ ! -x "$NB" ]]; then
    echo -e "${RED}ERROR${NC}: nb not found at $NB"
    echo "Run 'dotnet build' first"
    exit 1
fi

echo -e "${DIM}nb: $NB --config $MOCK_CONFIG (program on stdin)${NC}"
echo ""

# ----------------------------------------
# Mock provider (fast, deterministic)
# ----------------------------------------
echo "--- Mock Provider ---"
echo ""

run_prog_contains "mock provider returns response" 0 "OK" "run any prompt"
run_prog_contains "mock respects MOCK:response instruction" 0 "custom response here" "run MOCK:response=custom response here"

echo ""
echo "--- Transcript Schema (jsonl / --seed) ---"
echo ""

run_prog_jsonl "jsonl emit: answer extractable from stdout" \
    '[.[]|select(.type=="assistant_text").text]|last' "OK" "run any prompt"

# A program gets no persona, so the transcript leads with the user turn, not a system message.
run_prog_jsonl "jsonl emit: leads with a user event (no persona)" \
    '.[0].type' "user" "run any prompt"

run_prog_jsonl "jsonl emit: ends with a result trailer" \
    '.[-1].type' "result" "run any prompt"

# --seed replays fabricated history: both seeded user turns + the new one = 2 users.
run_prog_jsonl "seed: fabricated turns become premise" \
    '[.[]|select(.type=="user")]|length' "2" "run and 3+3?" --seed "$FIX/seed-basic.jsonl"

run_prog_jsonl "seed: fabricated assistant turn preserved" \
    '[.[]|select(.type=="assistant_text").text]|any(.=="4")' "true" "run and 3+3?" --seed "$FIX/seed-basic.jsonl"

# A seed's own system message survives as premise (no preset now, so it is the only one).
run_prog_jsonl "seed: own system message survives (1, no preset)" \
    '[.[]|select(.type=="system")]|length' "1" "run and 3+3?" --seed "$FIX/seed-system.jsonl"

run_prog "seed: orphan tool_result rejected" 1 "run go" --seed "$FIX/seed-orphan.jsonl"
run_prog "seed: missing file rejected" 1 "run go" --seed /nonexistent/seed.jsonl

echo ""
echo "--- Exit-code contract ---"
echo ""

run_prog "exit code: ok run exits 0" 0 "run any prompt"
run_prog "exit code: provider error exits 2" 2 "run MOCK:throw"
run_prog_jsonl "exit reason: provider error tagged in trailer" \
    '.[-1].exit_reason' "provider_error" "run MOCK:throw"

echo ""
echo "--- Non-TTY approval ---"
echo ""

# A tool needing approval, run headless (piped stdin, no TTY), is denied by policy
# — not a thrown ReadKey. The turn completes (exit 0) and the model gets a
# structured denial it can route around.
run_prog_jsonl "non-tty approval: unapproved bash is denied, not hung" \
    '[.[]|select(.type=="tool_result").output]|last|contains("non-interactive")' "true" \
    "run MOCK:tool=bash touch approval_probe.txt"

run_prog "non-tty approval: denied turn still exits 0" 0 \
    "run MOCK:tool=bash touch approval_probe.txt"

rm -f "$NB_DIR/approval_probe.txt"

echo ""
echo "--- Approval policy (config + directive) ---"
echo ""

# Approval.Bash auto-approves a matching bash command headlessly: `cat` is neither
# safe-listed nor trusted, so only the config allows it. (A later --config wins.)
run_prog_jsonl "approval: Approval.Bash auto-approves a match headlessly" \
    '[.[]|select(.type=="tool_result").output]|last|startswith("Error")' "false" \
    "run MOCK:tool=bash cat /etc/hostname" --config "$FIX/appr-bash.json"

run_prog_jsonl "approval: Default=deny denies via policy, not non-TTY" \
    '[.[]|select(.type=="tool_result").output]|last|contains("default: deny")' "true" \
    "run MOCK:tool=bash cat /etc/hostname" --config "$FIX/appr-deny.json"

# The `approval` directive layers onto the policy in a program.
run_prog_jsonl "approval: 'approval bash' directive auto-approves in a program" \
    '[.[]|select(.type=="tool_result").output]|last|startswith("Error")' "false" \
    "$(cat "$FIX/prog-approval-bash.nb")"

run_prog_jsonl "approval: 'approval default deny' directive denies via policy" \
    '[.[]|select(.type=="tool_result").output]|last|contains("default: deny")' "true" \
    "$(cat "$FIX/prog-approval-deny.nb")"

run_prog_contains "approval: --validate rejects an unknown key" 1 "invalid approval key" \
    "$(cat "$FIX/prog-approval-badkey.nb")" --validate

echo ""
echo "--- Env aliases ---"
echo ""

# NB_OUTPUT overrides the program's default (jsonl) — porcelain puts the answer on stdout.
export NB_OUTPUT=porcelain
run_prog_stdout_contains "env: NB_OUTPUT sets the output mode" "OK" "run any prompt"
unset NB_OUTPUT

# NB_MODEL overrides the active provider's model; the mock echoes it on MOCK:model.
export NB_MODEL=alias-model-9
run_prog_stdout_contains "env: NB_MODEL overrides the active provider model" "alias-model-9" \
    "run MOCK:model" --output porcelain
unset NB_MODEL

# An invalid NB_OUTPUT hard-fails during flag parsing, before reading the program.
export NB_OUTPUT=bogus
run_prog "env: invalid NB_OUTPUT exits 1" 1 "run hi"
unset NB_OUTPUT

echo ""
echo "--- Bash sandbox ---"
echo ""

run_prog_contains "sandbox: --validate rejects an invalid mode" 1 "invalid approval sandbox" \
    "$(cat "$FIX/prog-sandbox-badval.nb")" --validate

run_prog_stdout_contains "sandbox: --resolve prints sandbox=bwrap" "sandbox=bwrap" \
    "$(cat "$FIX/prog-sandbox-resolve.nb")" --resolve

# The behavioral proofs run the bash child for real, so they need bwrap present.
if ! command -v bwrap >/dev/null 2>&1; then
    echo -e "${YELLOW}SKIP${NC}: sandbox behavior evals (bwrap not on PATH)"
else
    run_prog_jsonl "sandbox: rootfs is read-only (write to /etc blocked)" \
        '[.[]|select(.type=="tool_result").output]|last|contains("Read-only file system")' "true" \
        "$(cat "$FIX/prog-sandbox-ro.nb")"

    SECRET="SECRET-MARKER-8842"
    SBX_PROBE="$HOME/.config/nb/nb_sbx_probe"
    mkdir -p "$HOME/.config/nb" && printf '%s\n' "$SECRET" > "$SBX_PROBE"

    run_prog_jsonl "sandbox: masked secret leaks WITHOUT sandbox (control)" \
        "[.[]|select(.type==\"tool_result\").output]|last|contains(\"$SECRET\")" "true" \
        "$(cat "$FIX/prog-sandbox-mask-control.nb")"

    run_prog_jsonl "sandbox: masked secret is empty WITH sandbox (Hole #2 sealed)" \
        "[.[]|select(.type==\"tool_result\").output]|last|contains(\"$SECRET\")" "false" \
        "$(cat "$FIX/prog-sandbox-mask.nb")"

    rm -f "$SBX_PROBE"
fi

echo ""
echo "--- Porcelain output ---"
echo ""

run_prog_stdout_contains "porcelain: plain answer on stdout" "hello world" \
    "run MOCK:response=hello world" --output porcelain

# A tool round emits a stable TOOL line on stdout (the call is recorded even when denied).
run_prog_stdout_contains "porcelain: tool call becomes a TOOL line" "TOOL bash echo hi" \
    "run MOCK:tool=bash echo hi" --output porcelain

# The headline: a fenced answer passes through verbatim. The fenced response has real
# newlines, so author it as a jsonl program (JSON escapes them cleanly).
FENCE_PROG=$(jq -cn '{type:"run",turn:0,prompt:"MOCK:response=answer:\n```json\n{\"root_cause\":\"timeout\"}\n```"}')
run_prog_stdout_contains "porcelain: fenced answer is verbatim" '```json' \
    "$FENCE_PROG" --output porcelain

echo ""
echo "--- Conversation-program evaluator ---"
echo ""

run_prog_jsonl "program: source syntax runs the directives" \
    '[.[]|select(.type=="assistant_text").text]|last' "hi there" "$(cat "$FIX/prog-basic.nb")"

run_prog_jsonl "program: bare program has no system message" \
    '[.[]|select(.type=="system")]|length' "0" "$(cat "$FIX/prog-bare.nb")"

run_prog_jsonl "program: model swaps between runs" \
    '[.[]|select(.type=="assistant_text").text]|join(",")' "alpha,beta" "$(cat "$FIX/prog-swap.nb")"

run_prog_jsonl "program: jsonl bytecode is sniffed and evaluated" \
    '[.[]|select(.type=="assistant_text").text]|last' "from bytecode" "$(cat "$FIX/prog.jsonl")"

run_prog "program: invalid directive exits 1" 1 "$(cat "$FIX/prog-bad.nb")"

# A multi-run program's trailer sums token usage across every run (mock = 15/round, so two = 30).
run_prog_jsonl "usage: multi-run trailer sums usage across runs" \
    '[.[]|select(.type=="result").usage.total]|first' "30" "$(cat "$FIX/prog-tworun.nb")"

echo ""
echo "--- Tool-surface directives ---"
echo ""

run_prog_jsonl "tools: none refuses a native tool call" \
    '[.[]|select(.type=="tool_result").output]|last|startswith("Error: Tool")' "true" \
    "$(cat "$FIX/prog-tools-none.nb")"

run_prog_jsonl "tools: -bash refuses the bash call" \
    '[.[]|select(.type=="tool_result").output]|last|startswith("Error: Tool")' "true" \
    "$(cat "$FIX/prog-tools-nobash.nb")"

# todo rides the native surface now: `tools none` strips it (the gate refuses the
# call); the default surface keeps it. (MOCK:loop/tool arg maps don't matter — the
# gate refuses BEFORE dispatch, so the assertion is Error-vs-not.)
run_prog_jsonl "tools: none strips todo (gate refuses todo_write)" \
    '[.[]|select(.type=="tool_result").output]|last|startswith("Error: Tool")' "true" \
    $'tools none\nrun MOCK:tool=todo_write x'

run_prog_jsonl "tools: todo is on by default (todo_write dispatches)" \
    '[.[]|select(.type=="tool_result").output]|last|startswith("Error: Tool")' "false" \
    $'run MOCK:tool=todo_write x'

echo ""
echo "--- Loop detector & budget rails ---"
echo ""

# Token budget: session-cumulative ceiling aborts the run (mock = 15 tokens/round).
run_prog "budget: token ceiling aborts the run (exit 3)" 3 \
    $'budget tokens 40\nrun MOCK:loop=bash echo hi'

run_prog_jsonl "budget: abort carries exit_reason token_budget" \
    '[.[]|select(.type=="result").exit_reason]|first' "token_budget" \
    $'budget tokens 40\nrun MOCK:loop=bash echo hi'

# budget tool_calls overrides the per-turn tool-call cap; a looping mock hits it.
run_prog_jsonl "budget: tool_calls cap stops a looping tool (max_tool_calls)" \
    '[.[]|select(.type=="result").exit_reason]|first' "max_tool_calls" \
    $'budget tool_calls 3\nloop off\nrun MOCK:loop=bash echo hi'

# Doom loop: on by default, a repeating tool trips the nudge (a user <system_reminder>
# enters the transcript); `loop off` silences it.
run_prog_jsonl "loop: default detector injects the nudge reminder" \
    '[.[]|select(.type=="user").text//""|test("repetitive loop")]|any' "true" \
    $'budget tool_calls 8\nrun MOCK:loop=bash echo hi'

run_prog_jsonl "loop: 'loop off' silences the nudge" \
    '[.[]|select(.type=="user").text//""|test("repetitive loop")]|any' "false" \
    $'loop off\nbudget tool_calls 8\nrun MOCK:loop=bash echo hi'

# Tool-error rail: error-ness is a typed ToolOutcome flag, not an "Error:" text sniff.
# A genuinely failing tool (unknown name) IS counted, tripping tool_error_limit at 3...
run_prog_jsonl "tool errors: repeated failures abort via tool_error_limit" \
    '[.[]|select(.type=="result").exit_reason]|first' "tool_error_limit" \
    $'run MOCK:loop=frobnicate zzz'

# ...but a SUCCEEDING tool whose stdout merely begins with "Error:" is NOT miscounted.
# The old string-sniff tripped tool_error_limit at 3 here; typed, it runs clean to the
# tool_calls cap (5). tool_calls 5 > the error limit (3), so a regression would surface
# as tool_error_limit before the cap is reached.
run_prog_jsonl "tool errors: 'Error:'-prefixed success is not a failure (typed outcome)" \
    '[.[]|select(.type=="result").exit_reason]|first' "max_tool_calls" \
    $'approval bash echo *\nloop off\nbudget tool_calls 5\nrun MOCK:loop=bash echo Error:-still-a-success'

# Wall-clock budget: a 1ms ceiling can't outlast even one model round-trip, so the
# run aborts as time_budget (exit 3). The CancellationToken now threads to the model
# call, so this preempts an in-flight hang rather than waiting it out.
run_prog "budget: wall_ms ceiling aborts the run (exit 3)" 3 \
    $'budget wall_ms 1\nrun hi'

run_prog_jsonl "budget: wall_ms abort carries exit_reason time_budget" \
    '[.[]|select(.type=="result").exit_reason]|first' "time_budget" \
    $'budget wall_ms 1\nrun hi'

# A generous wall budget doesn't false-trip a normal run.
run_prog "budget: generous wall_ms lets a normal run finish (exit 0)" 0 \
    $'budget wall_ms 60000\nrun MOCK:response=done'

# --validate catches malformed loop/budget directives.
run_prog_contains "loop: --validate rejects threshold < 2" 1 "invalid loop threshold" \
    $'loop 1\nrun hi' --validate

run_prog_contains "budget: --validate rejects an unknown key" 1 "invalid budget key" \
    $'budget cpu 5\nrun hi' --validate

echo ""
echo "--- Conversation-program tool rounds (fabricated premise) ---"
echo ""

run_prog_jsonl "tool round: fabricated call/result enters history and emits" \
    '[.[]|select(.type=="tool_result").output]|first' "foo.txt bar.txt" \
    "$(cat "$FIX/prog-toolround.jsonl")"

run_prog "tool round: unpaired call exits 1" 1 "$(cat "$FIX/prog-toolround-bad.jsonl")"

echo ""
echo "--- MCP exposure + dispatch (via built-in tester) ---"
echo ""

MCP_TESTER="$(dirname "$SCRIPT_DIR")/mcp-servers/mcp-tester/mcp-tester.csproj"
MCP_MANIFEST="$FIX/mcp-tester.generated.json"
cat > "$MCP_MANIFEST" <<JSON
{ "servers": { "tester": { "type": "stdio", "command": "dotnet",
  "args": ["run","--project","$MCP_TESTER"], "alwaysAllow": ["*"] } } }
JSON

run_prog_jsonl "mcp: manifest tool dispatches and returns a real result" \
    '[.[]|select(.type=="tool_result").output]|last|startswith("Error")' "false" \
    "$(printf 'mcp +tester\nrun MOCK:tool=tester_current_time')" --mcp "$MCP_MANIFEST"

MCP_MANIFEST_NOALLOW="$FIX/mcp-tester-noallow.generated.json"
cat > "$MCP_MANIFEST_NOALLOW" <<JSON
{ "servers": { "tester": { "type": "stdio", "command": "dotnet",
  "args": ["run","--project","$MCP_TESTER"] } } }
JSON
run_prog_jsonl "approval: Approval.McpTools glob permits an un-alwaysAllow'd MCP call" \
    '[.[]|select(.type=="tool_result").output]|last|startswith("Error")' "false" \
    "$(printf 'mcp +tester\nrun MOCK:tool=tester_current_time')" --config "$FIX/appr-mcp.json" --mcp "$MCP_MANIFEST_NOALLOW"
rm -f "$MCP_MANIFEST_NOALLOW"

run_prog_jsonl "mcp: +tester directive exposes the server on the program path" \
    '[.[]|select(.type=="tool_result").output]|last|startswith("Error")' "false" \
    "$(cat "$FIX/prog-mcp-tester.nb")" --mcp "$MCP_MANIFEST"

run_prog_jsonl "mcp: bare program (no directive) refuses the MCP tool (strict-empty)" \
    '[.[]|select(.type=="tool_result").output]|last|startswith("Error: Tool")' "true" \
    "$(cat "$FIX/prog-mcp-bare.nb")" --mcp "$MCP_MANIFEST"

rm -f "$MCP_MANIFEST"

# A server that dies on startup (mcp-tester --die-on-startup) never completes the
# handshake. Named explicitly (mcp +dead) that is a hard error — fail noisily, exit 1;
# left unnamed it is a non-fatal warning and the run still completes (exit 0).
MCP_DEAD_MANIFEST="$FIX/mcp-dead.generated.json"
cat > "$MCP_DEAD_MANIFEST" <<JSON
{ "servers": { "dead": { "type": "stdio", "command": "dotnet",
  "args": ["run","--project","$MCP_TESTER","--","--die-on-startup"] } } }
JSON

run_prog_contains "mcp: dead server named explicitly hard-fails (exit 1)" 1 \
    "was requested (mcp +dead) but failed to start" \
    "$(printf 'mcp +dead\nrun MOCK:response=hi')" --mcp "$MCP_DEAD_MANIFEST"

run_prog_contains "mcp: dead server left unnamed is a warning, run still exits 0" 0 \
    "MCP server 'dead' failed to start" \
    "run MOCK:response=hi" --mcp "$MCP_DEAD_MANIFEST"

rm -f "$MCP_DEAD_MANIFEST"

echo ""
echo "--- validate / resolve ---"
echo ""

run_prog "validate: good program exits 0" 0 "$(cat "$FIX/prog-basic.nb")" --validate
run_prog_contains "validate: unknown provider exits 1" 1 "unknown provider" \
    "$(cat "$FIX/prog-badprovider.nb")" --validate
run_prog_stdout_contains "resolve: prints per-run envelope" "run 1:" \
    "$(cat "$FIX/prog-swap.nb")" --resolve

echo ""

# ----------------------------------------
# LLM Eval (real provider, LLM-as-judge)
# ----------------------------------------
SKIP_LLM=false
for arg in "$@"; do
    if [[ "$arg" == "--skip-llm" ]]; then SKIP_LLM=true; fi
done

if [[ "$SKIP_LLM" == "true" ]]; then
    echo -e "${DIM}--- LLM Eval (skipped via --skip-llm) ---${NC}"
    echo ""
else
    echo "--- LLM Eval ---"
    echo ""

    # Args: test_name prompt criteria. Both nb runs are programs (built with jq so
    # multi-line content stays valid jsonl); the judge program carries judge.md as a
    # system directive and the eval prompt as the run.
    run_llm_eval() {
        local name="$1" prompt="$2" criteria="$3"
        echo -e "${DIM}  Running: $name...${NC}"

        local prog transcript
        prog=$(jq -cn --arg p "$prompt" '{type:"run",turn:0,prompt:$p}')
        transcript=$(cd "$NB_DIR" && printf '%s\n' "$prog" | "$NB" --output porcelain 2>&1) || true

        local eval_prompt="Criteria: $criteria

Transcript:
User: $prompt
Assistant: $transcript

Did the assistant meet the criteria?"

        local judge_prog verdict
        judge_prog=$(jq -cn --arg s "$(cat "$SCRIPT_DIR/judge.md")" '{type:"system",turn:0,text:$s}'; \
                     jq -cn --arg u "$eval_prompt" '{type:"run",turn:1,prompt:$u}')
        verdict=$(cd "$NB_DIR" && printf '%s\n' "$judge_prog" | "$NB" --output porcelain 2>&1) || true

        if [[ "$verdict" == *"PASS:"* ]] || [[ "$verdict" == *"PASS "* ]]; then
            echo -e "${GREEN}PASS${NC}: $name"; PASSED=$((PASSED + 1)); return 0
        elif [[ "$verdict" == *"FAIL:"* ]] || [[ "$verdict" == *"FAIL "* ]]; then
            echo -e "${RED}FAIL${NC}: $name"
            echo -e "${DIM}  Transcript: ${transcript:0:200}${NC}"
            echo -e "${DIM}  Verdict: ${verdict:0:300}${NC}"
            FAILED=$((FAILED + 1)); return 1
        else
            echo -e "${YELLOW}UNCLEAR${NC}: $name (no PASS/FAIL in verdict)"
            echo -e "${DIM}  Verdict: ${verdict:0:300}${NC}"
            FAILED=$((FAILED + 1)); return 1
        fi
    }

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
