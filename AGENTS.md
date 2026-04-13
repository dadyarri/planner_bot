# PlannerBot — Agent Instructions

## Project Overview

PlannerBot is a Telegram bot for coordinating D&D game sessions. It helps a group of players declare their weekly availability, vote on proposed game times, and receive reminders before scheduled games.

**Tech stack:** .NET 10, C#, Entity Framework Core (PostgreSQL), Telegram.Bot SDK, TickerQ (scheduled jobs), Humanizer (Russian time-span humanization in messages).

**Architecture:**
- `Services/UpdateHandler.cs` — Routes Telegram updates (messages, callbacks, reactions) to handlers
- `Services/UpdateHandler.logger.cs` — High-performance log methods via `[LoggerMessage]` (partial class of `UpdateHandler`)
- `Services/CommandHandler.cs` — Processes bot commands (`/plan`, `/vote`, `/yes`, `/no`, etc.)
- `Services/AvailabilityManager.cs` — Business logic for availability tracking (responses, date checking)
- `Services/VotingManager.cs` — Voting session lifecycle (creation, vote recording, outcome evaluation, messaging)
- `Services/GameScheduler.cs` — Game saving and reminder scheduling
- `Services/KeyboardGenerator.cs` — Generates inline keyboards for Telegram
- `Services/TimeZoneUtilities.cs` — UTC ↔ Moscow timezone conversions
- `Background/Jobs.cs` — Scheduled jobs (reminders, vote expiry, weekly notifications)
- `Background/*JobContext.cs` — TickerQ job payload types (`SendReminderJobContext`, `VoteReminderJobContext`, `VoteSessionExpiryJobContext`, `WeeklyVotingReminderJobContext`)
- `Data/` — Entity Framework entities and migrations

**Bot commands:**

| Command | Description |
|---|---|
| `/start` | Show command reference |
| `/yes hh:mm` | Mark available today at the given time |
| `/no` | Mark unavailable today; cancels today's saved games and their reminders |
| `/prob` | Mark tentatively available today |
| `/get` | Show 12-day availability grid for all active users |
| `/plan` | Open 12-day inline keyboard to set weekly availability |
| `/pause` | Mark user inactive (excluded from availability checks and reminders) |
| `/unpause` | Reactivate an inactive user |
| `/vote dd.MM.yyyy HH:mm` | Manually start a voting session for a specific date/time |
| `/saved` | List upcoming saved games (with IDs for `/unsave`) |
| `/unsave <id>` | Delete a saved game and cancel its scheduled reminders |
| `/weekly` | One-time setup: schedule recurring Saturday 21:00 UTC `/plan` reminder |

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
- Any other text that could trigger GitHub user notifications

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
- `Saved` triggers when `VoteCount >= activeUsersCount` (all active users voted FOR)
- `NoConsensus` triggers when `AgainstCount >= (activeUsersCount + 1) / 2` (ceiling of half)
- Votes can be retracted — removing a reaction calls `RemoveVote` and decrements counters atomically
- 24h TTL with automatic expiry
- 12h non-voter reminder
- Bot's own reactions are filtered out
- Cancel button uses callback data with creator username for ownership verification (same pattern as `/plan` command)
- Users who vote against (👎) are excluded from game reminders

## Key Patterns

- Inline keyboard buttons embed the owner's username in callback data for server-side access control (buttons are visible to everyone, but only the owner's clicks are processed)
- Callback data is semicolon-delimited: `"action;param1;...;username"`. Route on `split[0]`, guard ownership by comparing the username segment to `callbackQuery.From.Username`. Current actions: `plan`, `pstatus`, `ptime`, `pback`, `delete`, `vote_cancel`
- `ExecuteUpdateAsync` is used for atomic counter operations to prevent race conditions
- All scheduled jobs use TickerQ (`TimeTickerEntity` for one-time, `CronTickerEntity` for recurring)
- TickerQ function name strings (must match `[TickerFunction("name")]` in `Jobs.cs` and `AddAsync` calls): `send_reminder`, `send_vote_reminder`, `expire_vote_session`, `send_weekly_voting_reminder`
- Reminder intervals before a game: 48h, 24h, 5h, 3h, 1h, 10min (see `GameScheduler.ReminderIntervals`)
- `Availability.ToSign()` uses C# 14 explicit extension member syntax (`extension(Availability) { ... }`) in `Availability.cs` — intentional, do not refactor to classic static extension methods
- New log messages for `UpdateHandler` go in `UpdateHandler.logger.cs` as `[LoggerMessage]`-attributed static partial methods

## Database Migrations

Generate migrations with:
```bash
cd PlannerBot
DATABASE_URL="Host=localhost;Database=planner_bot;Username=postgres;Password=postgres" \
  dotnet ef migrations add <MigrationName>
```

## Environment Variables

- `DATABASE_URL` — PostgreSQL connection string (required at startup and for migrations)
- `TELEGRAM_TOKEN` — Telegram Bot API token (required at startup)
