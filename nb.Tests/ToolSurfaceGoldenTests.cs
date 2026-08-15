using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using nb.Harness;
using nb.MCP;
using nb.Shell;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// Golden master over the <b>fully-assembled advertised tool surface</b> — every tool
/// name, description and emitted JSON schema, exactly as they reach the wire.
///
/// This exists because the assembly block had no other unit coverage when
/// <c>plans/harness-emulation.md</c> step 1 lifted it out of
/// <c>ConversationManager.SendMessageInternalAsync</c> into <see cref="NbHarness"/>.
/// A green suite would not have detected a regression there; these files, recorded
/// against the pre-refactor code and unchanged by it, did the work.
///
/// Captured through a real <c>RunAsync</c> call, so what is asserted is what reached
/// the wire.
///
/// The same snapshot is reused afterwards to assert what each harness costume advertises.
///
/// To re-baseline after an intentional change: <c>UPDATE_GOLDEN=1 dotnet test</c>, then
/// read the diff before committing it.
/// </summary>
[Collection(ConsoleBoundCollection.Name)]
public class ToolSurfaceGoldenTests : IDisposable
{
    private readonly string _testDir;
    private readonly ShellEnvironment _env;

    public ToolSurfaceGoldenTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nb-test-surface-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _env = ShellEnvironment.Detect();
        _env.SetCwd(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public Task Surface_AllNativeTools() => AssertGolden("all-native", ToolSurface.All);

    [Fact]
    public Task Surface_ToolsNone() =>
        AssertGolden("tools-none", new ToolSurface { NativeAllow = new HashSet<string>() });

    [Fact]
    public Task Surface_FilteredSubset() =>
        AssertGolden("filtered-subset", new ToolSurface
        {
            NativeAllow = new HashSet<string>(new[] { "read_file", "edit_file", "grep" }, StringComparer.OrdinalIgnoreCase),
        });

    /// <summary>The <c>EditToolStyle: ApplyPatch</c> wiring — apply_patch replaces write_file+edit_file.</summary>
    [Fact]
    public Task Surface_ApplyPatchStyle() => AssertGolden("apply-patch", ToolSurface.All, applyPatchStyle: true);

    /// <summary>--nobash: every native tool is null. todo rides the surface and survives.</summary>
    [Fact]
    public Task Surface_NoBash() => AssertGolden("nobash", ToolSurface.All, noBash: true);

    /// <summary>The qwen-code costume: qwen's names and parameter spellings, nb's tools behind them.</summary>
    [Fact]
    public Task Surface_QwenCodeHarness() => AssertGolden("qwen-code", ToolSurface.All, harness: QwenCodeHarness.HarnessName);

    /// <summary>A tools directive still filters a costume — in nb's canonical vocabulary.</summary>
    [Fact]
    public Task Surface_QwenCodeFiltered() =>
        AssertGolden("qwen-code-filtered", new ToolSurface
        {
            NativeAllow = new HashSet<string>(new[] { "read_file", "edit_file", "bash" }, StringComparer.OrdinalIgnoreCase),
        }, harness: QwenCodeHarness.HarnessName);

    /// <summary>
    /// The codex costume: four tools, and — more to the point — none of the seven file
    /// and search tools nb holds. Withholding is most of what this costume does.
    /// </summary>
    [Fact]
    public Task Surface_CodexHarness() => AssertGolden("codex", ToolSurface.All, harness: CodexHarness.HarnessName);

    /// <summary>
    /// EditToolStyle no longer reaches a costume: codex advertises apply_patch either
    /// way, because the runtime now builds every edit tool and the harness chooses.
    /// </summary>
    [Fact]
    public Task Surface_CodexHarnessIgnoresEditToolStyle() =>
        AssertGolden("codex", ToolSurface.All, applyPatchStyle: true, harness: CodexHarness.HarnessName);

    /// <summary>The claude-code costume: PascalCase names and file_path throughout.</summary>
    [Fact]
    public Task Surface_ClaudeCodeHarness() =>
        AssertGolden("claude-code", ToolSurface.All, harness: ClaudeCodeHarness.HarnessName);

    // ---- harness ----

    private async Task AssertGolden(string name, ToolSurface surface, bool applyPatchStyle = false, bool noBash = false, string? harness = null)
    {
        var rendered = await CaptureSurface(surface, applyPatchStyle, noBash, harness);
        var path = GoldenPath(name);

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, rendered);
            return;
        }

