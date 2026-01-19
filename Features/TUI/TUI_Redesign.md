# TUI Redesign

Status: In Progress (Steps 1-2 complete)

## Research Notes (2026-01-19)

### Spectre.Console Integration Scope

Codebase analysis shows Spectre usage is lighter than expected:
- **9 files** use Spectre.Console
- **~70 `MarkupLine()` calls** - colored output, straightforward to replace
- **~40 `Markup.Escape()` calls** - trivial string escaping
- **4-5 interactive prompts** - `Confirm()`, `Ask()`, `Prompt()` for tool approvals

**Not used:** Tables, panels, progress bars, status spinners, grids, or any complex UI features. This is good news - we're essentially just removing a formatting library.

### Terminal.Gui v2 Color Handling

Terminal.Gui v2 supports 24-bit truecolor via RGB constructors:
```csharp
Color customColor = new Color(0xFF, 0x99, 0x00);  // RGB values
Color namedColor = Color.Yellow;                   // ANSI named colors
```

The library detects truecolor support at runtime (`SupportsTrueColorOutput`) and can fall back gracefully.

### Neutral Color Format Decision

**Use hex codes** (e.g., `#E87017`) as the neutral format for `UIColors`:
- Universal and human-readable
- Trivially converts to Terminal.Gui: parse hex → `new Color(r, g, b)`
- Trivially converts to ANSI 24-bit: `\u001b[38;2;R;G;Bm`
- Familiar CSS/web standard

Current Spectre color names like `deepskyblue4_1` will be replaced with their hex equivalents. The semantic color properties (`SpectreSuccess`, `SpectreError`, etc.) stay, but become `Success`, `Error`, etc. returning hex strings.

### Console I/O Entanglement (Step 2.5 Problem)

After completing steps 1-2, we discovered a deeper architectural issue: **~100 `Console.Read/Write` calls are scattered across 9 files**, mixed directly into business logic.

**Output calls (~70%)** - status messages, errors, help text. These are annoying but tractable.

**Blocking input calls (~30%)** - the real problem:
- `ConversationManager.cs`: 4+ approval loops using `Console.ReadKey()`
- `PromptProcessor.cs`: MCP parameter collection
- `Program.cs`: Main input loop

Terminal.Gui is event-driven. You show a dialog, attach handlers, return to the event loop. The current code does synchronous blocking reads in the middle of business logic. The control flow needs to invert.

**Key insight:** Single-shot mode should *never* block waiting for input. If a tool call needs approval that wasn't pre-approved via `--approve`, it should fail immediately with a non-zero exit code. Hanging silently in a headless script is the worst possible behavior.

**Proposed approach:** Accept that interactive and single-shot modes need genuinely different I/O strategies:
- **Single-shot:** Pure Console output, no blocking reads, fail-fast on unapproved actions
- **Interactive:** Event-driven Terminal.Gui with dialogs for approvals

This means step 2.5 is architectural surgery to separate the conversation/tool logic from I/O concerns before Terminal.Gui can be integrated.

### Work Estimate (Revised)

| Phase | Scope | Estimate |
|-------|-------|----------|
| Step 1-2 (Spectre removal) | Replace markup, escape calls, prompts; convert UIColors to hex | 1 day |
| Step 2.5 (I/O separation) | Extract blocking reads from business logic, fail-fast for single-shot | 2-3 days |
| Step 3 (Terminal.Gui shell) | Two-pane layout, input handling, scroll region | 1-2 days |
| Step 4-5 (Test & iterate) | Ongoing | Variable |

**Revised verdict:** This is a multi-day project. Steps 1-2 done. Step 2.5 is significant refactoring.

### Branching Strategy

Work in a `tui-redesign` branch. Rationale:
- Ripping out Spectre will temporarily degrade the UI (monochrome, raw prompts)
- Low user count, but those users shouldn't see a broken-looking tool
- Clean separation allows testing the new UI without pressure

## Problem

In practice, nb has two broad uses:
- An automation-friendly, text-first chat client. This is nearly always done with single-shot mode.
- An interactive shell for AI-assisted terminal work. This is nearly always done with interactive mode.

This wasn't part of the original design of nb, it's just a clear division of usage that has manifested. 

The current UI presents some problems here:
- The ANSI coloring and modest animations do little to benefit automation and mostly introduce bugs.
- The simple UI is scaling poorly as new features are added. This is most evident when making complex tool calls. Debug information and approvals are mixed into conversations, making both less legible.

## A New Philosophy

Single-shot mode should be stripped of adornment and optimized for consumption by other programs. In this mode, nb should be a well-behaved UNIX-style command for generating a text completion (possibly with tool calls).

Interactive mode should be a rich application built to take advantage of modern, high-resolution displays. nb will remain a terminal app mostly for brand differentiation. The world doesn't need yet another web chat app. However, we'll push the form as far as it can go.

## How we get there

This will need to happen in a few distinct steps.

1. **[DONE]** Rip out Spectre. We will know this is done when we compile without the Spectre library and all core interface elements continue to work (albeit in monochrome). Convert `UIColors` to use hex codes as a neutral format - this preserves the semantic color system while decoupling from Spectre's color names.
2. **[DONE]** Rip out wait animations and any emojis generated by nb itself. We can (and should) still display/return/transport/store any double-byte characters returned by a model or tool call.
3. **[NEW]** Separate I/O from business logic. Single-shot mode must never block for input - unapproved tool calls should fail with non-zero exit. Interactive mode needs an event-driven approval flow. This is prerequisite work for Terminal.Gui integration.
4. Create an extremely simple two-pane interactive UI based on Terminal.Gui v2. This should consist of a fixed input section on the bottom of the screen and a scrolling output section on top. Terminal.Gui v2 specifically chosen for its truecolor (24-bit RGB) support - we want the TUI to look distinctive, not like a stock demo.
5. Pause and test
6. Iteratively improve the new TUI UX based on actual usage