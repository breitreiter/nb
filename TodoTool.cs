using Microsoft.Extensions.AI;

namespace nb;

public class TodoTool
{
    private readonly TodoManager _manager;

    public TodoTool(TodoManager manager)
    {
        _manager = manager;
    }

    public AIFunction CreateWriteTool()
    {
        var writeFunc = (List<TodoChange> changes) => Write(changes);

        return AIFunctionFactory.Create(
            writeFunc,
            name: "todo_write",
            description: """
                Create or update the session's task checklist. Use this to plan multi-step work
                and track progress as you go. Highly recommended when a task has 3+ distinct steps,
                or when the user hands you a broad spec — write the checklist FIRST, then execute.

                ## How it works
                Each change has `content` (unique key, the task description) and `status`:
                  - `pending`        — not yet started
                  - `in_progress`    — currently working on (keep exactly one at a time)
                  - `completed`      — done
                  - `cancelled`      — removes the item from the list

                Only send the items that changed. Items you don't mention stay as-is.
                  - Unknown `content` → added as a new task
                  - Known `content` → status updated
                  - `cancelled` → removed

                ## When to use
                - Multi-step features or refactors (3+ discrete steps)
                - Broad specs that need to be decomposed before acting
                - Any time the user explicitly asks for a checklist or plan
                - To record new sub-tasks discovered mid-task

                ## When NOT to use
                - Single-step tasks ("read this file", "what does X do")
                - Pure Q&A with no code changes
                - Tasks that finish in under 3 steps

                ## Rules
                - Mark a task `in_progress` BEFORE you start work on it, not after.
                - Mark `completed` IMMEDIATELY when it's done. Don't batch completions.
                - Only ONE task should be `in_progress` at a time.
                - Don't mark completed if tests are failing, implementation is partial, or you're blocked.
                - If you're blocked, keep the task `in_progress` and add a new task describing the blocker.

                Parameters:
                - changes: array of { content: string, status: string }
                """);
    }

    public AIFunction CreateReadTool()
    {
        var readFunc = () => Read();

        return AIFunctionFactory.Create(
            readFunc,
            name: "todo_read",
            description: """
                Read the current session todo list. Use this to check what's pending,
                what's in progress, and avoid duplicating work you've already captured.
                Returns the list in "- [status] content" format, or "(no todos)" if empty.
                """);
    }

    public string Write(List<TodoChange> changes)
    {
        if (changes == null || changes.Count == 0)
        {
            return "No changes submitted.\n\nCurrent list:\n" + _manager.Render();
        }

        var applied = _manager.ApplyChanges(changes);
        var current = _manager.Render();
        return $"Changes applied:\n{string.Join("\n", applied)}\n\nCurrent list:\n{current}";
    }

    public string Read() => _manager.Render();
}
