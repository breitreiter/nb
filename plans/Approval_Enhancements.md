# Approval Enhancements

Status: Proposal

## Problem

The current approval system works correctly but is susceptible to approval fatigue. When agents are executing many tool calls, it's easy to develop a habit of pressing Y without carefully reviewing each command. This creates risk in two areas:

1. **Data exfiltration** - The model could leak sensitive data (API keys, tokens, credentials) through curl commands, environment variable dumps, or file reads piped to external services
2. **Destructive operations** - Dangerous commands can slip through when users are in "auto-approve" mode mentally

The existing dangerous command warnings help with #2, but we have no protection against #1.

## Proposal: Two Complementary Approaches

### Approach 1: Secret Detection

Scan command arguments and outputs for patterns that indicate sensitive data. Block or warn when secrets might be exfiltrated.

**Detection sources:**
- [gitleaks patterns](https://github.com/gitleaks/gitleaks/blob/master/config/gitleaks.toml) - comprehensive regex library for API keys, tokens, credentials
- Sensitive file paths (`~/.ssh/*`, `~/.aws/credentials`, `.env`, `**/secrets.yaml`, etc.)
- Environment variable names (`*_KEY`, `*_SECRET`, `*_TOKEN`, `*_PASSWORD`)

**Representative patterns:**
```
AWS Access Key:     AKIA[A-Z2-7]{16}
GCP API Key:        AIza[\w-]{35}
JWT Token:          ey[a-zA-Z0-9]{17,}\.ey[a-zA-Z0-9/_-]{17,}\..*
GitHub PAT:         ghp_[a-zA-Z0-9]{36}
Anthropic API Key:  sk-ant-api03-[a-zA-Z0-9_-]{93}
Generic Secret:     (?i)(api[_-]?key|secret|password|token)\s*[:=]\s*['"]?[a-zA-Z0-9_-]{16,}
```

**When to scan:**
- Command arguments before execution (outbound secrets)
- Command stdout/stderr after execution (secrets in output that might inform next action)
- Tool call parameters for any network-related tools

**UX when detected:**
```
⚠️ SENSITIVE DATA DETECTED
Command: curl -X POST https://api.example.com -H "Authorization: Bearer sk-ant-..."
         ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
         Possible Anthropic API key detected

This command may transmit credentials to an external service.
[y]es  [N]o  [?] details
```

The default flips to No, and "Always" is hidden.

### Approach 2: Risk-Based Auto-Approval

Allow users to configure automatic approval based on a blended risk assessment. The model provides its own risk assessment, which is combined with static analysis.

**Risk signals (each scored 0-1):**

| Signal | Description |
|--------|-------------|
| `model_assessment` | Model's self-reported risk level (low/medium/high) |
| `command_type` | Read-only (0.1), write (0.5), delete (0.7), network (0.6) |
| `path_sensitivity` | Home dir (0.3), system files (0.8), project files (0.1) |
| `data_sensitivity` | Secret patterns detected (0.9), env vars (0.5), normal (0.1) |
| `whitelist_match` | Matches user's pre-approved patterns (-0.5) |

**Blended score:**
```
risk = weighted_average(signals) + adjustments
```

**Model self-assessment (new tool parameter):**
```json
{
  "command": "cat ~/.bashrc",
  "risk_assessment": {
    "level": "low",
    "reasoning": "Reading a config file in user's home directory, no modification or network access"
  }
}
```

Cline tried this approach and users complained about unpredictability. Mitigations:
- Model assessment is just one signal, not the whole decision
- Show the risk breakdown so users understand why something was auto-approved or flagged
- Default to conservative (require explicit opt-in to auto-approve)

**Configuration:**
```json
{
  "approval": {
    "autoApproveThreshold": 0.3,
    "requireExplicitApproval": ["network", "delete", "sudo"],
    "whitelist": ["ls *", "cat *.md", "git status"],
    "trustModelAssessment": 0.5
  }
}
```

**UX for auto-approved:**
```
✓ auto-approved (risk: 0.12): cat README.md
  read-only | project file | model: low | whitelisted
```

Brief one-liner showing what happened and why, so users maintain awareness.

## How They Work Together

Secret detection acts as a **hard block** that overrides risk scoring. Even if a command would otherwise auto-approve, detected secrets force explicit approval with default No.

Risk scoring handles the **soft decisions** - things that aren't obviously dangerous but might warrant review based on user preferences.

```
┌─────────────────────────────────────────────────────┐
│                   Command Received                   │
└────────────────────────┬────────────────────────────┘
                         │
                         ▼
              ┌──────────────────────┐
              │  Secret Detection    │
              │  (hard block)        │
              └──────────┬───────────┘
                         │
            ┌────────────┴────────────┐
            │                         │
            ▼                         ▼
    Secrets Found              No Secrets
            │                         │
            ▼                         ▼
    Force Approval           ┌──────────────────┐
    (default: No)            │  Risk Scoring    │
                             └────────┬─────────┘
                                      │
                         ┌────────────┴────────────┐
                         │                         │
                         ▼                         ▼
                  Score > threshold         Score ≤ threshold
                         │                         │
                         ▼                         ▼
                  Require Approval          Auto-Approve
                  (default: Yes)            (show notice)
```

## Implementation Considerations

**Secret pattern maintenance:**
- Ship a default pattern set based on gitleaks
- Allow user overrides/additions in config
- Consider fetching updated patterns periodically (opt-in)

**False positives:**
- Entropy-based detection catches random strings that aren't secrets
- Base64-encoded content triggers JWT patterns
- Mitigation: require multiple signals or use entropy scoring

**Performance:**
- Regex scanning on every command adds latency
- Pre-compile patterns at startup
- Consider scanning in parallel with command execution for output checking

**Model gaming:**
- Model could learn to always report "low" risk
- Mitigation: model assessment is minority weight in scoring
- Could track model accuracy over time and adjust trust

## Open Questions

1. **Should secret detection be blocking or warning-only?** Blocking is safer but could be annoying for legitimate use cases (debugging auth, intentionally sharing test keys).

2. **How to handle false positives gracefully?** Options: per-session dismissal, persistent allowlist, "trust this pattern" button.

3. **Should risk scores be visible by default?** Transparency builds trust, but might be noise for users who want simple Y/N.

4. **What's the right default for `autoApproveThreshold`?** 0 = never auto-approve, 1 = always auto-approve. Should probably default to 0 (disabled) and let users opt in.

5. **Should model assessment be mandatory or optional?** Adding a required parameter to every tool call is intrusive. Could make it optional and assume "unknown" (0.5) when missing.

## Estimated Effort

| Component | Effort | Notes |
|-----------|--------|-------|
| Secret patterns + scanning | Medium | Port gitleaks patterns, integrate with approval flow |
| Sensitive path detection | Low | Simple glob matching |
| Risk scoring framework | Medium | Weighted scoring, config schema |
| Model self-assessment | Low | Add optional parameter to bash tool |
| UX for risk display | Low | One-liner format for auto-approved commands |
| Config schema + validation | Low | JSON schema, defaults |
| Testing | Medium | Need good coverage of edge cases |
