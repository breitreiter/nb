// Vendored from tonerdo/readline (MIT License)
// https://github.com/tonerdo/readline

namespace nb.LineEditor;

internal class ConsoleAdapter : IConsoleAdapter
{
    public int CursorLeft => Console.CursorLeft;
    public int CursorTop => Console.CursorTop;
    public int BufferWidth => Console.BufferWidth;

    public void SetCursorPosition(int left, int top) => Console.SetCursorPosition(left, top);

    public void Write(string value) => Console.Write(value);
}
