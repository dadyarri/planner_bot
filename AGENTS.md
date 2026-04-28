# PlannerBot — Agent Instructions

## Purpose

PlannerBot is a Telegram bot for coordinating tabletop sessions. It collects availability, starts vote sessions for proposed game times, saves approved games, and schedules reminders.

Primary stack:
- .NET 10
- C#
- Entity Framework Core with PostgreSQL
- Telegram.Bot
- TickerQ
- Humanizer

## Repository Layout

Repo root:
- `PlannerBot.slnx` — solution entrypoint
- `PlannerBot/` — main application project

Project structure under `PlannerBot/`:
- `Program.cs` — composition root and service registration
- `Abstract/` — shared receiver/background abstractions
- `Background/` — TickerQ jobs and job payload contracts
- `Data/` — EF Core entities, `AppDbContext`, and migrations
- `Properties/` — launch settings and assembly metadata
- `Services/` — bot behavior and application logic

Important service files:
- `Services/UpdateHandler.cs` — routes Telegram updates to handlers
- `Services/UpdateHandler.logger.cs` — high-performance log methods via `[LoggerMessage]`
- `Services/CommandHandler.cs` — slash command handling
- `Services/AvailabilityManager.cs` — availability rules and response updates
- `Services/VotingManager.cs` — voting lifecycle, counters, messaging
- `Services/GameScheduler.cs` — saved games and reminder scheduling
- `Services/KeyboardGenerator.cs` — inline keyboard generation
- `Services/TimeZoneUtilities.cs` — UTC and Moscow conversions
- `Background/Jobs.cs` — scheduled reminder and expiry jobs

## Product Rules

- Bot-facing text must be in Russian.
- Tone should have light fantasy / D&D flavor.
- Store all persisted datetimes in UTC.
- Convert to Europe/Moscow for user-facing display.
- Use `TimeZoneUtilities` for conversions. Do not introduce `DateTime.Now`-based logic.
- Do not mention GitHub users with `@name` in commits, PR text, review text, notes, or documentation.
- Telegram username mentions inside bot message code are allowed when they are actual product behavior.

## Voting Rules

- Votes are tracked per user in `VoteSessionVotes`.
- Aggregate counters on `VoteSessions` are the authoritative source for vote thresholds.
- Counter changes must be atomic. Use `ExecuteUpdateAsync` for increment/decrement operations.
- Any code that evaluates vote completion or renders aggregate counts must read fresh database state, not rely on stale tracked entities.
- `Saved` means all active campaign members voted `For`.
- `NoConsensus` means `AgainstCount >= (activeUsersCount + 1) / 2`.
- Vote removal must reverse the matching aggregate counter atomically.

## Development Requirements

Use normal .NET backend best practices, not ad hoc shortcuts.

- Keep business rules in services, not in Telegram transport code.
- Prefer small, explicit methods with single responsibilities.
- Preserve async flow end-to-end; do not block on async calls.
- Use dependency injection instead of service locators or static state.
- Keep database writes intentional and minimal.
- Favor EF Core queries that are explicit about tracking behavior.
- Use `AsNoTracking()` for read-only queries where tracked entities are not required.
- Use atomic database updates for shared counters and race-prone state transitions.
- Do not duplicate business rules across handlers and managers.
- Keep bot messages and formatting centralized when practical.
- Add new `UpdateHandler` logs in `Services/UpdateHandler.logger.cs` using `[LoggerMessage]`.
- Respect existing architecture before introducing new abstractions.

## EF Core Guidance

- Treat tracked entities as potentially stale after `ExecuteUpdateAsync` / `ExecuteDeleteAsync`.
- Reload or query fresh state when later logic depends on the updated values.
- Avoid mixing tracked and non-tracked reads carelessly in the same flow.
- Keep migrations focused and reversible when possible.
- Do not hand-edit old migrations unless explicitly required.

## Migration Rules

This rule is strict:
- Never write migration files manually.
- Never create a migration `.cs` file with `apply_patch`.
- Never create a migration `.Designer.cs` file with `apply_patch`.
- Never manually edit `AppDbContextModelSnapshot.cs` to simulate a generated migration.
- Always generate migrations with `dotnet ef migrations add <MigrationName>`.
- If a generated migration is wrong, fix the model and regenerate it with `dotnet ef migrations remove` followed by `dotnet ef migrations add ...`.
- If a migration must be reverted, use `dotnet ef migrations remove` when possible instead of manually deleting migration files.

Create migrations with:

```bash
cd PlannerBot
DATABASE_URL="Host=localhost;Database=planner_bot;Username=postgres;Password=postgres" \
  dotnet ef migrations add <MigrationName>
```

The snapshot file is generated artifact, not handwritten source of truth.

## Verification Requirements

Before finishing substantial code changes:
- build the solution
- run formatting
- report any tooling failures clearly if the environment prevents completion

Preferred commands:

```bash
dotnet restore PlannerBot.slnx
dotnet build PlannerBot.slnx
cd PlannerBot && dotnet format
```

## Mandatory Escalation Rule For .NET Commands

Always request escalated permissions before running any of these commands:
- `dotnet restore`
- `dotnet build`
- `dotnet format`
- `dotnet ef`

This rule applies even if the command might succeed in the sandbox. The reason is practical: these commands may need network access, MSBuild child processes, SDK workload checks, restore caches, or filesystem locations outside the writable sandbox.

When possible, use these approved command shapes:
- `dotnet restore PlannerBot.slnx'`
- `dotnet build PlannerBot.slnx'`
- `dotnet format PlannerBot.slnx --no-restore'`

If a command still fails due to environment or SDK issues, do not hide it. State exactly which command failed and why.

## TickerQ And Background Jobs

- Keep TickerQ function names aligned with `[TickerFunction("...")]` attributes.
- One-time jobs use `TimeTickerEntity`.
- Recurring jobs use `CronTickerEntity`.
- Changing payload contracts in `Background/*JobContext.cs` requires checking all producers and consumers.

Current function names:
- `send_reminder`
- `send_vote_reminder`
- `expire_vote_session`
- `send_weekly_voting_reminder`

## Callback And Telegram Patterns

- Callback data is semicolon-delimited.
- Route by the first segment.
- Ownership checks must stay server-side.
- Inline keyboards may be visible to everyone; authorization must not depend on UI visibility.
- Filter out the bot's own reactions.

## Environment

Required environment variables:
- `DATABASE_URL`
- `TELEGRAM_TOKEN`

## Formatting Rule

Always run `dotnet format` before committing when the tool is functional in the environment. If it fails because of SDK or environment issues, mention that explicitly in the final report.
