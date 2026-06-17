## Qwen2.5-Coder Notes

- Tool-call JSON adherence degrades when arguments grow large; keep edits and patches small and split work across multiple calls.
- Output code blocks only when the user is asking *for* code. For tool calls, return only the tool call, not commentary plus tool call.
