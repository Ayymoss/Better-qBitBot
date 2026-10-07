# qBitBot

A Discord bot that provides automated qBitTorrent support using Claude (Haiku 5.5). It monitors channels for questions from new server members and responds with helpful answers, and can be invoked on-demand by anyone via `@mention` or reply.

## Features

- **Auto-response for new users** — Detects questions from users who joined within the last 24 hours and responds after a 60-second aggregation window (collecting multi-message questions)
- **On-demand invocation** — Any user can `@mention` the bot or reply to its messages for help
- **Proxy invocation** — `@mention` the bot while replying to another user's message to get an answer on their behalf
- **Intent classification** — Claude classifies questions as on-topic, off-topic, or piracy-related using structured outputs
- **Image/screenshot support** — Attached images are sent to Claude for visual context (JPEG/PNG/GIF/WebP)
- **Intervention detection** — If a human starts helping during the aggregation window, the bot backs off
- **Per-user rate limiting** — 60-second cooldown on direct invocations to prevent spam
- **Web search** — Claude can search the web for version-specific or recent issues (capped per request, toggleable)

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (preview)
- A [Discord bot token](https://discord.com/developers/applications) with the following privileged intents enabled:
  - Message Content Intent
  - Server Members Intent
- An [Anthropic API key](https://platform.claude.com/settings/keys)

### Discord Bot Permissions

The bot requires these permissions:
- Send Messages
- Read Message History
- Add Reactions
- View Channels

## Configuration

Configuration is provided via `appsettings.json`, environment variables, or [user secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets).

| Key | Description | Default |
|-----|-------------|---------|
| `Discord:Token` | Discord bot token | *required* |
| `Anthropic:ApiKey` | Anthropic API key | *required* |
| `Anthropic:Model` | Claude model to use | `claude-haiku-5-5` |
| `Anthropic:Effort` | Thinking effort: `low`/`medium`/`high`/`xhigh`/`max` | `medium` |
| `Anthropic:WebSearchEnabled` | Allow Claude to use web search ($10 / 1k searches) | `true` |
| `Anthropic:WebSearchMaxUses` | Max web searches per request | `3` |
| `Bot:NewUserThresholdHours` | Hours since join to consider a user "new" (for greeting) | `24` |
| `Bot:DailyTurnBudget` | Claude calls per user per sliding 24h window | `20` |
| `Bot:GreetEnabled` | Offer the bot to new users after a quiet period (once per user, ever) | `true` |
| `Bot:GreetWaitMinutes` | Minutes to wait after a new user's last message before greeting | `10` |
| `Bot:ErrorContactHandle` | Discord handle to ping in error messages | `@ayymoss` |
| `Bot:MaxAttachmentBytes` | Max size of an image attachment sent to Claude (bytes) | `10485760` |
| `Persistence:DatabaseFile` | SQLite file path (relative paths resolved against CWD) | `data/qbitbot.db` |

### User Secrets (development)

```bash
dotnet user-secrets set "Discord:Token" "your-token"
dotnet user-secrets set "Anthropic:ApiKey" "your-key"
```

Requires `DOTNET_ENVIRONMENT=Development` to be set (configured in `Properties/launchSettings.json`).

## Running

### Local

```bash
cd qBitBotNew
dotnet run
```

### Docker

```bash
docker build -t qbitbot .
docker run -d \
  --name qbitbot \
  --restart unless-stopped \
  -e Discord__Token=your_discord_token \
  -e Anthropic__ApiKey=your_anthropic_key \
  qbitbot
```

### Docker (GHCR)

A GitHub Actions workflow builds and pushes the image to GitHub Container Registry on every push to `main`.

```bash
docker pull ghcr.io/<owner>/qbitbotnew:latest
docker run -d \
  --name qbitbot \
  --restart unless-stopped \
  -e Discord__Token=your_discord_token \
  -e Anthropic__ApiKey=your_anthropic_key \
  ghcr.io/<owner>/qbitbotnew:latest
```

## How It Works

1. **New user posts a message** — The bot starts a 60-second collection window, aggregating any follow-up messages and attachments from that user
2. **Window closes** — The aggregated question (text + images) is sent to Claude with a system prompt that classifies intent and generates a response
3. **Bot responds** — If the question is on-topic, the bot replies to the user's first message with the answer and any relevant resources
4. **Filtering** — Piracy and off-topic questions are silently ignored for auto-responses; direct invocations get a brief explanation of why the bot can't help

## Tech Stack

- [NetCord](https://netcord.dev/) — Discord library for .NET
- [Claude API](https://platform.claude.com/docs) — Claude Haiku 5.5 with structured outputs, web search, and vision
- [Serilog](https://serilog.net/) — Structured logging (console + file sinks)
- .NET Generic Host

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
