using UglyPrompt;

namespace UglyPrompt.Tests;

public class KeyHandlerTests
{
    // --- test doubles ---

    private class FakeConsole : IConsoleAdapter
    {
        public int CursorLeft { get; private set; }
        public int CursorTop { get; private set; }
        public int BufferWidth { get; init; } = 100;
        public int BufferHeight { get; init; } = 40;

        public void SetCursorPosition(int left, int top) { CursorLeft = left; CursorTop = top; }

        public void Write(string value)
        {
            foreach (char c in value)
            {
                if (c == '\n') { CursorTop++; CursorLeft = 0; }
                else if (++CursorLeft >= BufferWidth) { CursorLeft = 0; CursorTop++; }
            }
        }
    }

    // --- helpers ---

    private static ConsoleKeyInfo Char(char c) =>
        new(c, ConsoleKey.NoName, false, false, false);

    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new('\0', key, false, false, false);

    private static ConsoleKeyInfo Ctrl(ConsoleKey key) =>
        new('\0', key, false, false, true);

    private KeyHandler Make(List<string>? history = null) =>
        new KeyHandler(new FakeConsole(), history ?? new List<string>());

    private KeyHandler Type(KeyHandler h, string text)
    {
        foreach (char c in text) h.Handle(Char(c));
        return h;
    }

    private void MoveLeft(KeyHandler h, int n = 1)
    {
        for (int i = 0; i < n; i++) h.Handle(Key(ConsoleKey.LeftArrow));
    }

    private void MoveRight(KeyHandler h, int n = 1)
    {
        for (int i = 0; i < n; i++) h.Handle(Key(ConsoleKey.RightArrow));
    }

    // --- typing ---

    [Fact]
    public void Type_SingleChar_TextIsChar()
    {
        var h = Make();
        h.Handle(Char('x'));
        Assert.Equal("x", h.Text);
    }

    [Fact]
    public void Type_MultipleChars_Accumulates()
    {
        var h = Make();
        Type(h, "hello");
        Assert.Equal("hello", h.Text);
    }

    [Fact]
    public void Type_AtMidpoint_InsertsInOrder()
    {
        var h = Make();
        Type(h, "ab");
        MoveLeft(h);
        h.Handle(Char('c'));
        Assert.Equal("acb", h.Text);
    }

    // --- backspace ---

    [Fact]
    public void Backspace_RemovesLastChar()
    {
        var h = Make();
        Type(h, "abc");
        h.Handle(Key(ConsoleKey.Backspace));
        Assert.Equal("ab", h.Text);
    }

    [Fact]
    public void Backspace_AtStart_DoesNothing()
    {
        var h = Make();
        h.Handle(Key(ConsoleKey.Backspace));
        Assert.Equal("", h.Text);
    }

    [Fact]
    public void Backspace_AtMidpoint_RemovesCharBefore()
    {
        var h = Make();
        Type(h, "abc");
        MoveLeft(h);
        h.Handle(Key(ConsoleKey.Backspace));
        Assert.Equal("ac", h.Text);
    }

    [Fact]
    public void CtrlH_BehavesLikeBackspace()
    {
        var h = Make();
        Type(h, "abc");
        h.Handle(Ctrl(ConsoleKey.H));
        Assert.Equal("ab", h.Text);
    }

    // --- delete ---

    [Fact]
    public void Delete_AtEnd_DoesNothing()
    {
        var h = Make();
        Type(h, "abc");
        h.Handle(Key(ConsoleKey.Delete));
        Assert.Equal("abc", h.Text);
    }

    [Fact]
    public void Delete_AtStart_RemovesFirstChar()
    {
        var h = Make();
        Type(h, "abc");
        h.Handle(Key(ConsoleKey.Home));
        h.Handle(Key(ConsoleKey.Delete));
        Assert.Equal("bc", h.Text);
    }

    [Fact]
    public void Delete_AtMidpoint_RemovesCharUnderCursor()
    {
        var h = Make();
        Type(h, "abc");
        MoveLeft(h, 2);
        h.Handle(Key(ConsoleKey.Delete));
        Assert.Equal("ac", h.Text);
    }

