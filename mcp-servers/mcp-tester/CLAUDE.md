# Claude Development Notes - MCP Tester

## Dynamic Prompt Parameter Limitation Workaround

Currently limited to 3 parameters per prompt due to MCP SDK reflection requirements. If this becomes a problem, we can use CodeDOM to generate and compile methods at runtime.

### CodeDOM Approach
The MCP SDK's reflection system needs "real" methods with proper parameter names. We can generate C# source code at runtime and compile it into a temporary assembly:

```csharp
// Generate C# source for a method with arbitrary parameters
var code = $@"
using System;
public static class DynamicPrompts_{Guid.NewGuid():N}
{{
    public static string {methodName}({string.Join(", ", parameterNames.Select(p => $"string {p}"))})
    {{
        return ""{EscapeString(content)}""{string.Join("", parameterNames.Select(p => $".Replace(\"{{{p}}}\", {p}"))};
    }}
}}";

// Compile with CSharpCodeProvider
var provider = new CSharpCodeProvider();
var parameters = new CompilerParameters { GenerateInMemory = true };
var results = provider.CompileAssemblyFromSource(parameters, code);
var method = results.CompiledAssembly.GetType($"DynamicPrompts_{guid}").GetMethod(methodName);

// Create delegate from MethodInfo - SDK should be happy with this
var delegate = Delegate.CreateDelegate(appropriateFuncType, method);
```

This would handle unlimited parameters while keeping the SDK's reflection system happy.

### Why Current Simple Approach Works
The current solution uses basic `Func<string, string>` delegates that wrap static helper methods. The SDK can inspect these without issues since they're "normal" delegates, not dynamically generated IL or Expression Trees.

### Escalation Path
1. ✅ Simple delegates (0-3 params) - current solution
2. 🔄 CodeDOM compilation (unlimited params) - if needed
3. ☢️ Pre-compilation step - nuclear option
4. 🚜 Become a farmer - give up entirely

## Implementation Notes

### Project Integration
- Integrated into nb solution as separate project under `mcp-servers/mcp-tester/`
- Excluded from main nb project compilation via `<Compile Remove="mcp-servers/**" />`
- Uses stdio transport for MCP communication
- Automatically discovers and generates prompts from `.md` files in `Prompts/` directory

### Dynamic Prompt Generation
- Scans `Prompts/` directory for `.md` files on startup
- Extracts parameters using regex pattern `\{([^}]+)\}`
- Converts filename to method name (e.g., `fave_color.md` → `favecolor`)
- Creates appropriate delegate based on parameter count
- Registers with MCP SDK using `McpServerPrompt.Create()`

### Tools
- Basic test tools: `Echo`, `ReverseEcho`, `CurrentTime`
- Demonstrates MCP tool functionality
- Easy to extend with additional tools as needed