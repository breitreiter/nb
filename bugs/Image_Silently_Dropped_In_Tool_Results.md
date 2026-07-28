# Images are silently dropped on OpenAI-wire providers

Status: Open (2026-07-26) — found while testing nb against a local llama.cpp
server. Not a local-model quirk; affects every OpenAI-wire provider.

Candidate 1 was drafted and partially verified on 2026-07-28, then parked
unfinished — see **Findings (2026-07-28)** below before restarting.

---

## Symptom

`read_file` on a `.png`/`.jpg` reports success and the model answers confidently
about the image — but it never saw a single pixel. No error is raised anywhere.

Test fixture: an **8×8, entirely pure-red** PNG (75 bytes).

| file name given to the model | model's answer | actual |
|---|---|---|
| `red.png` | "1x1 pixel red image, RGB(255,0,0)" | 8×8, red |
| `sample.png` (byte-identical copy) | "1×1 pixels, **White** (255,255,255)" | 8×8, red |

Renaming the file changed the answer, which is the whole finding: the model is
answering from the **filename**, not the image. With the hint removed it gets
the colour flatly wrong while still asserting *"I can see the image data."*

nb prints `→ image (75 bytes)` either way, which reads as success.

## This is not the server refusing

llama.cpp rejects image content properly. Posting a real `image_url` part
straight at it:

```
POST /v1/chat/completions   {"type":"image_url","image_url":{"url":"data:image/png;base64,…"}}
→ HTTP 500  {"error":{"message":"image input is not supported - hint: if this is
             unexpected, you may need to provide the mmproj", ...}}
```

So a text-only model **does** produce a loud, correct error when it actually
receives an image. nb produced no error at all — which proves the image never
reached the wire as an image.

## Cause

`ConversationManager.cs:403-412` attaches the image to a **tool result**:

```csharp
var imageContent = new DataContent(imageBytes, readResult.MimeType!);
var textNote = new TextContent($"[Image loaded: {Path.GetFileName(path)} ({...} bytes)]");
allToolResults.Add(new FunctionResultContent(functionCall.CallId,
    new List<AIContent> { textNote, imageContent }));
```

The OpenAI chat-completions wire format has **no representation for an image
inside a `tool`-role message**. Image parts are only valid on `user` messages.
So when M.E.AI's OpenAI adapter serializes this `FunctionResultContent`, the
`DataContent` has nowhere to go and is dropped; only `textNote` survives — and
`textNote` contains the filename, which is exactly what the model then answers
from.

The failure is in the *shape* of the message, not in the provider, the model, or
the image-reading code. `ReadFileTool.ReadImage` is fine: correct MIME type,
correct base64, 20 MB guard.

## Scope

Affects every provider using the OpenAI wire format — **OpenAI, AzureOpenAI,
AzureFoundry, LocalLlm** — regardless of whether the model behind it has vision.
A vision-capable model reached through `LocalLlm` (or GPT-4o through `OpenAI`)
would be just as blind here, which makes this worth fixing rather than
documenting as a local-model limitation.

**Anthropic is probably unaffected** — its API *does* allow image blocks inside
`tool_result` — which would explain why this went unnoticed: prior testing was
against Anthropic and OpenAI endpoints, and only the Anthropic half of that can
work. **Untested; verify before relying on it.**

## Repro

1. Point `ActiveProvider` at any OpenAI-wire provider.
2. Create an 8×8 solid-red PNG named `sample.png` (no colour hint in the name).
3. `./nb --trust "Read sample.png and tell me its dimensions and dominant colour.
   If you cannot actually see the image, say so."`
4. Observed: confident, wrong answer. Expected: either the real answer, or an
   explicit "this provider can't receive images".

The name matters — using `red.png` masks the bug, because the model guesses
correctly from the filename.

## Fix candidates

1. **Send the image as its own `user` message.** Keep the text note in the tool
   result, then append a synthetic user message carrying the `DataContent`. This
   is the standard workaround for the OpenAI format's lack of images in tool
   results, and it makes the image genuinely visible to vision-capable models.
   Ordering matters: it must land after the tool result and before the next
   assistant turn.

