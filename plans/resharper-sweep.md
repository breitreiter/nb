---
kind: plan
title: The ReSharper sweep — triage, and making the next one cheap
created: 2026-08-15
updated: 2026-08-15
status: current
state: active
touches:
  files:
    - nb.Core/ConversationManager.cs
    - nb.Core/ProviderManager.cs
    - nb.Core/Shell/ShellEnvironment.cs
    - nb.Core/Utilities/ConfigurationService.cs
    - nb.sln.DotSettings
    - TODO.md
  features: [codebase-hygiene, dead-code, tooling]
provenance:
  author: claude
  source: TODO.md "Run a ReSharper pass over the solution, starting with dead code"
---

# The ReSharper sweep — triage, and making the next one cheap

## Why this plan exists

`jb inspectcode nb.sln` produced **522 findings** (311 note, 211 warning) on 2026-08-15.
That is too many to work through as a list and too few to ignore. This plan says which
ones are real, what decides that, and — the part that makes it worth writing down — how
to stop the next sweep re-litigating the same false positives.

The sweep was prompted by `ShellEnvironment.BuildSystemPromptSection()` being found dead
*by accident*. The point was never this one sweep; it is having a repeatable way to ask
the question.

## What already landed

Commit `6b58c40` took the unambiguous findings, so this plan starts from the current
tree, not the raw report:

- All 11 `NotAccessedField.Local` hits — ten write-only tool mirrors in
  `ConversationManager` plus its `_trustMode`, dead since dispatch moved into
  `NbHarness`.
- The single `InvalidXmlDocComment`, which turned out to be half of a doc comment torn
  apart by the same move.

A rerun should therefore report ~510, not 522. **Rerun before starting any item below** —
the counts here are pre-commit.

## The real deliverable: the next sweep

Roughly **40 of the 522 are structurally wrong and always will be.** They are not noise
to skim past once; they are noise that returns identically every time anyone runs the
tool, and paying that triage cost repeatedly is what turns a sweep into a thing nobody
does. Three families:

1. **The published library surface.** Every `NbProgramBuilder` method (`Provider`,
   `Model`, `System`, `User`, `Assistant`, `Loop`, `LoopOff`, `Add`, `Build`) and several
   `NbOptions` properties are flagged `UnusedMember.Global` / `UnusedAutoPropertyAccessor.Global`.
   They are the contract in `docs/conversation-program-api.md`, consumed out-of-tree. The
   in-tree call count is *supposed* to be zero.
2. **JSON deserialization targets.** `McpServerConfig`, `FakeToolConfig`, `FakeTool`,
   `FakeToolParameter` — "never instantiated" because `System.Text.Json` constructs them.
3. **Reflection and plugin loading.** The mcp-tester `TestTools` / `ChaosTools` and their
   methods (attribute-discovered), both `Program` entry points, and everything under
   `Providers/` reached through `AssemblyLoadContext`.

**Deliverable: a checked-in `nb.sln.DotSettings`** that turns these off at the source —
by rule where the rule is globally wrong for this repo, by file/namespace mask where it
is only wrong in one place. Where a suppression is not expressible as a mask, use a
`[UsedImplicitly]` or `[PublicAPI]` annotation at the declaration, which has the side
benefit of telling a human reader the same thing.

Do this **first**. It is what makes items 2 and 3 a short list instead of a long one, and
it is the difference between a sweep and a habit.

## The criterion for an "unused" symbol

The TODO says each hit is a decision — delete, wire up, or keep-and-document — but not how
to choose. Apply in order:

1. **Named in `docs/conversation-program-api.md`?** → published contract. Keep, suppress,
   never delete. This is the discriminator that does most of the work.
2. **Reached by reflection, JSON, or plugin load?** → keep, annotate at the declaration.
3. **Surface from a life nb no longer has** (chat mode, `CommandProcessor`,
   slash-commands, kit persistence, durable history)? → delete.
4. **A capability someone meant to wire and never did?** → a real decision, and the only
   category that needs a human. `BuildSystemPromptSection` is the worked example: deleting
   it silently would have been wrong.

**Apply step 1 on the exact member, not a substring.** `DoomLoopDetector.Threshold` looks
published because `docs/conversation-program-api.md` mentions `Threshold` three times —
all of them `NbOptions.DoomLoopThreshold`, a different symbol. A grep is a hint, not the
answer.

