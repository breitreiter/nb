# Skill Selection & Execution for nb

Status: Planned

## Overview

Skills are modular packages that extend an LLM's capabilities by bundling domain-specific instructions, reference materials, and executable code into self-contained directories. Rather than relying on a general-purpose model to handle every task from first principles, skills allow users to equip nb with specialized expertise on demand.

Each skill is defined by a `SKILL.md` file containing guidance for a particular domain (e.g., analyzing OpenTelemetry traces, formatting Word documents, debugging Kubernetes pods). When a user's query matches a skill's domain, the skill's instructions are loaded into context, transforming the LLM from a generalist into a focused specialist for that task.

**Benefits to users:**
- **Better results for specialized tasks** - Domain-specific instructions, examples, and reference materials produce higher quality outputs than generic prompting
- **Composability** - Skills are portable, shareable packages that capture procedural knowledge once and reuse it indefinitely
- **Efficiency** - Instructions are only loaded when relevant, keeping context lean for simple queries while enabling deep expertise when needed
- **Code execution** - Skills can include Python or C# code for deterministic operations better suited to traditional algorithms than token generation

## Architecture

### Installation Flow (One-Time)
1. Extract skill.zip to `~/.nb/skills/skill-name/`
2. Parse SKILL.md for description
3. Detect skill type: C#, Python, or instructions-only
4. Call LLM: "Generate 15-20 keywords and 10-15 phrases for this skill"
5. Save `keywords.json` alongside skill
6. If Python skill + no Docker → warn user

### Query Flow (Runtime)
1. User sends message
2. Match keywords (string contains, case-insensitive)
3. Score: keyword hit = 1pt, phrase hit = 2pt
4. If score ≥ 3: prompt user to load skill
5. User approves → load SKILL.md instructions into context
6. If skill needs code execution → run in isolated environment

---

## Skill Types & Execution

Use the user's configured LLM to generate keyword maps at skill installation time. Query-time matching is fast keyword/phrase matching. Python skills execute in Docker for isolation.

### Instructions-Only Skills
- Just SKILL.md text loaded into context
- No code execution
- Example: "docx" skill with document formatting guidelines

### C# Skills
- Loaded as `AssemblyLoadContext` with restricted permissions
- Can access nb's approved APIs
- Safer than arbitrary code execution

### Python Skills
- **Require Docker** for safe execution
- Isolated container per execution
- Read-only skill code, ephemeral work directory

---

## Docker Execution for Python Skills

### Check for Docker at Startup
```csharp
if (!IsDockerAvailable()) {
    Console.WriteLine("⚠️  Docker not found. Python skills disabled.");
    Console.WriteLine("   Install Docker to run Python-based skills.");
}

bool IsDockerAvailable() => 
    RunCommand("docker", "--version").ExitCode == 0;
```

### Execution Architecture
```
User Query
    ↓
Skill Matched & Approved
    ↓
Is Python Skill?
    ├─ No → Load instructions only
    └─ Yes → Execute in Docker
            ↓
        docker run \
          --rm \
          --network none \
          --memory 512m \
          --cpus 1 \
          -v /path/to/skill:/skill:ro \
          -v /tmp/work:/work \
          python:3.11-slim \
          python /skill/main.py
            ↓
        Capture stdout/stderr
            ↓
        Return results to nb
            ↓
        nb formats for user
```

### Docker Command Template
```bash
docker run \
  --rm                                    # Auto-cleanup
  --network none                          # No network access
  --memory 512m --cpus 1                  # Resource limits
  -v {skillPath}:/skill:ro                # Skill code (read-only)
  -v {workPath}:/work                     # Work directory (read-write)
  -e SKILL_INPUT={input}                  # Pass input data
  python:3.11-slim \
  python /skill/main.py
```

### Security Constraints
- **No network** by default (override only if skill manifest declares `requires_network: true`)
- **Read-only skill code** - skill cannot modify itself
- **Ephemeral work directory** - created per run, deleted after
- **Resource limits** - prevent runaway processes
- **No volume mounts to sensitive dirs** - no access to `~/.ssh`, `~/.aws`, etc.

### Skill Manifest (Optional)
```json
{
  "name": "trace-analyzer",
  "runtime": "python3.11",
  "requires_network": false,
  "requires_write_access": true,
  "entrypoint": "main.py",
  "max_memory_mb": 512,
  "timeout_seconds": 30
}
```

