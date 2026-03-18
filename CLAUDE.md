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
- `Gemini:ApiKey`, `Gemini:Model` — Gemini API
- `Bot:CooldownSeconds` — Per-user rate limit

## Architecture

**Discord bot** providing qBitTorrent support via Google Gemini. Built on .NET 10 with [NetCord](https://github.com/KubaZ2/NetCord) (alpha) for Discord and Serilog for logging.

### Invocation paths (all require explicit user action)

| Trigger | Handler | Context gathering |
|---|---|---|
| @mention the bot | `MessageCreateHandler.HandleDirectMention` | 50 msgs around invocation, 12h window, all users |
| @mention + reply to someone | `MessageCreateHandler.HandleInvocationOnBehalf` | Same, with replied-to msg as anchor |
| Reply to bot's message | `MessageCreateHandler.HandleReplyToBot` | Walks reply chain recursively |
| `/qbit <question>` slash command | `QBitCommands.Ask` | Question text only |
| Right-click → "Ask qBitBot" | `QBitCommands.AskFromMessage` | Target message content + attachments |

### Request flow

1. **Context assembly** — `GatherUserContext` fetches channel messages, formats as `[HH:mm] Name: text` with labeled sections (Primary question, Older context >2h, Recent context <2h, Current question)
2. **Gemini call** — `GeminiService.AskAsync` sends system prompt + context + base64 images. Uses structured JSON output with schema enforcement. Returns `GeminiResponse` with intent classification (on_topic/piracy/off_topic), confidence, response text, resources, reasoning.
3. **Response formatting** — Builds Discord embeds color-coded by confidence (green/yellow/orange). Splits at 4096-char embed description limit on newline boundaries. Appends feedback buttons.
4. **Typing indicator** — `EnterTypingScope` wraps all Gemini calls for visual feedback during the ~10s API wait.

### Key design decisions

- **Embed responses, not plain text** — Embeds support 4096 chars (vs 2000 for messages), allow color-coding, and separate bot output visually.
- **Structured Gemini output** — `responseJsonSchema` forces Gemini to return typed JSON (intent, confidence, response, resources, reasoning). The `reasoning` field is internal-only (logged, not shown to users).
- **Reply chain walking** — `HandleReplyToBot` traverses `ReferencedMessage` recursively to build full conversation history. Bot messages use embed descriptions (not `Content`), so the walker checks both.
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