    [Fact]
    public void CtrlD_BehavesLikeDelete()
    {
        var h = Make();
        Type(h, "abc");
        h.Handle(Key(ConsoleKey.Home));
        h.Handle(Ctrl(ConsoleKey.D));
        Assert.Equal("bc", h.Text);
    }

    // --- cursor movement ---

    [Fact]
    public void LeftArrow_AtStart_DoesNothing()
    {
        var h = Make();
        Type(h, "a");
        MoveLeft(h, 5); // more than text length — should clamp at 0
        h.Handle(Char('x'));
        Assert.Equal("xa", h.Text);
    }

    [Fact]
    public void RightArrow_AtEnd_DoesNothing()
    {
        var h = Make();
        Type(h, "a");
        MoveRight(h, 5); // should clamp at end
        h.Handle(Char('x'));
        Assert.Equal("ax", h.Text);
    }

    [Fact]
    public void Home_MovesToStart()
    {
        var h = Make();
        Type(h, "ab");
        h.Handle(Key(ConsoleKey.Home));
        h.Handle(Char('x'));
        Assert.Equal("xab", h.Text);
    }

    [Fact]
    public void End_MovesToEnd()
    {
        var h = Make();
        Type(h, "ab");
        h.Handle(Key(ConsoleKey.Home));
        h.Handle(Key(ConsoleKey.End));
        h.Handle(Char('x'));
        Assert.Equal("abx", h.Text);
    }

    [Fact]
    public void CtrlA_MovesToStart()
    {
        var h = Make();
        Type(h, "ab");
        h.Handle(Ctrl(ConsoleKey.A));
        h.Handle(Char('x'));
        Assert.Equal("xab", h.Text);
    }

    [Fact]
    public void CtrlE_MovesToEnd()
    {
        var h = Make();
        Type(h, "ab");
        h.Handle(Ctrl(ConsoleKey.A));
        h.Handle(Ctrl(ConsoleKey.E));
        h.Handle(Char('x'));
        Assert.Equal("abx", h.Text);
    }

    [Fact]
    public void CtrlB_MovesLeft()
    {
        var h = Make();
        Type(h, "ab");
        h.Handle(Ctrl(ConsoleKey.B));
        h.Handle(Char('x'));
        Assert.Equal("axb", h.Text);
    }

    [Fact]
    public void CtrlF_MovesRight()
    {
        var h = Make();
        Type(h, "ab");
        h.Handle(Key(ConsoleKey.Home));
        h.Handle(Ctrl(ConsoleKey.F));
        h.Handle(Char('x'));
        Assert.Equal("axb", h.Text);
    }

    // --- kill commands ---

    [Fact]
    public void Escape_ClearsLine()
    {
        var h = Make();
        Type(h, "hello");
        h.Handle(Key(ConsoleKey.Escape));
        Assert.Equal("", h.Text);
    }

    [Fact]
    public void CtrlL_ClearsLine()
    {
        var h = Make();
        Type(h, "hello");
        h.Handle(Ctrl(ConsoleKey.L));
        Assert.Equal("", h.Text);
    }

    [Fact]
    public void CtrlU_KillsToStart()
    {
        var h = Make();
        Type(h, "hello world");
        h.Handle(Ctrl(ConsoleKey.U));
        Assert.Equal("", h.Text);
    }

    [Fact]
    public void CtrlU_FromMidpoint_KillsToStart()
    {
        var h = Make();
        Type(h, "hello world");
        MoveLeft(h, 5);
        h.Handle(Ctrl(ConsoleKey.U));
        Assert.Equal("world", h.Text);
    }

    [Fact]
    public void CtrlK_KillsToEnd()
    {
        var h = Make();
        Type(h, "hello");
        MoveLeft(h, 3);
        h.Handle(Ctrl(ConsoleKey.K));
        Assert.Equal("he", h.Text);
    }

    [Fact]
    public void CtrlK_AtEnd_DoesNothing()
    {
        var h = Make();
        Type(h, "hello");
        h.Handle(Ctrl(ConsoleKey.K));
        Assert.Equal("hello", h.Text);
    }

