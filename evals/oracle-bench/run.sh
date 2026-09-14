#!/usr/bin/env bash
# Oracle bench: measure the oracle's verdicts against a real judge model, one case at
# a time, N samples each.
#
# Each case is a fixed assistant message + an answer sheet + the verdict a careful
# human would give. The Mock provider replays the message as the subject's turn and a
# real model, named by `oracle provider`, judges it — so every cell exercises the whole
# product path (prompt, side call, ParseVerdict, the loop) and not a replay of it.
# Motivating bug: bugs/Oracle_Misses_A_Proposal_Awaiting_Confirmation.md.
#
# Usage:
#   ./run.sh --provider <entry> [--endpoint URL] [--model NAME] [--n 5] [--case GLOB]
#            [--config appsettings.json] [--variant LABEL] [--out DIR]
#
#   --provider  a ChatProviders entry in the config (the judge); the subject is always Mock
#   --endpoint  override that entry's Endpoint (e.g. to point one entry at another local model)
#   --model     override that entry's Model
#   --n         samples per case (default 5)
#   --case      glob over case names (default '*')
#   --config    the appsettings to derive the bench config from (default: bin/Debug/net10.0/appsettings.json)
#   --variant   a label recorded with every result, for comparing prompt variants across runs
#   --out       results directory (default: out/<timestamp>-<model>-<variant>)
#
# A case file, cases/<name>.md:
#   sheet: <file under sheets/>
#   expect: hit id[,id] | miss | done        ('|'-separated alternatives all count as a pass)
#   ---
#   <the assistant's last message, verbatim>
#
# Results land in <out>/results.jsonl (one line per sample) and <out>/summary.md, and the
# summary prints. "done-on-waiting" is the count worth watching: a DONE on a message that
# was waiting on the user ends the run as `ok`, which downstream scores as a finished run.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NB_DIR="$(cd "$HERE/../.." && pwd)/bin/Debug/net10.0"
NB="$NB_DIR/nb"

PROVIDER="" ENDPOINT="" MODEL="" N=5 CASE_GLOB='*' CONFIG="$NB_DIR/appsettings.json" VARIANT="stock" OUT=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --provider) PROVIDER="$2"; shift 2 ;;
        --endpoint) ENDPOINT="$2"; shift 2 ;;
        --model) MODEL="$2"; shift 2 ;;
        --n) N="$2"; shift 2 ;;
        --case) CASE_GLOB="$2"; shift 2 ;;
        --config) CONFIG="$2"; shift 2 ;;
        --variant) VARIANT="$2"; shift 2 ;;
        --out) OUT="$2"; shift 2 ;;
        -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done
[[ -n "$PROVIDER" ]] || { echo "--provider <entry> is required" >&2; exit 2; }
[[ -x "$NB" ]] || { echo "nb not built at $NB (run dotnet build)" >&2; exit 2; }
[[ -f "$CONFIG" ]] || { echo "no config at $CONFIG" >&2; exit 2; }

# The bench config: the user's entries plus a Mock subject, the judge entry's endpoint
# and model overridden if asked. Written 0600 next to the results because it may carry keys.
JUDGE_MODEL=$(jq -r --arg p "$PROVIDER" --arg m "$MODEL" \
    '(.ChatProviders[] | select(.Name==$p) | .Model // "default") as $cur | if $m=="" then $cur else $m end' "$CONFIG")
