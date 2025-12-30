# Automated Testing for nb

Status: Planned

## Overview

As nb grows in complexity—with pluggable AI providers, MCP integration, shell command execution, and conversation management—manual testing becomes insufficient. This document outlines a testing strategy that catches regressions without requiring extensive mocking infrastructure or slowing down development.

**Goals:**
- Catch regressions before they reach users
- Enable confident refactoring
- Keep tests fast enough to run on every build
- Avoid over-mocking that makes tests brittle

## Key Insight: Single-Shot Mode Enables Automation

Unlike interactive CLI apps that require complex stdin/stdout orchestration, nb's single-shot mode makes integration testing straightforward:

```bash
# Run a prompt, get output, exit
./nb "what files are in the current directory"

# Pre-approve shell commands for deterministic execution
./nb --approve "ls" "list the files here"
```

This means we can:
- **Run nb as a subprocess** and capture stdout/stderr
- **Pre-approve commands** via `--approve` flags for deterministic test runs
- **Verify output** contains expected content
- **Check exit codes** for success/failure scenarios
- **Test conversation persistence** by running multiple single-shot commands in sequence

Single-shot mode + `--approve` = fully automatable integration tests without simulating user input.

## Testing Layers

### Layer 1: Unit Tests (Fast, Isolated)

Pure logic with no external dependencies. These run in milliseconds.

**Good candidates:**
- `CommandClassifier` - classify shell commands (read/write/delete/run)
- `ApprovalPatterns` - pattern matching for --approve flag
- Output truncation logic (sandwich strategy in BashTool)
- Conversation history serialization/deserialization
- Command parsing (extracting /commands from input)
- File extension detection in FileContentExtractor

**Framework:** xUnit (already common in .NET ecosystem)

**Example:**
```csharp
public class CommandClassifierTests
{
    [Theory]
    [InlineData("ls -la", CommandType.Read)]
    [InlineData("cat file.txt", CommandType.Read)]
    [InlineData("echo 'hello' > file.txt", CommandType.Write)]
    [InlineData("rm -rf node_modules", CommandType.Delete)]
    [InlineData("npm install", CommandType.Run)]
    public void ClassifiesCommandsCorrectly(string command, CommandType expected)
    {
        var result = CommandClassifier.Classify(command);
        Assert.Equal(expected, result);
    }
}
```

### Layer 2: Component Tests (Medium Speed)

Test components in isolation with minimal fakes. Focus on boundaries.

**Good candidates:**
- `ProviderManager` - provider discovery and loading (use test provider DLLs)
- `McpManager` - MCP client lifecycle (use mcp-tester server)
- `ShellEnvironment` - environment detection (real but read-only)
- `ConfigurationService` - config loading with test appsettings files

**Approach:** Use real implementations where safe, test doubles only at true boundaries (network, filesystem writes, LLM calls).

### Layer 3: Integration Tests (Slower, High Confidence)

End-to-end flows using the actual executable. These catch issues that unit tests miss.

**Approach:** Run `./nb` as a subprocess with crafted inputs and verify outputs.

**Test harness:**
```csharp
public class NbTestHarness
{
    private readonly string _workDir;
    private readonly string _nbPath;

    public async Task<NbResult> RunAsync(string prompt, params string[] approvePatterns)
    {
        var args = new List<string>();
        foreach (var pattern in approvePatterns)
            args.AddRange(new[] { "--approve", pattern });
        args.Add(prompt);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = _nbPath,
            Arguments = string.Join(" ", args),
            WorkingDirectory = _workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        // Capture output, handle timeout, return structured result
    }
}
```

**Key scenarios:**
- Single-shot mode executes and exits
- Conversation history persists between runs
- `/clear` resets history
- `/insert` loads file content
- Shell commands execute with approval
- `--approve` flag pre-authorizes commands

### Layer 4: LLM Integration Tests (Optional, Expensive)

Tests that actually call an LLM. Run manually or in CI with budget limits.

**Purpose:** Verify tool calling works end-to-end with real models.

**Approach:**
- Use cheapest available model
- Small, deterministic prompts ("What is 2+2?")
- Verify response structure, not content
- Run sparingly (nightly, pre-release)

## Project Structure

```
nb/
├── nb.csproj                    # Main project
├── nb.Tests/                    # Test project
│   ├── nb.Tests.csproj
│   ├── Unit/
│   │   ├── CommandClassifierTests.cs
│   │   ├── ApprovalPatternsTests.cs
│   │   └── OutputTruncationTests.cs
│   ├── Component/
│   │   ├── ProviderManagerTests.cs
│   │   └── McpManagerTests.cs
│   ├── Integration/
│   │   ├── NbTestHarness.cs
│   │   ├── SingleShotModeTests.cs
│   │   └── ConversationHistoryTests.cs
│   └── Fixtures/
│       ├── test-appsettings.json
│       └── test-conversation.json
```

## Test Configuration

Tests need isolated configuration to avoid touching user's real settings.

```csharp
public class TestFixture : IDisposable
{
    public string WorkDir { get; }
    public string ConfigPath { get; }

    public TestFixture()
    {
        WorkDir = Path.Combine(Path.GetTempPath(), $"nb-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(WorkDir);

        // Copy test config
        File.Copy("Fixtures/test-appsettings.json",
                  Path.Combine(WorkDir, "appsettings.json"));
    }

    public void Dispose() => Directory.Delete(WorkDir, recursive: true);
}
```

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

      - name: Build
        run: dotnet build

      - name: Unit Tests
        run: dotnet test --filter "Category=Unit"

      - name: Component Tests
        run: dotnet test --filter "Category=Component"

      - name: Integration Tests
        run: dotnet test --filter "Category=Integration"
```

## What NOT to Test

Avoid tests that provide low value or high maintenance burden:

- **Don't mock IChatClient deeply** - LLM responses are unpredictable; test the structure of your calls, not the responses
- **Don't test Spectre.Console rendering** - trust the library
- **Don't test configuration binding** - trust Microsoft.Extensions.Configuration
- **Don't test happy-path-only** - edge cases and error handling are where bugs hide

## Implementation Phases

### Phase 1: Foundation
- Create nb.Tests project with xUnit
- Add unit tests for CommandClassifier and ApprovalPatterns
- Set up CI pipeline running on every push
- **Ship this first** - immediate value, low effort

### Phase 2: Component Tests
- Add ProviderManager tests with test provider
- Add McpManager tests using mcp-tester
- Test ShellEnvironment detection

### Phase 3: Integration Tests
- Build NbTestHarness
- Add single-shot mode tests
- Add conversation history tests
- Add command tests (/clear, /insert, etc.)

### Phase 4: Polish
- Code coverage reporting
- Test categorization and filtering
- Performance benchmarks for hot paths

## Edge Cases to Cover

**Shell execution:**
- Command timeout handling
- Output truncation thresholds
- Dangerous command detection
- Working directory changes

**Provider loading:**
- Missing provider DLL
- Provider throws during initialization
- Provider returns null client

**MCP integration:**
- Server process crashes
- Tool returns error
- Prompt has missing arguments

**Conversation history:**
- Corrupted JSON file
- Missing history file (first run)
- History from incompatible version

## Testing Checklist

- [ ] Create nb.Tests project
- [ ] Add CommandClassifier unit tests
- [ ] Add ApprovalPatterns unit tests
- [ ] Add output truncation tests
- [ ] Set up GitHub Actions workflow
- [ ] Add ProviderManager component tests
- [ ] Build integration test harness
- [ ] Add single-shot mode integration tests
- [ ] Add conversation history integration tests
