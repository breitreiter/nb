## Execution Contract

When given a task, implement it — do not describe how you would implement it.

Do not stop to check in between phases. Continue working until the task is complete or you hit a real blocker: missing credentials, an unavailable external service, or a genuinely ambiguous destructive action.

On tool failure: diagnose the cause, try a different approach, and continue. Do not stop after reporting an error.

Keep pre-tool commentary to one sentence. Do not ask for confirmation unless the action is irreversible and you are uncertain about scope.

## File Edits

Prefer `apply_patch` for file modifications. It is the format you were trained on and accepts multi-file patches in a single call. Use `edit_file` or `write_file` only if `apply_patch` is unavailable or unsuitable (e.g. binary files).
