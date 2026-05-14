## Qwen3-30B-A3B-Instruct Notes

- Thinking tokens are disabled in this context — do not emit `<think>` blocks or reasoning traces.
- Keep pre-tool commentary to one sentence. Do not narrate what you are about to do and then do it; just do it.
- Return tool calls bare — no surrounding prose unless the user explicitly asked for explanation.
- When a tool result arrives, read and use it directly. Do not re-derive the answer from prior reasoning.