[[ -n "$JUDGE_MODEL" && "$JUDGE_MODEL" != "null" ]] || { echo "no ChatProviders entry named '$PROVIDER' in $CONFIG" >&2; exit 2; }
OUT="${OUT:-$HERE/out/$(date +%Y%m%d-%H%M%S)-${JUDGE_MODEL//\//_}-$VARIANT}"
mkdir -p "$OUT/runs"
CFG="$OUT/config.json"
(umask 077; jq --arg p "$PROVIDER" --arg e "$ENDPOINT" --arg m "$MODEL" '
    .ActiveProvider = "Mock"
    | .ChatProviders = ([.ChatProviders[] | select(.Name != "Mock")]
        | map(if .Name == $p then
                (if $e != "" then .Endpoint = $e else . end)
              | (if $m != "" then .Model = $m else . end)
              else . end))
        + [{"Name":"Mock","Harness":"nb","Response":"Done."}]' "$CONFIG" > "$CFG")

# Hermetic: no layered mcp.json servers start for a run whose subject is a Mock.
MCP="$OUT/mcp.json"; echo '{"servers":{}}' > "$MCP"

RESULTS="$OUT/results.jsonl"
: > "$RESULTS"
echo "judge: $PROVIDER ($JUDGE_MODEL)  variant: $VARIANT  n: $N  out: $OUT"

shopt -s nullglob
CASES=("$HERE"/cases/$CASE_GLOB.md)
[[ ${#CASES[@]} -gt 0 ]] || { echo "no cases match '$CASE_GLOB'" >&2; exit 2; }

# name<TAB>expected<TAB>hit<TAB>miss<TAB>done<TAB>error<TAB>pass<TAB>done-on-waiting
TABLE="$OUT/table.tsv"; : > "$TABLE"

for case_file in "${CASES[@]}"; do
    name=$(basename "$case_file" .md)
    sheet=$(sed -n 's/^sheet: *//p' "$case_file" | head -1)
    expect=$(sed -n 's/^expect: *//p' "$case_file" | head -1)
    [[ -f "$HERE/sheets/$sheet" ]] || { echo "$name: no sheet '$sheet'" >&2; continue; }

    # The message is everything after the first '---' line, replayed verbatim by the Mock.
    msg_file="$OUT/runs/$name.message"
    { printf 'MOCK:response='; awk 'f{print} /^---$/{f=1}' "$case_file"; } > "$msg_file"
    prog_file="$OUT/runs/$name.nb"
    printf 'oracle provider %s\noracle @%s\nbudget oracle_turns 1\nrun @%s\n' "$PROVIDER" "$HERE/sheets/$sheet" "$msg_file" > "$prog_file"

    hit=0 miss=0 done_=0 err=0 pass=0 dow=0
    printf '%-28s ' "$name"
    for ((i = 1; i <= N; i++)); do
        out_jsonl="$OUT/runs/$name.$i.jsonl"; out_err="$OUT/runs/$name.$i.stderr"
        t0=$(date +%s%N)
        (cd "$NB_DIR" && "$NB" "$prog_file" --config "$CFG" --mcp "$MCP" --output jsonl > "$out_jsonl" 2> "$out_err")
        ms=$(( ($(date +%s%N) - t0) / 1000000 ))

        exit_reason=$(jq -r 'select(.type=="result") | .exit_reason' "$out_jsonl" 2>/dev/null | head -1)
        keys=$(jq -r 'select(.type=="user" and .source=="oracle") | .keys | join(",")' "$out_jsonl" 2>/dev/null | head -1)
        out_tokens=$(jq -r 'select(.type=="result") | .usage.output // .usage.output_tokens // empty' "$out_jsonl" 2>/dev/null | head -1)
        raw=$(jq -r 'select(.type=="result") | .oracle_verdict // empty' "$out_jsonl" 2>/dev/null | head -1)
        warn=$(grep -h '^program: oracle:' "$out_err" | sed 's/^program: //' | head -3 | paste -sd'|' -)

        if [[ -n "$keys" ]]; then verdict="hit $keys"
        elif [[ "$exit_reason" == "oracle_miss" ]]; then verdict="miss"
        elif [[ "$exit_reason" == "ok" ]]; then verdict="done"
        else verdict="error ${exit_reason:-$(head -c 120 "$out_err" | tr '\n' ' ')}"
        fi

        # Pass if the verdict matches any alternative; hits compare as id sets.
        ok=0
        IFS='|' read -ra alts <<< "$expect"
        for alt in "${alts[@]}"; do
            alt=$(echo "$alt" | xargs)
            if [[ "$alt" == hit* && "$verdict" == hit* ]]; then
                a=$(echo "${alt#hit }" | tr ',' '\n' | sort | paste -sd, -)
                v=$(echo "${verdict#hit }" | tr ',' '\n' | sort | paste -sd, -)
                [[ "$a" == "$v" ]] && ok=1
            elif [[ "$alt" == "$verdict" ]]; then ok=1
            fi
        done

        case "$verdict" in hit*) hit=$((hit+1)); mark=H ;; miss) miss=$((miss+1)); mark=M ;; done) done_=$((done_+1)); mark=D ;; *) err=$((err+1)); mark=E ;; esac
        [[ $ok -eq 1 ]] && { pass=$((pass+1)); } || mark="${mark,,}"
        [[ "$verdict" == done && "$expect" != done* ]] && dow=$((dow+1))
        printf '%s' "$mark"

        jq -cn --arg case "$name" --argjson i "$i" --arg expect "$expect" --arg verdict "$verdict" --argjson pass "$ok" \
            --arg exit "$exit_reason" --arg raw "$raw" --arg warn "$warn" --argjson ms "$ms" --arg tokens "${out_tokens:-}" \
            --arg judge "$PROVIDER" --arg model "$JUDGE_MODEL" --arg variant "$VARIANT" \
            '{case:$case, sample:$i, expect:$expect, verdict:$verdict, pass:($pass==1), exit_reason:$exit, raw_verdict:$raw, warnings:$warn, ms:$ms, output_tokens:$tokens, judge:$judge, model:$model, variant:$variant}' >> "$RESULTS"
    done
    printf '  %d/%d pass  (hit %d, miss %d, done %d, err %d)  expect: %s\n' "$pass" "$N" "$hit" "$miss" "$done_" "$err" "$expect"
    printf '%s\t%s\t%d\t%d\t%d\t%d\t%d\t%d\n' "$name" "$expect" "$hit" "$miss" "$done_" "$err" "$pass" "$dow" >> "$TABLE"
done

# Summary: per case, then the two numbers that matter.
{
    echo "# Oracle bench — judge \`$PROVIDER\` ($JUDGE_MODEL), variant \`$VARIANT\`, n=$N, $(date -Iminutes)"
    echo
    echo "| case | expected | hit | miss | done | err | pass |"
    echo "|---|---|---|---|---|---|---|"
    awk -F'\t' -v n="$N" '{ printf "| %s | %s | %d | %d | %d | %d | **%d/%d** |\n", $1, $2, $3, $4, $5, $6, $7, n }' "$TABLE"
    echo
    awk -F'\t' -v n="$N" '{ p+=$7; t+=n; d+=$8 } END { printf "**overall pass: %d/%d**  ·  **done-on-waiting: %d** (a DONE on a message that was waiting on the user — scored as a finished run)\n", p, t, d }' "$TABLE"
    echo
    printf 'median judge time: %s ms  ·  mean output tokens: %s\n' \
        "$(jq -s 'map(.ms) | sort | .[length/2|floor]' "$RESULTS")" \
        "$(jq -s '[.[] | .output_tokens | select(. != "") | tonumber] | if length>0 then (add/length|floor) else "n/a" end' "$RESULTS")"
} > "$OUT/summary.md"
echo; cat "$OUT/summary.md"