    [Fact]
    public void CtrlW_KillsLastWord()
    {
        var h = Make();
        Type(h, "foo bar");
        h.Handle(Ctrl(ConsoleKey.W));
        Assert.Equal("foo ", h.Text);
    }

    [Fact]
    public void CtrlW_KillsEntireLineIfNoSpace()
    {
        var h = Make();
        Type(h, "hello");
        h.Handle(Ctrl(ConsoleKey.W));
        Assert.Equal("", h.Text);
    }

    // --- history ---

    [Fact]
    public void UpArrow_WithHistory_RestoresLastEntry()
    {
        var h = Make(new List<string> { "old command" });
        h.Handle(Key(ConsoleKey.UpArrow));
        Assert.Equal("old command", h.Text);
    }

    [Fact]
    public void CtrlP_BehavesLikeUpArrow()
    {
        var h = Make(new List<string> { "old command" });
        h.Handle(Ctrl(ConsoleKey.P));
        Assert.Equal("old command", h.Text);
    }

    [Fact]
    public void UpArrow_MultipleHistory_NavigatesBackward()
    {
        var h = Make(new List<string> { "first", "second" });
        h.Handle(Key(ConsoleKey.UpArrow));
        h.Handle(Key(ConsoleKey.UpArrow));
        Assert.Equal("first", h.Text);
    }

    [Fact]
    public void UpArrow_AtOldestEntry_DoesNotWrap()
    {
        var h = Make(new List<string> { "only" });
        h.Handle(Key(ConsoleKey.UpArrow));
        h.Handle(Key(ConsoleKey.UpArrow)); // extra press — should stay at "only"
        Assert.Equal("only", h.Text);
    }

    [Fact]
    public void DownArrow_AfterUp_MovesForward()
    {
        var h = Make(new List<string> { "first", "second" });
        h.Handle(Key(ConsoleKey.UpArrow));
        h.Handle(Key(ConsoleKey.UpArrow));
        h.Handle(Key(ConsoleKey.DownArrow));
        Assert.Equal("second", h.Text);
    }

    [Fact]
    public void DownArrow_PastNewest_ClearsLine()
    {
        var h = Make(new List<string> { "old" });
        h.Handle(Key(ConsoleKey.UpArrow));
        h.Handle(Key(ConsoleKey.DownArrow));
        Assert.Equal("", h.Text);
    }

    [Fact]
    public void CtrlN_BehavesLikeDownArrow()
    {
        var h = Make(new List<string> { "old" });
        h.Handle(Key(ConsoleKey.UpArrow));
        h.Handle(Ctrl(ConsoleKey.N));
        Assert.Equal("", h.Text);
    }

    // --- InsertText ---

    [Fact]
    public void InsertText_InsertsString()
    {
        var h = Make();
        h.InsertText("hello");
        Assert.Equal("hello", h.Text);
    }

    [Fact]
    public void InsertText_SkipsCarriageReturn()
    {
        var h = Make();
        h.InsertText("a\rb");
        Assert.Equal("ab", h.Text);
    }

    [Fact]
    public void InsertText_PreservesNewlines()
    {
        var h = Make();
        h.InsertText("a\nb");
        Assert.Equal("a\nb", h.Text);
    }

    [Fact]
    public void InsertText_AtMidpoint_InsertsInPlace()
    {
        var h = Make();
        Type(h, "ac");
        MoveLeft(h);
        h.InsertText("b");
        Assert.Equal("abc", h.Text);
    }

    // --- transpose ---

    [Fact]
    public void CtrlT_AtEnd_TransposesLastTwoChars()
    {
        var h = Make();
        Type(h, "ab");
        h.Handle(Ctrl(ConsoleKey.T));
        Assert.Equal("ba", h.Text);
    }

    [Fact]
    public void CtrlT_AtMidpoint_SwapsCharBeforeAndAtCursor()
    {
        // Cursor between 'b' and 'c': transposes char-before-point ('b') with char-at-point ('c')
        var h = Make();
        Type(h, "abc");
        MoveLeft(h);
        h.Handle(Ctrl(ConsoleKey.T));
        Assert.Equal("acb", h.Text);
    }

