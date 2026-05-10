Here’s a **drop-in Markdown report** you can hand to a coding agent. It’s opinionated, Azure-specific, and structured as an execution plan rather than a vague checklist.

---

# Azure OpenAI Integration Hardening Guide (for Agentic Coding Workflows)

## Context

We are using Azure OpenAI models (GPT-5.x family) inside a tool-using coding agent (`nb`). Compared to Anthropic models, we are seeing:

* Slow responses
* Hesitation to use tools
* Weak recovery after errors
* “Lazy” behavior (analysis instead of execution)

This document outlines **Azure-specific constraints and required changes** to improve reliability, tool usage, and execution quality.

---

# 1. Critical Azure-Specific Constraints

## 1.1 Tool Description Length Limit (HIGH PRIORITY)

Azure enforces:

* **Max 1024 characters per tool/function description**

### Impact

If exceeded or approached:

* Descriptions may be truncated
* Critical instructions may be lost
* Tool selection degrades significantly
* Model appears “lazy” or “tool-avoidant”

### Required Actions

* Enforce **hard cap: 400 chars per tool description**
* Move all:

  * policies
  * workflows
  * heuristics
    → into the **system prompt**

### Example (GOOD)

```text
Edit a file by replacing an exact string.

Use for small, targeted edits to existing files.
Fails if match is missing or ambiguous.
Use write_file for full rewrites or new files.
```

---

## 1.2 Reasoning Models Require Stateful Loops

Azure GPT-5.x models are **reasoning models**, not simple chat models.

### Impact

If you do NOT preserve state across tool calls:

* Model loses context after each tool call
* Planning degrades
* Error recovery collapses
* Behavior appears inconsistent or “gives up”

### Required Actions

You MUST:

* Preserve **full response context across turns**, including:

  * tool calls
  * tool outputs
  * reasoning state (if available)
* Use:

  * `previous_response_id` (preferred), OR
  * replay prior messages fully

### Anti-pattern (BAD)

```
User → Model → Tool → New Chat Turn → Model
```

### Correct Pattern (GOOD)

```
User → Model → Tool → Model (same chain, preserved context)
```

---

## 1.3 GPT-5 Models Trade Latency for Reasoning

Azure guidance:

* GPT-5.x = **high reasoning, high latency**
* Not suitable for tight inner loops by default

### Impact

* Feels “slow” during:

  * file inspection
  * grep/search
  * trivial edits

### Required Actions

Introduce **phase-based model usage**:

| Task Type          | Model Strategy              |
| ------------------ | --------------------------- |
| File browsing      | lower effort / faster model |
| Planning           | GPT-5 (medium effort)       |
| Debugging failures | GPT-5 (high effort)         |
| Simple edits       | low effort                  |

If multi-model is unavailable:

* Dynamically adjust reasoning settings (see below)

---

## 1.4 Reasoning Configuration Matters (Azure-Specific)

Azure exposes controls like:

* `reasoning_effort`: low / medium / high
* `verbosity`
* `preamble` (planning exposure)

### Required Actions

Set **by phase**:

| Phase            | reasoning_effort |
| ---------------- | ---------------- |
| Exploration      | low              |
| Planning         | medium           |
| Error recovery   | medium/high      |
| Final validation | medium           |

---

## 1.5 Tool Choice Must Be Constrained

Azure/OpenAI models will hesitate if:

* too many tools are available
* tool purposes overlap
* descriptions are verbose or unclear

### Required Actions

* Use **tool gating per step**
* When obvious, force:

```json
tool_choice: "required"
```

OR restrict to a subset:

```json
allowed_tools: ["read_file", "edit_file"]
```

---

# 2. Prompting Requirements (CRITICAL)

Your current system prompt is **too passive**. It allows:

* planning without execution
* summarization instead of action
* stopping after first failure

## 2.1 Add an Execution Contract

This is REQUIRED.

