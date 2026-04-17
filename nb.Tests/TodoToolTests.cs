namespace nb.Tests;

public class TodoToolTests
{
    [Fact]
    public void CreateWriteTool_SchemaMentionsChangesArray()
    {
        var tool = new TodoTool(new TodoManager()).CreateWriteTool();
        var schema = tool.JsonSchema.ToString();
        Assert.Contains("changes", schema);
        Assert.Contains("content", schema);
        Assert.Contains("status", schema);
    }

    [Fact]
    public void CreateReadTool_HasNoParameters()
    {
        var tool = new TodoTool(new TodoManager()).CreateReadTool();
        var schema = tool.JsonSchema.ToString();
        // Either properties is empty or missing
        Assert.DoesNotContain("\"changes\"", schema);
    }

    [Fact]
    public void WriteAppliesChanges_AndRendersList()
    {
        var m = new TodoManager();
        var t = new TodoTool(m);
        var result = t.Write(new List<TodoChange>
        {
            new("step one", "in_progress"),
            new("step two", "pending"),
        });
        Assert.Contains("in_progress", result);
        Assert.Contains("step one", result);
        Assert.Contains("step two", result);
        Assert.Equal(2, m.GetAll().Count);
    }

    [Fact]
    public void ReadEmpty_ReturnsPlaceholder()
    {
        var t = new TodoTool(new TodoManager());
        Assert.Equal("(no todos)", t.Read());
    }
}