    [Fact]
    public void CtrlT_AtStart_DoesNothing()
    {
        var h = Make();
        Type(h, "ab");
        h.Handle(Key(ConsoleKey.Home));
        h.Handle(Ctrl(ConsoleKey.T));
        Assert.Equal("ab", h.Text);
    }

    // --- soft-wrap + history regression repro ---

    private class GridConsole : IConsoleAdapter
    {
        public int CursorLeft { get; set; }
        public int CursorTop { get; set; }
        public int BufferWidth { get; init; } = 10;
        public int BufferHeight { get; init; } = 40;
        private readonly char[,] _cells = new char[40, 10];

        public GridConsole()
        {
            for (int r = 0; r < 40; r++)
                for (int c = 0; c < 10; c++)
                    _cells[r, c] = ' ';
        }

        public void SetCursorPosition(int left, int top) { CursorLeft = left; CursorTop = top; }

        public void Write(string value)
        {
            foreach (char c in value)
            {
                if (c == '\n') { CursorTop++; CursorLeft = 0; }
                else
                {
                    _cells[CursorTop, CursorLeft] = c;
                    if (++CursorLeft >= BufferWidth) { CursorLeft = 0; CursorTop++; }
                }
            }
        }

        public string Row(int r)
        {
            var buf = new char[BufferWidth];
            for (int c = 0; c < BufferWidth; c++) buf[c] = _cells[r, c];
            return new string(buf);
        }
    }

    [Fact]
    public void UpArrow_AfterWordWrapMovedText_ClearsOrphanedCells()
    {
        // Repro for the "Up arrow replaces only the current line" bug.
        // When word-wrap moves a partial word to the next line, the preceding
        // text on the prior row is left behind. ClearLine's Backspace walk
        // only revisits cells along the cursor path, so those "orphaned"
        // cells survive and the user sees old text alongside the new entry.
        var console = new GridConsole();
        console.SetCursorPosition(2, 0);
        var h = new KeyHandler(console, new List<string> { "hi" });

        foreach (char c in "hello world this is a test") h.Handle(Char(c));

        h.Handle(Key(ConsoleKey.UpArrow));

        Assert.Equal("hi", h.Text);
        var allRows = string.Join("|", Enumerable.Range(0, 4).Select(r => console.Row(r)));
        var hiCount = System.Text.RegularExpressions.Regex.Matches(allRows, "hi").Count;
        Assert.Equal(1, hiCount);
        var withoutHi = allRows.Replace("hi", "  ");
        Assert.Equal(string.Join("|", Enumerable.Repeat(new string(' ', 10), 4)), withoutHi);
    }

    [Fact]
    public void UpArrow_AfterSoftWrap_ClearsAllRowsVisitedDuringTyping()
    {
        // Repro: buffer width 10, 2-char "prompt" offset, type a long non-space
        // sequence that wraps across 3 visual rows, then UpArrow to a short
        // history entry. Every row touched during typing should be erased, and
        // the new entry should appear exactly once.
        var console = new GridConsole();
        console.SetCursorPosition(2, 0);
        var h = new KeyHandler(console, new List<string> { "hi" });

        // 20 non-space chars: word-wrap moves the first 7 to row 1, remaining
        // chars fill row 1 and spill into row 2.
        foreach (char c in new string('A', 20)) h.Handle(Char(c));
        Assert.Equal(new string('A', 20), h.Text);

        h.Handle(Key(ConsoleKey.UpArrow));

        Assert.Equal("hi", h.Text);
        var allRows = string.Join("|", new[] { console.Row(0), console.Row(1), console.Row(2) });
        // Exactly one "hi" anywhere in those three rows, everything else spaces
        var hiCount = System.Text.RegularExpressions.Regex.Matches(allRows, "hi").Count;
        Assert.Equal(1, hiCount);
        var withoutHi = allRows.Replace("hi", "  ");
        Assert.Equal(new string(' ', 10) + "|" + new string(' ', 10) + "|" + new string(' ', 10), withoutHi);
    }
}
