# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build
dotnet run --project qBitBotNew
```

No test projects exist. The solution is single-project (`qBitBotNew.sln` → `qBitBotNew/qBitBotNew.csproj`).

Docker: `docker build -t qbitbot .` (multi-stage .NET 10 build).

## Configuration

Secrets go in user-secrets (dev) or environment variables (prod). Config sections:
- `Discord:Token` — Bot token (auto-bound by NetCord)
- `Anthropic:ApiKey`, `Anthropic:Model`, `Anthropic:Effort`, `Anthropic:WebSearchEnabled`, `Anthropic:WebSearchMaxUses` — Claude API (official `Anthropic` NuGet SDK)
- `Bot:CooldownSeconds` — Per-user rate limit

## Architecture

**Discord bot** providing qBitTorrent support via Claude (Haiku 5.5). Built on .NET 10 with [NetCord](https://github.com/KubaZ2/NetCord) (alpha) for Discord and Serilog for logging.

### Invocation paths (all require explicit user action)

| Trigger | Handler | Context gathering |
|---|---|---|
| @mention the bot | `MessageCreateHandler.HandleDirectMention` | 50 msgs around invocation, 12h window, all users |
| @mention + reply to someone | `MessageCreateHandler.HandleInvocationOnBehalf` | Same, with replied-to msg as anchor |
| Any message (reply, @mention or plain) in a thread the bot has answered in | `MessageCreateHandler.HandleThreadConversation` | Whole thread (last 100 msgs) + its starter message, as real user/assistant turns; conversation prompt-cached |
| Reply to bot's message outside a bot thread | `MessageCreateHandler.HandleReplyToBot` | Walks `ReferencedMessage` (Discord only nests one level, so usually just the bot's last answer) |
| `/qbit <question>` slash command | `QBitCommands.Ask` | Question text only |
| Right-click → "Ask qBitBot" | `QBitCommands.AskFromMessage` | Target message content + attachments |

### Request flow

1. **Context assembly** — `GatherUserContext` fetches channel messages, formats as `[HH:mm] Name: text` with labeled sections (Primary question, Older context >2h, Recent context <2h) for first invocations. Bot threads use `GatherThreadConversation` instead (see below)
2. **Claude call** — `ClaudeService.AskAsync` sends system prompt + context + base64 images (jpeg/png/gif/webp only) with adaptive thinking (summarized), `output_config.format` structured output, and optional basic web search. Returns `BotResponse` with intent classification (on_topic/piracy/off_topic), confidence, response text, resources, follow-ups, topic. `stop_reason: refusal` (Haiku 5.5 has no server-side fallback) and `pause_turn` map to `Result.Failure`.
3. **Response formatting** — Builds Discord embeds color-coded by confidence (green/yellow/orange). Splits at 4096-char embed description limit on newline boundaries. Appends feedback buttons.
4. **Typing indicator** — `EnterTypingScope` wraps all Claude calls for visual feedback during the API wait.

### Key design decisions

- **Embed responses, not plain text** — Embeds support 4096 chars (vs 2000 for messages), allow color-coding, and separate bot output visually.
- **Structured output** — `output_config.format` (json_schema, `additionalProperties: false`) forces typed JSON. No `reasoning` field — asking for the model's reasoning in output can trigger `reasoning_extraction` refusals; summarized thinking blocks fill `ThoughtSummary` instead. With web search on, the parser takes every text block after the last search result. Structured output returns no citations, so Resources are ranked by whether each URL appeared in this turn's `web_search_tool_result` blocks (search-confirmed links first).
- **Prompt caching** — one `cache_control` breakpoint on the system prompt caches the ~4.1k-token tools + system + schema prefix (5-min TTL). The system prompt embeds today's date (Haiku 5.5 searches more reliably with it), so the cache rolls once a day. Anything that changes the system prompt, tools, or `output_config.format` per request would break caching.
- **Token stats** — Claude's `output_tokens` includes thinking, so `ThoughtTokens` is always 0 for Claude-era rows.
- **Thread conversations** — in a bot thread, `GatherThreadConversation` rebuilds the thread as turns: starter message (plus the message it replied to, for on-behalf invocations), then each human message as a user turn and each bot answer (untitled answer embeds + the "I asked" follow-up questions) as an assistant turn. Placeholders and "Something went wrong" notices are skipped. Every turn must format identically across requests or the conversation cache misses — so names use `GlobalName ?? Username` (REST history has no nicknames) and the bot mention is stripped everywhere. Only images posted since the bot's last answer are re-sent. Known cache misses: a turn with images, threads past 100 messages, and the daily date change.
- **Reply chain walking (non-thread)** — `HandleReplyToBot` follows `ReferencedMessage`; bot messages use embed descriptions (not `Content`), so the walker checks both.
- **No auto-response** — The bot previously auto-responded to new users via `MessageAggregatorService`. This was removed to avoid interjecting into normal conversation. The service file still exists but is not wired up.

## NetCord reference

NetCord is in alpha with limited docs. A local reference copy lives at `C:\Users\Amos\RiderProjects\_Work\_CODE REFERENCES` — use this to look up NetCord APIs (types, method signatures, hosting patterns) when the compiler or runtime behavior is unclear.

Key NetCord patterns used:
- `IMessageCreateGatewayHandler` for message events
- `ApplicationCommandModule<ApplicationCommandContext>` for slash/message commands
- `ComponentInteractionModule<ButtonInteractionContext>` for button handlers
- `InteractionCallback.DeferredMessage()` + `FollowupAsync` for long-running commands
- `InteractionCallback.ModifyMessage()` to update the message a button lives on
- Command methods returning `Task` (void) when manually handling responses — returning `Task<T>` causes the framework to auto-serialize the return value as an interaction response
