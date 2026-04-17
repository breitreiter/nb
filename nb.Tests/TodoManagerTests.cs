namespace nb.Tests;

public class TodoManagerTests
{
    [Fact]
    public void Empty_RendersPlaceholder()
    {
        var m = new TodoManager();
        Assert.Equal("(no todos)", m.Render());
        Assert.Empty(m.GetAll());
        Assert.Empty(m.GetActive());
    }

    [Fact]
    public void AddingNewItems_CreatesThem()
    {
        var m = new TodoManager();
        var report = m.ApplyChanges(new List<TodoChange>
        {
            new("write detector", "pending"),
            new("wire into ConversationManager", "pending"),
        });
        Assert.Equal(2, m.GetAll().Count);
        Assert.Equal(2, m.GetActive().Count);
        Assert.Contains(report, r => r.Contains("ADDED") && r.Contains("write detector"));
    }

    [Fact]
    public void UpdatingByContent_ChangesStatus()
    {
        var m = new TodoManager();
        m.ApplyChanges(new List<TodoChange> { new("build feature X", "pending") });
        m.ApplyChanges(new List<TodoChange> { new("build feature X", "in_progress") });
        Assert.Single(m.GetAll());
        Assert.Equal(TodoStatus.InProgress, m.GetAll()[0].Status);
    }

    [Fact]
    public void CancelledStatus_RemovesItem()
    {
        var m = new TodoManager();
        m.ApplyChanges(new List<TodoChange>
        {
            new("task a", "pending"),
            new("task b", "pending"),
        });
        m.ApplyChanges(new List<TodoChange> { new("task a", "cancelled") });
        Assert.Single(m.GetAll());
        Assert.Equal("task b", m.GetAll()[0].Content);
    }

    [Fact]
    public void CompletedItems_NotActive()
    {
        var m = new TodoManager();
        m.ApplyChanges(new List<TodoChange>
        {
            new("done task", "completed"),
            new("pending task", "pending"),
        });
        Assert.Equal(2, m.GetAll().Count);
        Assert.Single(m.GetActive());
        Assert.Equal("pending task", m.GetActive()[0].Content);
    }

    [Fact]
    public void PartialUpdate_PreservesOtherItems()
    {
        var m = new TodoManager();
        m.ApplyChanges(new List<TodoChange>
        {
            new("a", "pending"),
            new("b", "pending"),
            new("c", "pending"),
        });
        // Only send change for "b"
        m.ApplyChanges(new List<TodoChange> { new("b", "completed") });
        Assert.Equal(3, m.GetAll().Count);
        Assert.Equal(TodoStatus.Pending, m.GetAll().First(t => t.Content == "a").Status);
        Assert.Equal(TodoStatus.Completed, m.GetAll().First(t => t.Content == "b").Status);
        Assert.Equal(TodoStatus.Pending, m.GetAll().First(t => t.Content == "c").Status);
    }

    [Fact]
    public void InvalidStatus_ReportedNotApplied()
    {
        var m = new TodoManager();
        var report = m.ApplyChanges(new List<TodoChange> { new("x", "bogus") });
        Assert.Empty(m.GetAll());
        Assert.Contains(report, r => r.StartsWith("[ERROR]"));
    }

    [Fact]
    public void StatusAliases_Accepted()
    {
        var m = new TodoManager();
        m.ApplyChanges(new List<TodoChange>
        {
            new("a", "InProgress"),
            new("b", "DONE"),
            new("c", "canceled"),
        });
        Assert.Equal(2, m.GetAll().Count); // c was cancelled (not found, so skipped)
        Assert.Equal(TodoStatus.InProgress, m.GetAll().First(t => t.Content == "a").Status);
        Assert.Equal(TodoStatus.Completed, m.GetAll().First(t => t.Content == "b").Status);
    }

    [Fact]
    public void Reset_ClearsAll()
    {
        var m = new TodoManager();
        m.ApplyChanges(new List<TodoChange> { new("x", "pending") });
        m.Reset();
        Assert.Empty(m.GetAll());
    }
}