### Implementation Sketch
```csharp
public async Task<SkillResult> ExecutePythonSkillAsync(Skill skill, string input)
{
    if (!IsDockerAvailable())
        throw new Exception("Docker required for Python skills");
    
    var workDir = CreateTempWorkDir();
    var inputFile = Path.Combine(workDir, "input.json");
    File.WriteAllText(inputFile, input);
    
    var args = new[] {
        "run", "--rm",
        "--network", "none",
        "--memory", "512m",
        "--cpus", "1",
        "-v", $"{skill.Directory}:/skill:ro",
        "-v", $"{workDir}:/work",
        "python:3.11-slim",
        "python", "/skill/main.py"
    };
    
    var result = await RunCommandAsync("docker", args, timeout: 30);
    
    CleanupTempDir(workDir);
    
    return new SkillResult {
        Success = result.ExitCode == 0,
        Output = result.Stdout,
        Error = result.Stderr
    };
}
```

---

## Keyword Generation Prompt

```
You are analyzing a skill for a CLI tool. Generate comprehensive keywords 
to help match user queries to this skill.

Skill: {name}
Description: {description}

Generate:
- Technical terms (e.g., "otel", "telemetry")
- Synonyms (e.g., "traces" → "tracing")
- Problem phrases (e.g., "slow requests")
- Action phrases (e.g., "analyze traces")
- Related concepts (e.g., "observability", "APM")

Output JSON only:
{
  "category": "data-analysis",
  "keywords": [...],  // 15-20 words
  "phrases": [...]    // 10-15 phrases
}
```

---

## Configuration

```json
{
  "skills": {
    "autoload": true,
    "keyword_threshold": 3,
    "show_matches": true,
    "docker_enabled": true,
    "python_base_image": "python:3.11-slim"
  }
}
```

---

## User Experience

### Installation
```bash
$ nb skill install otel-analyzer.zip

Extracting skill... ✓
Analyzing skill (Python)...
Generating keywords... ✓
Skill 'otel-analyzer' installed!
Keywords: otel, traces, spans, latency, +12 more
```

### Query Time
```bash
$ nb "analyze my otel traces for bottlenecks"

Load 'otel-analyzer' skill (~2400 tokens)? (Y/n/?)
  Matched: otel, traces, bottlenecks

[user presses Enter]

✓ Loaded 'otel-analyzer'
[executing Python code in Docker...]
[results displayed]
```

### Docker Not Available
```bash
$ nb skill install trace-analyzer.zip

⚠️  Warning: Docker not found.
   This Python skill cannot execute.
   Install Docker or use C#-based skills only.

Skill 'trace-analyzer' installed (instructions-only mode)
```

---

## Implementation Phases

### Phase 1: Keyword Matching (No Docker)
- Keyword generation at install
- String matching at query time
- Instructions-only skills work
- **Ship this first**

### Phase 2: Docker Execution
- Check for Docker availability
- Execute Python skills in containers
- Basic resource limits
- Error handling

### Phase 3: Polish
- Skill manifest support
- Better error messages
- Skill update/regenerate commands
- Usage tracking for boosting

---

## Edge Cases

**No Docker installed:**
- Warn at install time
- Disable Python execution
- Still load skill instructions into context

**Docker timeout:**
- Kill container after 30s
- Return timeout error to user
- Suggest increasing timeout in config

**Container exits non-zero:**
- Capture stderr
- Show to user with context
- Don't crash nb

**Malicious skill:**
- Docker isolation prevents system access
- No network = can't exfiltrate
- Resource limits prevent DOS

---

## Example keywords.json

```json
{
  "category": "observability",
  "keywords": [
    "otel", "traces", "spans", "latency", "observability",
    "telemetry", "distributed", "bottleneck", "jaeger"
  ],
  "phrases": [
    "analyze traces", "find bottlenecks", "slow requests",
    "performance issues", "trace analysis"
  ]
}
```

---

## Testing Checklist

- [ ] Install Python skill with Docker
- [ ] Install Python skill without Docker (should warn)
- [ ] Execute Python skill successfully
- [ ] Execute Python skill with timeout
- [ ] Execute Python skill with error
- [ ] Verify Docker container cleanup
- [ ] Verify no network access
- [ ] Verify read-only skill mount
- [ ] Test resource limits (memory/CPU)

---

Hot takes from Gemini 3
You have hit on a critical insight: **Model-led routing (Progressive Disclosure) assumes the model is compliant and self-aware.** If you are supporting arbitrary models (like smaller open-weights models or older API checkpoints), they will often confidently hallucinate an answer rather than admitting they need a tool.