2. **Fail loudly instead of silently.** If the active provider is OpenAI-shaped
   and option 1 isn't implemented, have `read_file` return an error for image
   files rather than a success the model will confabulate over. A wrong answer
   with no warning is worse than a refusal.

3. **At minimum, stop printing `→ image (N bytes)` as success** when the content
   won't survive serialization. That line is the reason this looks like it works.

Worth doing 3 regardless — it is what made the bug invisible.

---

## Findings (2026-07-28)

Candidate 1 was implemented against `ConversationManager.cs` and parked before
completion. The work is in a local `git stash` on master, which is not durable —
treat the notes below as the record, not the stash.

### Confirmed: the approach reaches the wire

With the fix applied, the report's own repro against GLM-4.5-Air on llama.cpp
(text-only, no mmproj) produces:

```
• reading sample.png
  → image (75 bytes), attached below
Error: Service request failed.
Status: 500 (Internal Server Error)
```

That is this report's own test for success: a text-only server produces a loud,
correct error *only* when it actually receives an image. Before the fix the same
repro returned a confident wrong answer with no error. So the image now leaves
nb as an image.

Note the consequence — on a text-only model, `read_file` on an image now hard
fails the turn rather than quietly confabulating. That is the intended trade per
candidate 2 ("a wrong answer with no warning is worse than a refusal"), but it
is a behaviour change worth stating in release notes.

### Not confirmed: the positive path

Nobody has yet shown the image serializing as an `image_url` part on a **user**
message, or a vision-capable model reading it correctly. The 500 proves the
image is on the wire; it does not prove the shape is right. This is the gap to
close first on any restart.

### The shape that was implemented

- Tool result becomes **text-only** — `[Image loaded: <name> (<n> bytes). The
  image itself follows in the next message.]`
- Pixels ride a synthetic `user` message appended immediately after the tool
  message, carrying a `TextContent` note plus the `DataContent` parts.
- Ordering is enforced by appending at the tool-result join
  (`_conversationHistory.Add(new AIChatMessage(ChatRole.Tool, allToolResults))`),
  not at read time.
- The note tells the model to say so if it cannot see the image, rather than
  inferring from the filename — the specific failure this bug documents.
- Runs **unconditionally**, with no provider sniffing. A user message is valid on
  every provider, so this needs no capability flag. Anthropic does allow images
  inside `tool_result`, but sending them in both places would only duplicate
  tokens.

Token accounting needed no change: `EstimateTokens` and `CompactHistoryAsync`
already charge `DataContent` a flat 3500 chars wherever it sits.

### Pre-existing, not a regression: history drops images

`ExtractMessageContent` returns the first `TextContent` and nothing else, so a
persisted `.nb_conversation_history.json` keeps only the text note either way.
An image does not survive a save/load cycle before or after this fix. Possibly
moot — see `TODO.md` on retiring the history machinery.

### Test-harness notes (this is what cost the time)

- **nb block-buffers stdout when redirected.** Piping to `tail` or `>` and then
  killing on timeout loses everything buffered, which reads as "nb printed
  nothing and hung". Run it under a pty — `script -q -c '<cmd>' /dev/null` — or
  you will misdiagnose your own test rig.
- **A stub OpenAI endpoint did not work as a capture harness.** Pointed at a
  local Python `HTTPServer` returning a canned completion, nb never issued the
  POST and hung after the trust banner. Unresolved; suspect streaming
  (nb expects SSE, the stub answered with a plain JSON body). If retried, answer
  with SSE and use `ThreadingHTTPServer`.
- The history lock was ruled out as the cause — `TryAcquire` failure is
  non-blocking and only skips load/save.
- Fixture: an 8×8 pure-red PNG is 75 bytes and can be generated with `zlib` +
  `struct` alone, no imaging library. The filename must carry no colour hint, or
  the model guesses correctly and masks the bug.

### Where this should land

`ConversationManager.cs` moves to `nb.Core/ConversationManager.cs` on the
`conversation-program` branch, which rewrites the surrounding tool loop. If that
branch is close to landing, reimplement there rather than porting a master-side
patch across the restructure.
