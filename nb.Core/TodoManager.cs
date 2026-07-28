namespace nb;

public enum TodoStatus
{
    Pending,
    InProgress,
    Completed,
    Cancelled,
}

public class Todo
{
    public required string Content { get; init; }
    public TodoStatus Status { get; set; }
}

/// <summary>
/// Input record for todo_write tool calls. Content is the unique key:
/// unknown content → added, known content → status updated, cancelled → removed.
/// </summary>
public record TodoChange(string Content, string Status);

/// <summary>
/// Session-scoped task checklist the model uses to plan and track multi-step work.
/// Content is the unique key — only send changed items, not the whole list.
/// Cleared by /clear; does not persist across processes.
/// </summary>
public class TodoManager
{
    private readonly List<Todo> _todos = new();

    public IReadOnlyList<Todo> GetAll() => _todos;

    public IReadOnlyList<Todo> GetActive() =>
        _todos
            .Where(t => t.Status is TodoStatus.Pending or TodoStatus.InProgress)
            .ToList();

    public void Reset() => _todos.Clear();

    public List<string> ApplyChanges(IList<TodoChange> changes)
    {
        var applied = new List<string>();
        foreach (var c in changes)
        {
            if (string.IsNullOrWhiteSpace(c.Content))
            {
                applied.Add("[ERROR] Empty content, skipped");
                continue;
            }
            if (!TryParseStatus(c.Status, out var status))
            {
                applied.Add($"[ERROR] Invalid status '{c.Status}' for '{c.Content}' — use pending | in_progress | completed | cancelled");
                continue;
            }

            var existing = _todos.FirstOrDefault(t => t.Content == c.Content);

            if (status == TodoStatus.Cancelled)
            {
                if (existing != null)
                {
                    _todos.Remove(existing);
                    applied.Add($"[CANCELLED] {c.Content}");
                }
                else
                {
                    applied.Add($"[SKIP] not found: {c.Content}");
                }
            }
            else if (existing == null)
            {
                _todos.Add(new Todo { Content = c.Content, Status = status });
                applied.Add($"[ADDED {StatusLabel(status)}] {c.Content}");
            }
            else
            {
                existing.Status = status;
                applied.Add($"[UPDATED {StatusLabel(status)}] {c.Content}");
            }
        }
        return applied;
    }

    public string Render()
    {
        if (_todos.Count == 0) return "(no todos)";
        return string.Join("\n", _todos.Select(t => $"- [{StatusLabel(t.Status)}] {t.Content}"));
    }

    private static bool TryParseStatus(string s, out TodoStatus status)
    {
        switch (s?.Trim().ToLowerInvariant())
        {
            case "pending":
                status = TodoStatus.Pending;
                return true;
            case "in_progress":
            case "inprogress":
            case "in-progress":
                status = TodoStatus.InProgress;
                return true;
            case "completed":
            case "done":
                status = TodoStatus.Completed;
                return true;
            case "cancelled":
            case "canceled":
                status = TodoStatus.Cancelled;
                return true;
            default:
                status = default;
                return false;
        }
    }

    public static string StatusLabel(TodoStatus s) => s switch
    {
        TodoStatus.Pending => "pending",
        TodoStatus.InProgress => "in_progress",
        TodoStatus.Completed => "completed",
        TodoStatus.Cancelled => "cancelled",
        _ => s.ToString().ToLowerInvariant(),
    };
}