## Work items

### 1. Suppression layer *(do first)*

`nb.sln.DotSettings` covering the three families above, plus annotations where a mask
cannot express it. Verification: rerun `jb inspectcode`; the three families are gone and
nothing else disappeared with them. Record the new baseline count in `TODO.md`.

### 2. Unused-symbol triage — roughly 15 decisions

What survives suppression. Grouped by expected disposition, but each still gets checked
against the criterion:

- **Expected delete (chat-era surface, no caller):** `ProviderManager.GetAvailableProviders`,
  `ShowProvidersWithStatus`, `ShowProviderStatus` (the old `/provider` command);
  `ConversationManager.SendMessageAsync` and `ClearConversationHistory`;
  `ConfigurationService.GetSystemPrompt`. None appear in the API doc — checked.
  `ClearConversationHistory` needs a note: the durable-state cull explicitly *kept* it
  (`TODO.md`, "durable-state cull"), so deleting it reverses a recorded decision and
  should say so.
- **Expected delete (leftovers from the harness work itself):**
  `QwenCodeHarness.MillisecondsToSeconds`, private and uncalled — residue from
  `bugs/Bash_Advertises_A_Timeout_It_Ignores.md`.
- **Small fry, decide as a batch:** `FileReadTracker.Clear`, `WriteFileTool.GetCwd`,
  `TranscriptSerializer.Write`, `DoomLoopDetector.Threshold`, `UIColors.SpectreAccent`,
  `ShellEnvironment.LaunchDirectory`, `ApprovalPolicy.TrustMode`, and four
  `FakeToolManager` accessors (`GetOverriddenTools`, `GetFakeToolNames`, `ToolsLoaded`,
  `ToolsOverridden`).
- **Already open, unchanged by this plan:** `ShellEnvironment.BuildSystemPromptSection`
  (`TODO.md`, Harness emulation). Category 4 — leave it there.

Land as one commit, separate from item 3.

### 3. The nullable-contract cluster — an investigation, not a cleanup

~17 findings, heavily concentrated in `ConversationManager`: 5
`ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract`, 6
`ConditionalAccessQualifierIsNonNullable…`, 6 `NullCoalescingConditionIsAlwaysNotNull…`.

Treat separately and read each one. Two opposite readings, and which is true matters:

- The annotation is right and the check is dead → delete the check.
- The annotation is **wrong** — a provider or MCP payload really can be null despite what
  the signature claims — and the check is the only thing standing between nb and a
  `NullReferenceException` on a bad response. Then the *annotation* is the bug.

`Microsoft.Extensions.AI` types crossing an `AssemblyLoadContext` boundary from a provider
plugin are exactly where the second reading is plausible. Do not bulk-delete these, and do
not let them ride in the same commit as item 2 — a mechanical deletion commit should not
contain a behavioural judgement.

### 4. Everything else — read once, act on nothing

~170 style notes: 106 `UseCollectionExpression`, 41 `InconsistentNaming`, 23
`ConvertToPrimaryConstructor`, plus assorted redundancies. Per the TODO: read, mostly
ignore, and **do not run `jb cleanupcode`**. A whole-repo reformat buries real history in
`git blame` for no behavioural gain, and this codebase's comments carry reasoning that a
naming inspection will happily suggest away.

Two exceptions worth pulling out by hand if someone is already in the file: 20
`EmptyGeneralCatchClause` (some are deliberate and documented, some are not — the
deliberate ones should say so) and 7 `PossibleMultipleEnumeration` in `RateLimitRetry`
and the Mock provider.

## Not in this plan

The duplication findings from the same sweep are tracked separately, because jb does not
report them and they are structural rather than dead: the three identical success/error
blocks in `find_files`/`grep`/`list_dir`, the two divergent todo parsers
(`catch (JsonException)` vs a bare `catch`), the ten-parameter costume constructor
repeated across three costumes and three `HarnessRegistry.Create` branches, and the
duplicated `LeadingContext` filter. The seven TTY approval loops moved to
`plans/approval-without-prompts.md`.

## Done test

Someone runs `jb inspectcode nb.sln` six months from now and the report is short enough
to read in one sitting, with no entry that a maintainer has to re-decide is a false
positive. If they have to rediscover that `NbProgramBuilder` is public contract, the
suppression layer did not land — and the sweep will not happen a third time.