```text
You are an implementation agent.

When the user asks for a task:
- Perform the task
- Do not stop at planning or explanation
- Do not return “here’s how I would do it”

You must continue working until:
- the task is complete and validated, OR
- you hit a real external blocker

After errors:
- diagnose the issue
- try another approach
- continue working

Do not stop after reporting errors.
```

---

## 2.2 Enforce Tool Usage

```text
If a task involves files, code, or commands:
- you MUST use tools
- do not ask the user to do work you can do

Prefer acting over describing.
```

---

## 2.3 Add Anti-Laziness Rule

```text
Do not respond with:
- “the spec looks good”
- “here’s how I would implement this”
- “ready for you to proceed”

If implementation is possible, you must implement it.
```

---

## 2.4 Add Persistence Rule

```text
On failure:
- infer the likely cause
- try a different approach
- continue

Only stop if:
- missing credentials
- missing external service
- destructive ambiguity
```

---

## 2.5 Reduce Conversational Friction

REMOVE or weaken:

* “explain before acting”
* “check in after phases”

REPLACE with:

```text
Be brief before tool calls (one sentence max).
Do not ask for confirmation unless necessary.
```

---

# 3. Tool Design Guidelines

## 3.1 Keep Descriptions Short

Target:

* **150–400 characters**

Never include:

* step-by-step usage guides
* retry strategies
* policy rules

---

## 3.2 Use System Prompt for Behavior

Move this OUT of tools:

* “use read_file first”
* “retry if ambiguous”
* “prefer X over Y”

Put this in system prompt instead.

---

## 3.3 Prefer Strong Schemas

* clear parameter names
* required fields
* minimal ambiguity

Bad:

```
content
```

Good:

```
old_string
new_string
replace_all
```

---

## 3.4 Reduce Tool Surface Area

Too many tools → hesitation.

Group tools conceptually:

* file read/write/edit
* shell execution
* search

Avoid:

* near-duplicate tools
* overlapping capabilities

---

# 4. Agent Loop Requirements

## 4.1 Required Loop Behavior

Each iteration must:

1. Decide next action
2. Use tool (if needed)
3. Observe result
4. Continue

NOT:

* Plan → Stop
* Error → Stop

---

## 4.2 Validation Step (MANDATORY)

Before completion:

* run tests / build / lint if available

OR:

* explicitly state why validation is not possible

---

## 4.3 Progress Over Perfection

Encourage:

* small edits
* iterative fixes
* re-validation

Discourage:

* large speculative rewrites

---

# 5. Implementation Plan (for Agent)

## Phase 1 — Tool Cleanup

* [ ] Audit all tools
* [ ] Enforce <400 char descriptions
* [ ] Remove instructional prose
* [ ] Normalize parameter schemas

---

## Phase 2 — Prompt Upgrade

* [ ] Add execution contract
* [ ] Add persistence rules
* [ ] Add anti-laziness rules
* [ ] Reduce conversational behaviors

---

## Phase 3 — Loop Fixes

* [ ] Preserve response state across turns
* [ ] Ensure tool outputs feed next step
* [ ] Remove stateless tool handling

---

## Phase 4 — Tool Strategy

* [ ] Implement tool gating
* [ ] Add `tool_choice` where obvious
* [ ] Reduce tool set per step

---

## Phase 5 — Reasoning Tuning

* [ ] Add phase-based `reasoning_effort`
* [ ] Reduce effort for simple steps
* [ ] Increase effort for debugging

---

## Phase 6 — Validation Layer

* [ ] Add automatic test/build step before completion
* [ ] Require validation or explicit explanation

---

# 6. Expected Outcome

After applying these changes:

* Faster perceived performance
* More consistent tool usage
* Stronger task completion
* Better error recovery
* Elimination of “lazy” behavior

---

# Bottom Line

This is not a model quality issue.

It is a combination of:

* Azure constraints (1024 char limit)
* reasoning model requirements (stateful loops)
* insufficiently strict prompting
* overly verbose tool definitions

Fix those, and GPT-5.x will behave much closer to what you’re seeing with Anthropic.
