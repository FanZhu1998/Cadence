# Phase 0 — Recon findings (captured 2026-09-13)

Captured from this machine's real Claude Code and Codex installs. **Where this
document and `TokenGauge-Windows-Blueprint.md` disagree, this document wins** —
per the blueprint's own Phase 0 instruction ("trust your capture, not this
document").

## Claude — session logs

`%USERPROFILE%\.claude\projects\**\*.jsonl`

Assistant lines carry `message.usage`. Confirmed top-level keys:
`parentUuid, isSidechain, message, apiBlockIndex, requestId, type, uuid,
timestamp, effort, userType, entrypoint, cwd, sessionId, version, gitBranch`

### Deduplication is not optional — measured

One real 80 MB transcript:

| metric | value |
|---|---|
| assistant lines carrying `message.usage` | 1156 |
| distinct `(message.id, requestId)` pairs | 553 |
| pairs appearing more than once | 324 |
| max repeats of a single pair | 14 |

Naive summing inflates that file's token total by **>2x**. Dedup on
`(message.id, requestId)`, keeping the *last* occurrence (cumulative chunks).

### `usage` shape — richer than the blueprint states

```json
{ "input_tokens": 2,
  "cache_creation_input_tokens": 8807,
  "cache_read_input_tokens": 28966,
  "output_tokens": 211,
  "output_tokens_details": { "thinking_tokens": 46 },
  "cache_creation": { "ephemeral_1h_input_tokens": 8807,
                      "ephemeral_5m_input_tokens": 0 },
  "service_tier": "standard",
  "iterations": [ { ...same fields... } ] }
```

Three deviations from the blueprint:

1. **`cache_creation` splits 5m vs 1h**, and those bill at different rates
   (1h write costs more than 5m). A single "cache-write" price is wrong.
   `pricing.json` needs `cacheWrite5m` and `cacheWrite1h`.
2. **`iterations[]`** repeats the same fields. It is a breakdown of the
   top-level, not an addition — summing both double-counts. Top level is
   authoritative.
3. `output_tokens_details.thinking_tokens` is a *subset* of `output_tokens`,
   already billed as output. Do not add it.

**Additivity:** `input_tokens` (2) excludes cache reads (28966). Claude's
fields are **additive** — total input = input + cache_read + cache_creation.

## Codex — session logs

`%USERPROFILE%\.codex\sessions\YYYY\MM\DD\*.jsonl`

The blueprint says to parse `event_msg` token-count entries. **That is wrong
for this Codex version.** Line types actually present:
`compacted, world_state, turn_context, event_msg, session_meta,
inter_agent_communication_metadata, response_item, token_usage_record`

Usage lives in `type == "token_usage_record"`:

```json
{ "type": "token_usage_record",
  "payload": {
    "thread_id": "...", "turn_id": "...", "response_id": "resp_...",
    "usage":              { "input_tokens": 25886, "cached_input_tokens": 18944,
                            "cache_write_input_tokens": 0, "output_tokens": 194,
                            "reasoning_output_tokens": 0, "total_tokens": 26080 },
    "turn_token_usage":   { ...cumulative for the turn... },
    "thread_token_usage": { ...cumulative for the thread... } } }
```

**Trap:** `turn_token_usage` and `thread_token_usage` are *running totals*.
Only `payload.usage` is the per-response delta. Sum `usage`, dedup on
`response_id`.

**Additivity — opposite of Claude:** `total_tokens` (26080) =
`input_tokens` (25886) + `output_tokens` (194), and `cached_input_tokens`
(18944) is a *subset* of `input_tokens`. So billable fresh input =
`input_tokens - cached_input_tokens`. Likewise `reasoning_output_tokens`
is a subset of `output_tokens`.

Getting this backwards silently overstates Codex cost by ~3x on
cache-heavy sessions. The two providers need separate normalizers.

Model for the bucket comes from the nearest preceding `turn_context`
payload's `model` field (confirmed present).

## Codex — credentials

`%USERPROFILE%\.codex\auth.json` keys: `auth_mode` (= "chatgpt"),
`OPENAI_API_KEY` (null), `tokens{ id_token, access_token, refresh_token,
account_id }`, `last_refresh` (ISO-8601). Matches the blueprint.

`config.toml` on this machine has **no** `cli_auth_credentials_store` key, so
the file store is the default. The keyring branch still has to be handled.

## Claude — credentials

`%USERPROFILE%\.claude\.credentials.json` and `%USERPROFILE%\.claude.json`
both exist. Structure not dumped here deliberately: reading them into an
agent transcript materializes live OAuth tokens. The blueprint's documented
shape (`claudeAiOauth{ accessToken, refreshToken, expiresAt, scopes }`) is
taken as-is, and the reader is written defensively against it. All test
fixtures are synthetic.

## Gemini

`%USERPROFILE%\.gemini\oauth_creds.json` absent — no Gemini CLI auth on this
machine. Consistent with the 2026-06-18 consumer deprecation. Antigravity
probe is therefore built to spec and exercised against a fake loopback
server in tests rather than a live one.
