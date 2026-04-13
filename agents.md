# PlannerBot — Agent Instructions

## Project Overview

PlannerBot is a Telegram bot for coordinating D&D game sessions. It helps a group of players declare their weekly availability, vote on proposed game times, and receive reminders before scheduled games.

**Tech stack:** .NET 10, C#, Entity Framework Core (PostgreSQL), Telegram.Bot SDK, TickerQ (scheduled jobs).

**Architecture:**
- `Services/UpdateHandler.cs` — Routes Telegram updates (messages, callbacks, reactions) to handlers
- `Services/CommandHandler.cs` — Processes bot commands (`/plan`, `/vote`, `/yes`, `/no`, etc.)
- `Services/AvailabilityManager.cs` — Business logic for availability tracking (responses, date checking)
- `Services/VotingManager.cs` — Voting session lifecycle (creation, vote recording, outcome evaluation, messaging)
- `Services/GameScheduler.cs` — Game saving and reminder scheduling
- `Services/KeyboardGenerator.cs` — Generates inline keyboards for Telegram
- `Services/TimeZoneUtilities.cs` — UTC ↔ Moscow timezone conversions
- `Background/Jobs.cs` — Scheduled jobs (reminders, vote expiry, weekly notifications)
- `Data/` — Entity Framework entities and migrations

## Formatting Requirements

**Always run `dotnet format` in the `PlannerBot/` project folder before committing.** This ensures consistent code style across the project.

```bash
cd PlannerBot && dotnet format
```

## CRITICAL: Never Mention Users

**DO NOT EVER use the `@username` syntax in:**
- Commit messages
- Pull request titles, descriptions, or comments
- Any GitHub-related text (issues, reviews, etc.)
- Your thought process or planning notes
- Any text that could trigger GitHub user notifications

This includes common placeholder names — do not write things like `@alice`, `@bob`, `@user1`, etc., since these are real GitHub accounts and mentioning them sends unwanted notifications to real people.

**Exception:** Using `@{username}` syntax inside C# code is allowed when it's part of building Telegram messages (e.g., `$"@{user.Username}"`), because Telegram usernames are different from GitHub usernames and this code runs server-side.

When referring to users in documentation or examples, use display names without the `@` prefix (e.g., "User A", "User B", "the creator", "the DM").

## Bot Language

All messages the bot sends to Telegram must be in **Russian** with a **fantasy/D&D flavor** (medieval language, references to quests, battles, brotherhood, ancient magic, etc.).

## DateTime Handling

- **Store:** Always UTC in the database
- **Display:** Always Europe/Moscow timezone for user-facing messages
- Use `TimeZoneUtilities` for all conversions — never call `DateTime.Now` or assume system timezone
- `CheckIfDateIsAvailable` returns Moscow time; use `ConvertToUtc()` before storing

## Voting System

The voting system uses Telegram emoji reactions (👍 for, 👎 against) on messages:
- Per-user vote tracking via `VoteSessionVote` join table
- Vote deduplication (one vote per user per session)
- Distinct outcomes: `Pending`, `Saved`, `Expired`, `Canceled`, `NoConsensus`
- 24h TTL with automatic expiry
- 12h non-voter reminder
- Bot's own reactions are filtered out
- Cancel button uses callback data with creator username for ownership verification (same pattern as `/plan` command)
- Users who vote against (👎) are excluded from game reminders

## Key Patterns

- Inline keyboard buttons embed the owner's username in callback data for server-side access control (buttons are visible to everyone, but only the owner's clicks are processed)
- `ExecuteUpdateAsync` is used for atomic counter operations to prevent race conditions
- All scheduled jobs use TickerQ (`TimeTickerEntity` for one-time, `CronTickerEntity` for recurring)

## Database Migrations

Generate migrations with:
```bash
cd PlannerBot
DATABASE_URL="Host=localhost;Database=planner_bot;Username=postgres;Password=postgres" \
  dotnet ef migrations add <MigrationName>
```
