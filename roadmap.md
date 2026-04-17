# Roadmap: PlannerBot

## Overview

PlannerBot is a Telegram bot for coordinating D&D game sessions across multiple campaigns within a single
Telegram supergroup that uses forum threads. Each campaign is run by a separate Dungeon Master (DM), lives in
its own forum thread, and has its own set of players and game schedule. Player availability remains **global** —
a player fills in their schedule once and that data is shared across all campaigns.

**Tech stack:** .NET 10, C#, Entity Framework Core (PostgreSQL), Telegram.Bot SDK, TickerQ (scheduled jobs),
Humanizer (Russian time-span humanization in messages).

---

## Current State (as of Phase 10)

All multi-campaign support and turn-order mechanics are implemented. Below is a summary of what is live.

### Entities

- **`User`** — `Id`, `Username`, `Name`, `IsActive`. `IsActive` is a global pause flag toggled by `/pause`/`/unpause`.
- **`Response`** — stores a user's availability for a UTC date/time: `Availability` (Yes/No/Probably/Unknown) and
  `DateTime` (nullable start time).
- **`ForumThread`** — `Id`, `ChatId`, `ThreadId`, `Name`, `IsClosed`. Tracked reactively via service messages.
- **`ServiceThread`** — marks a forum thread as administrative. Toggled by `/service_thread`.
- **`Campaign`** — `Id`, `DungeonMasterId`, `ForumThreadId`, `IsActive`, `OrderIndex`, `CreatedAt`. Name and
  chat ID derived from the linked `ForumThread`.
- **`CampaignMember`** — join table `(CampaignId, UserId, JoinedAt)`.
- **`SavedGame`** — `Id`, `DateTime` (UTC), `CampaignId`.
- **`VoteSession`** — `Id`, `ChatId`, `MessageId`, `ThreadId`, `GameDateTime`, `VoteCount`, `AgainstCount`,
  `Outcome`, `CreatedAt`, `ExpiresAt`, `CreatorId`, `CampaignId`.
- **`VoteSessionVote`** — per-user vote record (For/Against) with unique constraint on `(VoteSessionId, UserId)`.
- **`AvailableSlot`** — `Id`, `CampaignId`, `DateTime` (UTC), `ComputedAt`. Rebuilt on every `/plan` completion.
- **`CampaignOrderState`** — one row per chat; `CurrentCampaignId` tracks whose turn it is in the rotation.
- **`CampaignOrderDraft`** — one row per (UserId, ChatId); stores the in-progress `/order_set` draft.
  Deleted on save or cancel.

### Commands

| Command                   | Who           | Description                                                                                                                                  |
|---------------------------|---------------|----------------------------------------------------------------------------------------------------------------------------------------------|
| `/start`                  | Anyone        | Show command reference.                                                                                                                      |
| `/yes hh:mm`              | Anyone        | Mark today as available with a start time.                                                                                                   |
| `/no`                     | Anyone        | Mark today as unavailable. Cancels today's saved games and reminders.                                                                        |
| `/prob`                   | Anyone        | Mark today as "probably available".                                                                                                          |
| `/get`                    | Anyone        | Show 12-day availability grid for all active users.                                                                                          |
| `/plan`                   | Anyone        | Inline calendar for the next 8 days. On "Done": computes per-campaign slots, caches to `AvailableSlot`, posts summary.                       |
| `/pause` / `/unpause`     | Anyone        | Toggle `User.IsActive` globally.                                                                                                             |
| `/weekly`                 | Anyone        | Schedule a weekly Saturday 21:00 UTC `/plan` reminder (cron job).                                                                           |
| `/vote [dd.MM.yyyy HH:mm]`| DM / Admin    | Start a vote. Without args: pick a cached available slot (slot picker). With timestamp: manual date entry. In a service thread: shows a campaign picker first. DM-only (or super-admin). |
| `/saved`                  | Anyone        | List upcoming saved games for the current campaign.                                                                                          |
| `/unsave`                 | DM / Admin    | Inline keyboard to delete a saved game and cancel its reminders. DM-only (or super-admin).                                                   |
| `/campaign_new`           | Anyone        | Create a campaign in the current thread (sender becomes DM). Super-admin gets a DM picker to choose who becomes DM.                          |
| `/campaign_join`          | Anyone        | Join the campaign of the current thread.                                                                                                     |
| `/campaign_leave`         | Member        | Leave the campaign of the current thread.                                                                                                    |
| `/campaign_next`          | Turn-holder DM / Admin | Advance the turn to the next campaign in the rotation. Only the DM of the current turn-holder (or super-admin) may use this.          |
| `/service_thread`         | Anyone        | Toggle the current thread's service-thread status.                                                                                           |
| `/order`                  | Anyone        | Display the campaign rotation queue with a 🎯 indicator on the current turn-holder.                                                          |
| `/order_set`              | Anyone        | Interactive inline keyboard to configure the rotation order. Draft is stored in DB; discarded on cancel, applied on save.                    |

### Background jobs

- `expire_vote_session` — marks a vote session as Expired after 24 h.
- `send_vote_reminder` — pings non-voters after 12 h.
- `send_game_reminder` — notifies campaign members before a saved game (48 h, 24 h, 5 h, 3 h, 1 h, 10 min).
  Posted in the campaign's forum thread, mentioning the DM and all members.
- `send_weekly_voting_reminder` — Saturday cron job (global).

### Super-admin

The user with username `dadyarri` has elevated privileges:
- Can run any DM-restricted command (`/vote`, `/unsave`, `/campaign_next`) as if they were the actual DM, without changing campaign ownership.
- `/campaign_new` shows a DM picker so they can assign any active user as the DM of the new campaign.

---