        Assert.True(File.Exists(path),
            $"Missing golden file {path}. Run with UPDATE_GOLDEN=1 to create it, then review the contents before committing.");

        var expected = await File.ReadAllTextAsync(path);
        Assert.Equal(Normalize(expected), Normalize(rendered));
    }

    private async Task<string> CaptureSurface(ToolSurface surface, bool applyPatchStyle, bool noBash, string? harness = null)
    {
        var client = new RecordingChatClient();
        var approval = new ApprovalPolicy(trust: false, new ApprovalPatterns(), _ => false);

        // Mirrors NbRuntime's wiring: --nobash nulls every native tool; otherwise every
        // tool is built and EditToolStyle decides only which edit surface nb's own harness
        // advertises. search_web stays null (it is config-gated on an API key and would
        // otherwise make the golden environment-dependent).
        BashTool? bash = null;
        ReadFileTool? readFile = null;
        WriteFileTool? writeFile = null;
        EditFileTool? editFile = null;
        FindFilesTool? findFiles = null;
        GrepTool? grep = null;
        ListDirTool? listDir = null;
        FetchUrlTool? fetchUrl = null;
        ApplyPatchTool? applyPatch = null;

        if (!noBash)
        {
            bash = new BashTool(_env, defaultTimeoutSeconds: 120);
            readFile = new ReadFileTool(_env);
            findFiles = new FindFilesTool(_env);
            grep = new GrepTool(_env);
            listDir = new ListDirTool(_env);
            fetchUrl = new FetchUrlTool();
            writeFile = new WriteFileTool(_env);
            editFile = new EditFileTool(_env);
            applyPatch = new ApplyPatchTool(_env);
        }

        NbHarness harnessInstance = new NbHarness(
            bash, readFile, writeFile, editFile, findFiles, grep, listDir, fetchUrl,
            searchWeb: null, applyPatch, applyPatchStyle);
        if (harness is not null)
            harnessInstance = HarnessRegistry.Create(harness, harnessInstance);

        var conversation = new ConversationManager(
            client, new McpManager(), new FakeToolManager(), harnessInstance, approval);
        conversation.SetToolSurface(surface);

        await conversation.RunAsync("hello");

        Assert.True(client.Called, "The model was never invoked, so no tool surface was captured.");
        return Render(client.CapturedTools);
    }

    private string Render(IList<AITool>? tools)
    {
        var sb = new StringBuilder();
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        if (tools is null || tools.Count == 0)
        {
            sb.AppendLine("(no tools advertised)");
            return sb.ToString();
        }

        // Declaration order is part of the behaviour under test — do not sort.
        foreach (var tool in tools)
        {
            sb.AppendLine($"=== {tool.Name}");
            sb.AppendLine("--- description");
            sb.AppendLine(Scrub(tool.Description ?? ""));
            sb.AppendLine("--- schema");
            sb.AppendLine(tool is AIFunction fn
                ? Scrub(JsonSerializer.Serialize(fn.JsonSchema, jsonOptions))
                : "(not an AIFunction)");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Several tools interpolate live environment values into their descriptions —
    /// bash carries the shell cwd and shell name (<c>BashTool.cs:56-59</c>), and the
    /// file tools carry "Paths are relative to: {cwd}". Without scrubbing, the golden
    /// file would be machine- and run-dependent.
    /// </summary>
    private string Scrub(string text) => text
        .Replace(_env.ShellCwd, "<CWD>")
        .Replace(_env.ShellName, "<SHELL>");

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

    private static string GoldenPath(string name, [CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "golden", $"tool-surface.{name}.txt");

    /// <summary>Captures the ChatOptions of the first model call and ends the turn with plain text.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public IList<AITool>? CapturedTools { get; private set; }
        public bool Called { get; private set; }

        private void Capture(ChatOptions? options)
        {
            if (Called) return;
            Called = true;
            CapturedTools = options?.Tools;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Capture(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Capture(options);
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