Your idea of using a "smart" model to generate retrieval artifacts for the "dumb" (or stubborn) runtime models is excellent. In Information Retrieval (IR), this is known as **Document Expansion** (specifically, generating synthetic queries for documents).

Here is a robust architecture for a **"Pre-Computed Intent Router"** that moves the intelligence to the *registration* phase, keeping the runtime phase fast and deterministic.

### The Architecture: "Offline Expansion, Online Retrieval"

Instead of searching against the *Skill Definition* (which is technical and dry), you search against *Hypothetical User Triggers* generated by a smart model.

#### Phase 1: Skill Registration (The "Smart" Step)
When a user adds a new skill to your CLI, you run a one-time extraction process using a high-end model (e.g., Claude 3.5 Sonnet or GPT-4o). You ask it to generate a **Routing Manifest**.

**Prompt for the Smart Model:**
> "Here is a tool definition. Generate a JSON object containing:
> 1. A list of 20 distinct, natural language user queries that would be best answered by this tool. (e.g., 'undo my last commit' for a git tool).
> 2. A list of 10 highly specific technical keywords (e.g., 'cherry-pick', 'rebase').
> 3. A 'Negative Constraint': A description of queries that might *look* like they match, but shouldn't (e.g., for a Python tool: 'Do not trigger on general questions about snake biology')."

**Why this works:**
*   **Semantic Bridging:** You are matching "query-to-query" (User input vs. Synthetic input) rather than "query-to-document." Embedding models perform significantly better on query-to-query tasks.
*   **Obstinate Model Proofing:** The runtime model never gets a choice. If the router matches, the skill is injected into the context immediately.

#### Phase 2: The Runtime Router (The Low-Latency Step)
Now that you have your Routing Manifest, you use a **Hybrid Search** strategy in your CLI.

**1. The Semantic Layer (Vector Search)**
*   **Store:** Use a lightweight, local-file vector store. Since you likely have <100 skills, `chromadb` or even raw `numpy` arrays with `sqlite` are fine.
*   **Process:**
    *   Embed the *Synthetic User Queries* generated in Phase 1 (not the skill text).
    *   Embed the actual User Input at runtime.
    *   Check cosine similarity.
*   **Thresholding:** If the score is < 0.6, assume no skill is needed.

**2. The Lexical Layer (Keyword/BM25)**
*   Use the "Technical Keywords" list from Phase 1.
*   If the user types a rare technical term (e.g., `kubectl`), you don't need semantic fuzziness; you need an exact match.
*   *Implementation:* A simple Python `set` intersection or a regex check.

### The "Tie-Breaker" Optimization
If your semantic search returns a high score for a skill, but you are worried about false positives (loading a heavy skill when not needed), implement a **tiny Verification Step**.

Don't ask the main LLM. Ask a local, quantized "Nano-LLM" or a Cross-Encoder.

*   **Tool:** `cross-encoder/ms-marco-TinyBERT-L-2-v2` (approx. 15MB, runs on CPU in milliseconds).
*   **Input:** `(User Query, Skill Description)`
*   **Output:** A relevance score (0 to 1).
*   **Logic:**
    1.  Vector search finds top 2 potential skills.
    2.  Cross-Encoder scores them against the query.
    3.  If score > 0.9, load the skill.

### Summary of the Workflow

1.  **User:** `add-skill ./docker-skill.md`
2.  **CLI (Background):** Calls GPT-4o/Sonnet. Generates 30 synthetic triggers. Embeds them using `all-MiniLM-L6-v2`. Saves to `~/.mycli/vectors.db`.
3.  --------------------------------------------------
4.  **User:** "My container keeps crashing on startup."
5.  **CLI (Runtime):**
    *   Embeds query.
    *   Finds match in `vectors.db` (Matches synthetic trigger: "debug crashing docker container").
    *   **Decision:** High confidence match -> **Inject Docker Skill**.
6.  **CLI:** Sends Prompt + Docker Skill to the obstinate/dumb model.
7.  **Model:** Sees the tool info in context. Can't ignore it. Answers correctly.

### Why this fits your constraints
1.  **Arbitrary Models:** The decision is made *before* the LLM is even invoked. The LLM is just the recipient of the context.
2.  **Low Latency:** Local vector math is sub-50ms.
3.  **Hefty Skills:** You only pay the context cost when the intent is strongly aligned.

> User: Why is my pod crashing?

[SYSTEM]  Detected intent: Kubernetes Debugging
[SKILL]   Load 'k8s-guru'? (+4,200 tokens / ~$0.04)
[Y]es  [N]o  [A]lways for this session