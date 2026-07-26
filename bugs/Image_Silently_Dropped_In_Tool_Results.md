# Images are silently dropped on OpenAI-wire providers

Status: Open (2026-07-26) — found while testing nb against a local llama.cpp
server. Not a local-model quirk; affects every OpenAI-wire provider.

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
