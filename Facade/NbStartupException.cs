namespace nb;

/// <summary>
/// A run could not be assembled: no usable chat client, an invalid approval/sandbox
/// setting, or an unusable shell environment. The library throws this instead of the
/// CLI's <c>Environment.Exit(1)</c>; the CLI catches it and maps it to exit code 1.
/// </summary>
public sealed class NbStartupException : Exception
{
    public NbStartupException(string message) : base(message) { }
}
