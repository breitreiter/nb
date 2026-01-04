# Automated Testing for nb

Status: Partially Implemented

## Overview

Testing strategy optimized for a solo developer with AI-assisted workflow. Goals:
- Catch regressions before they reach users
- Enable confident refactoring
- Keep tests simple enough that one person can maintain them
- Support AI-first development (Claude Code can run tests, interpret failures, iterate)

**Key insight:** nb's single-shot mode + `--approve` flag makes integration testing trivial without complex test infrastructure.

## Testing Layers

Two layers only. Skip "component tests" - for a project this size, the distinction adds complexity without value.

### Layer 1: Unit Tests (xUnit)

Pure logic with no external dependencies. Run in milliseconds via `dotnet test`.

**What to test:**
- Pure functions with edge cases (CommandClassifier, ApprovalPatterns)
- Parsing/serialization logic (if bugs occur)
- Anything that broke before (regression tests)

**What NOT to test:**
- Orchestration code (ConversationManager) - test via e2e
- Provider wrappers - they just call SDKs
- Spectre.Console rendering - trust the library
- Config binding - trust Microsoft.Extensions.Configuration

**Current coverage:**
- ✅ `CommandClassifier` - command categories, danger patterns, multi-line
- ✅ `ApprovalPatterns` - exact match, globs, whitespace handling

**Future candidates (add when touched or broken):**
- `BashTool` sandwich truncation (extract pure function, test it)
- Conversation history JSON serialization

### Layer 2: Integration Tests (Bash)

End-to-end flows using the actual executable. Located in `evals/run.sh`.

**Why bash over C#:**
- Readable by anyone
- No test infrastructure to maintain
- Tests real behavior, not mocked approximations
- Easy to add new tests (copy-paste a function call)

**Test categories:**

```bash
# Sanity tests - arg parsing, command interception
run_test_contains "--system with missing file errors" 1 "not found" "$NB" --system /nonexistent "test"
run_test_contains "? shows help" 0 "exit" "$NB" "?"

# Mock provider tests - deterministic, no API calls
run_test_contains "mock returns response" 0 "OK" "$NB" "any prompt"
run_test_contains "MOCK:response instruction" 0 "custom" "$NB" "MOCK:response=custom"

# LLM evals - behavioral tests with real models (optional, expensive)
run_llm_eval "answers math without tools" "What is 2+2?" "should answer 4 without bash"
```

**Running tests:**
```bash
./evals/run.sh              # Full suite
./evals/run.sh --skip-llm   # Skip expensive LLM tests (for CI)
```

## MockProvider

The Mock provider enables deterministic integration tests without API calls:
- Returns "OK" by default
- Responds to `MOCK:response=<text>` prefix for controlled responses
- No API key required

## CI Integration

```yaml
# .github/workflows/test.yml
name: Tests
on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet build
      - run: dotnet test
      - run: ./evals/run.sh --skip-llm
```

## AI-First Testing

For Claude Code workflow:

1. **Fast feedback** - `dotnet test` runs in seconds, bash tests in seconds
2. **Clear failures** - xUnit and bash output show exactly what broke
3. **Behavior over implementation** - tests verify "command X is classified as dangerous" not "method Y calls Z", so Claude can refactor freely
4. **MockProvider** - deterministic e2e tests without API costs

**Pattern:**
```
User: "Add feature X"
Claude: *implements*
Claude: *runs dotnet test && ./evals/run.sh --skip-llm*
Claude: "Tests pass, here's the change"
```

## What NOT to Build

- ❌ C# test harness for integration tests (bash is simpler)
- ❌ Test fixtures, factories, builders (over-engineering)
- ❌ Mocks for IChatClient (LLM responses are unpredictable)
- ❌ Coverage targets (20% of critical paths beats 80% of trivial code)
- ❌ Separate "component test" layer (not enough value for complexity)

## Adding Tests

**When adding a new feature:**
1. If it has pure logic with edge cases → add unit test
2. If it's user-facing behavior → add bash integration test
3. If it's orchestration/glue code → test via e2e, don't unit test

**When fixing a bug:**
1. Add a test that reproduces the bug first
2. Fix the bug
3. Test passes → done

**When refactoring:**
1. Run existing tests
2. If they pass, you're probably fine
3. If critical code has no tests, add them before refactoring

## Implementation Checklist

- [x] Create nb.Tests project
- [x] CommandClassifier unit tests
- [x] ApprovalPatterns unit tests
- [x] Basic integration tests (evals/run.sh)
- [x] MockProvider for deterministic e2e
- [ ] GitHub Actions CI
- [ ] Truncation logic unit tests (when BashTool changes)
- [ ] History serialization tests (if corruption bug occurs)
