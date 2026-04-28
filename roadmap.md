# Roadmap: PlannerBot

## Overview

PlannerBot is a Telegram bot for coordinating D&D game sessions across multiple campaigns inside one Telegram supergroup with forum threads. Each campaign lives in its own thread, has its own DM and membership, and shares the same global availability pool of users.

Tech stack:
- .NET 10
- C#
- Entity Framework Core with PostgreSQL
- Telegram.Bot
- TickerQ
- Humanizer

---

## Current State

Most of the core multi-campaign workflow is already implemented and usable:
- global availability collection
- per-campaign slot calculation
- reaction-based voting
- saved game scheduling with reminders
- forum-thread based campaign separation
- campaign turn rotation
- super-admin backdoors for selected DM flows

This is no longer an early prototype. The project has real workflow coverage, but it still needs cleanup, stronger validation, and better automated safety nets.

### Data Model

- `User` — Telegram user profile plus global `IsActive` pause flag.
- `Response` — global availability records stored in UTC.
- `ForumThread` — tracked forum topics with chat/thread identity and current name.
- `ServiceThread` — marks administrative threads that are not campaign threads.
- `Campaign` — active campaign linked to a forum thread, with DM and optional turn-order position.
- `CampaignMember` — membership join table.
- `SavedGame` — approved scheduled game for a campaign.
- `VoteSession` — active vote state, aggregate counters, creator, campaign, and expiry metadata.
- `VoteSessionVote` — per-user vote records with deduplication.
- `AvailableSlot` — cached campaign-specific candidate slots produced after planning.
- `CampaignOrderState` — current turn-holder for a chat.
- `CampaignOrderDraft` — persisted draft for `/order_set`.
- `CampaignJoinDraft` — persisted draft for super-admin multi-select `/campaign_join`.

### Commands

| Command | Who | Current behavior |
|---|---|---|
| `/start` | Anyone | Shows command reference. |
| `/yes hh:mm` | Anyone | Marks the user available today at the specified time. |
| `/no` | Anyone | Marks the user unavailable today and cancels today's saved games/reminders. |
| `/prob` | Anyone | Marks the user as tentatively available today. |
| `/get` | Anyone | Shows the 12-day availability view for active users. |
| `/plan` | Anyone | Opens inline planning UI for the next 12 days, then recalculates cached slots per campaign. |
| `/pause` / `/unpause` | Anyone | Toggles global activity state. |
| `/weekly` | Anyone | Schedules the recurring weekly planning reminder. |
| `/vote [dd.MM.yyyy HH:mm]` | DM / super-admin | Starts a vote from a cached slot or a manually entered time. In service threads, campaign selection comes first. |
| `/saved` | Anyone | Shows upcoming saved games for the current campaign or asks for a campaign in a service thread. |
| `/unsave` | DM / super-admin | Removes a saved game and cancels its reminder jobs. |
| `/campaign_new` | Anyone / super-admin | Creates a campaign in the current forum thread. Super-admin may pick any DM. |
| `/campaign_join` | Anyone / super-admin | Regular users join the current campaign. Super-admin can pick a campaign first when needed, then multi-select any users, including inactive ones, and selected inactive users are reactivated on save. |
| `/campaign_leave` | Member | Leaves the current campaign. |
| `/campaign_next` | Turn-holder DM / super-admin | Advances turn order to the next campaign in the chat rotation. |
| `/service_thread` | Anyone | Toggles the current forum thread as administrative. |
| `/order` | Anyone | Shows current campaign rotation with the active turn-holder. |
| `/order_set` | Anyone | Interactive inline editor for campaign rotation order, persisted as a draft until save or cancel. |

### Voting System

- Votes are cast with Telegram reactions: `👍` and `👎`.
- Vote deduplication is enforced per user per session.
- Aggregate counters are updated atomically in the database.
- Outcome evaluation reads fresh DB state instead of relying on stale tracked entities.
- `Saved` requires all active campaign members to vote `For`.
- `NoConsensus` requires at least half of active users, rounded up, to vote `Against`.
- Votes expire automatically after 24 hours.
- Non-voters are reminded after 12 hours.
- Vote-against users are excluded from game reminders.

### Scheduling And Jobs

- Saved games schedule reminder jobs at multiple intervals before the game.
- Vote expiry and vote reminder flows are implemented with TickerQ.
- Weekly reminder scheduling is implemented.
- Saved-game deletion also removes associated reminder jobs.

### Admin / Permission Model

- DM-only flows exist for voting and unsaving.
- Super-admin currently bypasses selected DM restrictions without changing DM ownership.
- Callback ownership is enforced server-side using callback payload ownership checks.
- Service threads and campaign threads are treated differently for command routing.

---

## What Was Recently Added

- Atomic vote counter increments and fresh-state reads for vote outcome evaluation and vote message rendering.
- Root `AGENTS.md` rules clarifying .NET command escalation and migration generation policy.
- Super-admin `/campaign_join` backdoor with:
  - campaign picker in service threads
  - multi-select inline user picker
  - persisted draft state in DB
  - visible markers for existing members
  - reactivation of inactive selected users on save

---

## Gaps And Risks

These are the most obvious weaknesses in the current codebase:

- Too much command and callback logic is concentrated in `CommandHandler` and `UpdateHandler`.
- Business rules are partly duplicated across direct command flows and callback flows.
- There is very little visible automated test coverage around voting, scheduling, and permission logic.
- Draft-like flows now exist in multiple places and use similar but separate persistence patterns.
- Some operations still do repeated `SaveChangesAsync()` calls inside loops instead of batching.
- Authorization rules are spread across handlers instead of being centralized.
- The roadmap and usage text can drift from behavior unless they are maintained aggressively.

---

## Recommended Next Improvements

### Product / UX

- Add pagination or filtering for large user lists in the super-admin `/campaign_join` picker.
- Show campaign membership lists and DM information with a dedicated command.
- Add clearer feedback when a user is auto-reactivated by super-admin membership changes.

### Reliability

- Add idempotency checks around job creation where duplicates would be harmful.
- Reduce multi-step state changes that save partially and could leave inconsistent intermediate state.
- Audit all places that mix `ExecuteUpdateAsync` with tracked entities and refresh rules.

### Refactoring

- Split `UpdateHandler` callback cases into smaller dedicated handler methods or action-specific services.
- Split `CommandHandler` into feature-oriented command services:
  - campaign commands
  - voting commands
  - saved-game commands
  - order-management commands
- Extract shared authorization helpers for DM, member, super-admin, and callback-owner checks.
- Unify draft storage patterns behind a reusable small draft abstraction or utility.
- Centralize user-facing Russian message templates so tone and wording stay consistent.

### Observability

- Add structured logs for campaign membership changes.
- Add log coverage for slot recalculation decisions and collision detection.
- Add warning logs for suspicious callback flows, stale draft usage, and race-prone edge cases.

### Data / Domain Model

- Consider explicit audit history for administrative actions like forced joins, forced reactivations, and unsaves.

---

## Suggested Near-Term Plan

1. Refactor callback handling into smaller feature-specific units before `UpdateHandler` grows further.
2. Add audit-style logging for super-admin actions and other privileged mutations.
3. Improve large-list UX for admin pickers.
4. Review repeated DB write patterns and batch where practical.
