namespace nb.Tests;

/// <summary>
/// Tests that drive a real run share one xunit collection, which means they never run
/// concurrently with each other.
///
/// They have to. <c>ConversationManager.SendMessageInternalAsync</c> wraps the first
/// model round-trip in <c>AnsiConsole.Status()</c> for the "Thinking…" spinner, and
/// Spectre's <c>AnsiConsole</c> is process-global: a second concurrent live display
/// throws, the exception is caught by the turn's own error handling, and the run ends
/// having never called the client. The symptom is a run that quietly did nothing —
/// which reads as a product bug rather than a collision, so it is worth naming here.
///
/// See bugs/Concurrent_Runs_Collide_On_The_Global_Console.md. When that is fixed, this
/// collection can go.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConsoleBoundCollection
{
    public const string Name = "console-bound";
}
