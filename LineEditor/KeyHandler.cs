// Vendored from tonerdo/readline (MIT License)
// https://github.com/tonerdo/readline
// Modified: namespace, removed password mode, cleaned up style

using System.Text;

namespace nb.LineEditor;

internal class KeyHandler
{
    private int _cursorPos;
    private int _cursorLimit;
    private readonly StringBuilder _text;
    private readonly List<string> _history;
    private int _historyIndex;
    private ConsoleKeyInfo _keyInfo;
    private readonly Dictionary<string, Action> _keyActions;
    private readonly IConsoleAdapter _console;

    private bool IsStartOfLine() => _cursorPos == 0;
    private bool IsEndOfLine() => _cursorPos == _cursorLimit;
    private bool IsStartOfBuffer() => _console.CursorLeft == 0;
    private bool IsEndOfBuffer() => _console.CursorLeft == _console.BufferWidth - 1;

    private void MoveCursorLeft()
    {
        if (IsStartOfLine()) return;

        if (IsStartOfBuffer())
            _console.SetCursorPosition(_console.BufferWidth - 1, _console.CursorTop - 1);
        else
            _console.SetCursorPosition(_console.CursorLeft - 1, _console.CursorTop);

        _cursorPos--;
    }

    private void MoveCursorRight()
    {
        if (IsEndOfLine()) return;

        if (IsEndOfBuffer())
            _console.SetCursorPosition(0, _console.CursorTop + 1);
        else
            _console.SetCursorPosition(_console.CursorLeft + 1, _console.CursorTop);

        _cursorPos++;
    }

    private void MoveCursorHome()
    {
        while (!IsStartOfLine()) MoveCursorLeft();
    }

    private void MoveCursorEnd()
    {
        while (!IsEndOfLine()) MoveCursorRight();
    }

    private void ClearLine()
    {
        MoveCursorEnd();
        while (!IsStartOfLine()) Backspace();
    }

    private void WriteNewString(string str)
    {
        ClearLine();
        foreach (char c in str) WriteChar(c);
    }

    private void WriteChar() => WriteChar(_keyInfo.KeyChar);

    private void WriteChar(char c)
    {
        if (IsEndOfLine())
        {
            // Word wrap: avoid splitting a word at the terminal buffer boundary
            if (IsEndOfBuffer() && c != ' ' && c != '\t' && _cursorPos > 0)
            {
                // Find start of current word in existing text
                int wordStart = _cursorPos;
                while (wordStart > 0 && _text[wordStart - 1] != ' ' && _text[wordStart - 1] != '\t')
                    wordStart--;
                int wordLen = (_cursorPos - wordStart) + 1; // existing word chars + new char

                // Cursor is at BufferWidth-1; word starts at BufferWidth-wordLen
                int wordStartCol = _console.BufferWidth - wordLen;

                // Only wrap if the word fits on the next row and started after column 0
                if (wordLen < _console.BufferWidth && wordStartCol > 0)
                {
                    int savedTop = _console.CursorTop;
                    // Erase the existing word chars from the current row
                    _console.SetCursorPosition(wordStartCol, savedTop);
                    _console.Write(new string(' ', wordLen - 1));
                    // Jump to next row and write the full word + new char
                    _console.SetCursorPosition(0, savedTop + 1);
                    _text.Append(c);
                    _console.Write(_text.ToString().Substring(wordStart, wordLen));
                    _cursorPos++;
                    _cursorLimit++;
                    return;
                }
            }

            _text.Append(c);
            _console.Write(c.ToString());
            _cursorPos++;
        }
        else
        {
            int left = _console.CursorLeft;
            int top = _console.CursorTop;
            string str = _text.ToString().Substring(_cursorPos);
            _text.Insert(_cursorPos, c);
            _console.Write(c.ToString() + str);
            _console.SetCursorPosition(left, top);
            MoveCursorRight();
        }

        _cursorLimit++;
    }

