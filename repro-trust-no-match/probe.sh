#!/usr/bin/env bash
# Minimal repro: which bash commands does `approval default prompt` +
# `approval sandbox bwrap` + `"Trust": true` actually auto-approve?
#
# Observed in the wild (a documentation-retrieval eval harness, 2026-09-04):
# commands as harmless as `find <path> -name "x.md" 2>/dev/null` come back
#   approved: deny   approval_reason: no-match
# even with Trust: true and a read-only, network-less bwrap sandbox. The docs say
# the third rung of the `prompt` ladder is "trust + sandbox, which auto-approves
# any non-dangerous command in the sandbox", so either that rung is not being
# reached or `non-dangerous` is narrower than it reads.
#
# This walks one variable at a time: a bare command, then the same command with a
# stderr redirect, a pipe, a `&&`, and an absolute path. Each is a separate nb run
# with a single instruction, so the model has no reason to do anything else.
#
# Usage:  NB=/path/to/nb NB_CONFIG=/path/to/config.json ./probe.sh [outdir]
# The config needs a working provider; set TRUST=false to run the same matrix with
# trust off for comparison.
set -uo pipefail

NB="${NB:?set NB to the nb binary}"
NB_CONFIG="${NB_CONFIG:?set NB_CONFIG to a config with a working provider}"
PROVIDER="${PROVIDER:-LocalCoder}"
MODEL="${MODEL:-qwen3-coder-next}"
TRUST="${TRUST:-true}"
OUT="${1:-$(cd "$(dirname "$0")" && pwd)/out-trust-$TRUST}"

mkdir -p "$OUT/workspace/docs"
printf 'hello\n' > "$OUT/workspace/docs/a.md"
printf 'world\n' > "$OUT/workspace/docs/b.md"

CFG="$OUT/config.json"
python3 - "$NB_CONFIG" "$CFG" "$TRUST" <<'PYCFG'
import json, os, stat, sys
cfg = json.load(open(sys.argv[1]))
cfg["Trust"] = (sys.argv[3] == "true")
fd = os.open(sys.argv[2], os.O_WRONLY | os.O_CREAT | os.O_TRUNC,
             stat.S_IRUSR | stat.S_IWUSR)
with os.fdopen(fd, "w") as fh:
    json.dump(cfg, fh, indent=1)
PYCFG

# name<TAB>command. One variable changed at a time.
CASES=$(cat <<'EOF'
bare-ls	ls docs
bare-find	find docs -name "a.md"
find-stderr	find docs -name "a.md" 2>/dev/null
find-or	find docs -name "*.md" -o -name "*.css"
bare-grep	grep hello docs/a.md
grep-pipe	grep hello docs/a.md | head -1
grep-stderr	grep hello docs/a.md 2>/dev/null
cd-and-grep	cd docs && grep hello a.md
abs-path-grep	grep hello __WS__/docs/a.md
bare-cat	cat docs/a.md
bare-wc	wc -l docs/a.md
EOF
)

printf '%-16s %-8s %-14s %s\n' case approved reason command
printf '%s\n' "--------------------------------------------------------------------------------"

while IFS=$'\t' read -r NAME CMD; do
  [ -z "$NAME" ] && continue
  CMD="${CMD//__WS__/$OUT/workspace}"
  RUN="$OUT/$NAME"; mkdir -p "$RUN"
  cat > "$RUN/program.nb" <<NBEOF
provider $PROVIDER
model $MODEL
approval default prompt
approval sandbox bwrap
budget tokens 20000
budget tool_calls 3
user Run exactly this shell command and report its output verbatim: $CMD
run
NBEOF
  ( cd "$OUT/workspace" && "$NB" --config "$CFG" --output jsonl "$RUN/program.nb" ) \
      > "$RUN/transcript.jsonl" 2> "$RUN/nb.err"
  python3 - "$RUN/transcript.jsonl" "$NAME" "$CMD" <<'PYR'
import json, sys
name, cmd = sys.argv[2], sys.argv[3]
appr, reason = "(no bash call)", ""
for line in open(sys.argv[1], errors="replace"):
    line = line.strip()
    if not line:
        continue
    try:
        e = json.loads(line)
    except Exception:
        continue
    if e.get("type") == "tool_call" and e.get("name") == "bash":
        appr = e.get("approved") or "allow"
        reason = e.get("approval_reason") or ""
        break
print(f"{name:<16} {appr:<8} {reason:<14} {cmd[:60]}")
PYR
done <<< "$CASES"

echo
echo "transcripts under $OUT/<case>/transcript.jsonl"