    private void Backspace()
    {
        if (IsStartOfLine()) return;

        MoveCursorLeft();
        int index = _cursorPos;
        _text.Remove(index, 1);
        string replacement = _text.ToString().Substring(index);
        int left = _console.CursorLeft;
        int top = _console.CursorTop;
        _console.Write($"{replacement} ");
        _console.SetCursorPosition(left, top);
        _cursorLimit--;
    }

    private void Delete()
    {
        if (IsEndOfLine()) return;

        int index = _cursorPos;
        _text.Remove(index, 1);
        string replacement = _text.ToString().Substring(index);
        int left = _console.CursorLeft;
        int top = _console.CursorTop;
        _console.Write($"{replacement} ");
        _console.SetCursorPosition(left, top);
        _cursorLimit--;
    }

    private void TransposeChars()
    {
        if (IsStartOfLine()) return;

        bool almostEnd = (_cursorLimit - _cursorPos) == 1;
        var firstIdx = IsEndOfLine() ? _cursorPos - 2 : _cursorPos - 1;
        var secondIdx = IsEndOfLine() ? _cursorPos - 1 : _cursorPos;

        var secondChar = _text[secondIdx];
        _text[secondIdx] = _text[firstIdx];
        _text[firstIdx] = secondChar;

        var left = almostEnd ? _console.CursorLeft + 1 : _console.CursorLeft;
        var cursorPosition = almostEnd ? _cursorPos + 1 : _cursorPos;

        WriteNewString(_text.ToString());

        _console.SetCursorPosition(left, _console.CursorTop);
        _cursorPos = cursorPosition;
        MoveCursorRight();
    }

    private void PrevHistory()
    {
        if (_historyIndex > 0)
        {
            _historyIndex--;
            WriteNewString(_history[_historyIndex]);
        }
    }

    private void NextHistory()
    {
        if (_historyIndex < _history.Count)
        {
            _historyIndex++;
            if (_historyIndex == _history.Count)
                ClearLine();
            else
                WriteNewString(_history[_historyIndex]);
        }
    }

    private string BuildKeyInput()
    {
        return (_keyInfo.Modifiers != ConsoleModifiers.Control && _keyInfo.Modifiers != ConsoleModifiers.Shift)
            ? _keyInfo.Key.ToString()
            : _keyInfo.Modifiers.ToString() + _keyInfo.Key.ToString();
    }

    public string Text => _text.ToString();

    public KeyHandler(IConsoleAdapter console, List<string> history)
    {
        _console = console;
        _history = history ?? new List<string>();
        _historyIndex = _history.Count;
        _text = new StringBuilder();
        _keyActions = new Dictionary<string, Action>
        {
            ["LeftArrow"] = MoveCursorLeft,
            ["Home"] = MoveCursorHome,
            ["End"] = MoveCursorEnd,
            ["ControlA"] = MoveCursorHome,
            ["ControlB"] = MoveCursorLeft,
            ["RightArrow"] = MoveCursorRight,
            ["ControlF"] = MoveCursorRight,
            ["ControlE"] = MoveCursorEnd,
            ["Backspace"] = Backspace,
            ["Delete"] = Delete,
            ["ControlD"] = Delete,
            ["ControlH"] = Backspace,
            ["ControlL"] = ClearLine,
            ["Escape"] = ClearLine,
            ["UpArrow"] = PrevHistory,
            ["ControlP"] = PrevHistory,
            ["DownArrow"] = NextHistory,
            ["ControlN"] = NextHistory,
            ["ControlU"] = () => { while (!IsStartOfLine()) Backspace(); },
            ["ControlK"] = () => { int pos = _cursorPos; MoveCursorEnd(); while (_cursorPos > pos) Backspace(); },
            ["ControlW"] = () => { while (!IsStartOfLine() && _text[_cursorPos - 1] != ' ') Backspace(); },
            ["ControlT"] = TransposeChars,
        };
    }

    public void Handle(ConsoleKeyInfo keyInfo)
    {
        _keyInfo = keyInfo;
        _keyActions.TryGetValue(BuildKeyInput(), out var action);
        (action ?? WriteChar).Invoke();
    }
}
