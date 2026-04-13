# Roadmap: Multi-Campaign Support

## Overview

The goal is to support multiple D&D campaigns within a single Telegram supergroup that uses forum threads. Each campaign
is run by a separate Dungeon Master (DM), lives in its own forum thread, and has its own set of players and game
schedule. Player availability remains **global** — a player fills in their schedule once and that data is shared across
all campaigns.

---

## Current State

### Entities

- **`User`** — `Id`, `Username`, `Name`, `IsActive`. `IsActive` is a global pause flag toggled by `/pause` and
  `/unpause`.
- **`Response`** — stores a user's availability for a UTC date/time: `Availability` (Yes/No/Probably/Unknown) and
  `DateTime` (nullable, set when the player picks a start time).
- **`SavedGame`** — `Id`, `DateTime` (UTC). A confirmed session. No campaign association yet.
- **`VoteSession`** — `Id`, `ChatId`, `MessageId`, `ThreadId`, `GameDateTime`, `VoteCount`, `AgainstCount`, `Outcome`,
  `CreatedAt`, `ExpiresAt`, `CreatorUsername`. No campaign association yet.
- **`VoteSessionVote`** — per-user vote record (For/Against) with unique constraint on `(VoteSessionId, UserId)`.

### Commands

| Command                  | Current behaviour                                                                                                                                                         |
|--------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `/yes hh:mm`             | Mark today as available with a start time. Triggers auto-voting if everyone is available in current day                                                                   |
| `/no`                    | Mark today as unavailable. Cancels any reminder jobs for today's saved games.                                                                                             |
| `/prob`                  | Mark today as "probably available".                                                                                                                                       |
| `/get`                   | Shows a 12-day availability grid for all active users.                                                                                                                    |
| `/pause` / `/unpause`    | Toggle `User.IsActive`.                                                                                                                                                   |
| `/plan`                  | Sends an inline calendar for the next 8 days. The "Done" button fills unmarked days as unavailable and auto-creates voting sessions for any day where all users are free. |
| `/vote dd.MM.yyyy HH:mm` | Anyone can start a vote. No campaign or DM restriction.                                                                                                                   |
| `/saved`                 | Lists all future `SavedGame` rows with their internal integer IDs.                                                                                                        |
| `/unsave <id>`           | Deletes a `SavedGame` by integer ID. Cancels associated reminder jobs.                                                                                                    |
| `/weekly`                | Schedules a weekly Saturday reminder (cron job via TickerQ).                                                                                                              |

### Voting

Voting is **reaction-based** — the bot posts a message and reacts 👍 to it. Players react 👍 (for) or 👎 (against). The bot
monitors `MessageReaction` updates. A session is saved when `VoteCount >= activeUsersCount`; no-consensus when
`AgainstCount >= ceil(activeUsersCount / 2)`. Sessions expire after 24 hours; a reminder is sent after 12 hours.

### Callback data format

All callback data uses semicolon-separated strings: `"action;param1;param2;..."`. Routing is handled in
`UpdateHandler.OnCallbackQuery` via a `switch` on `split[0]`.

### Background jobs

The bot uses **TickerQ** for scheduled tasks:

- `expire_vote_session` — marks a vote session as Expired after 24 h.
- `send_vote_reminder` — pings non-voters after 12 h.
- `send_game_reminder` — notifies players before a saved game.
- `send_weekly_voting_reminder` — Saturday cron job.

---

## Phase 1: Refactoring

Clean up the existing codebase before adding multi-campaign logic. This phase has no user-visible changes.

### 1.1 Extract `SlotCalculator`

Move the "are all users free on this date?" logic out of `AvailabilityManager.CheckIfDateIsAvailable` into a dedicated
`SlotCalculator` service.

`SlotCalculator` will expose:

- `GetAvailableSlotsForCampaign(Campaign campaign, IEnumerable<Response> allResponses) → IEnumerable<DateTime>` — finds
  all UTC datetimes where **every active member** of the campaign has responded `Yes` with a non-zero start time, with
  no saved game already on that date **for this campaign**.
-

`GetAvailableSlotsForAllCampaigns(IEnumerable<Campaign> campaigns, IEnumerable<Response> allResponses) → Dictionary<Campaign, IEnumerable<DateTime>>` —
convenience wrapper.

### 1.2 Standardise callback routing

The current `OnCallbackQuery` switch statement will grow significantly. Before adding more cases, standardise the
pattern:

- Define constants or an enum for callback prefixes (e.g. `"plan"`, `"pstatus"`, `"ptime"`, `"pback"`, `"delete"`,
  `"vote_cancel"`).
- Add a `ResolveCampaignFromContext(long chatId, int? threadId) → Campaign?` helper that looks up the campaign
  associated with a given thread. Returns `null` for service threads or untracked threads.
- Add a `ShowCampaignPicker(long chatId, int? threadId, long userId, string action) → Task` helper that posts an inline
  keyboard listing campaigns relevant to the user (member or DM), for use in service-thread flows.

### 1.3 Per-campaign context resolution

Centralise the pattern: *"which campaign does this command apply to?"*:

1. If the message is in a campaign thread → that campaign.
2. If the message is in a service thread → show a campaign picker inline keyboard.
3. If the thread is unknown → treat as service thread.

This logic will be called by every campaign-scoped command (`/vote`, `/saved`, `/unsave`, `/steal`, `/campaign_join`,
etc.).

### 1.4 Remove auto-voting from `/yes` and simplify `UpdateResponseForDate`

The `/yes` command currently auto-creates a voting session when all users are available for today. Remove this
behaviour — voting must always be initiated explicitly by a DM.

`AvailabilityManager.UpdateResponseForDate` currently calls `CheckIfDateIsAvailable` internally and returns
`Task<DateTime?>` so callers can trigger voting. Once auto-voting from `/yes` is removed, no caller uses that return
value (the `ptime` and `pstatus` callbacks already ignore it). Simplify the method to return `Task` and remove the
internal `CheckIfDateIsAvailable` call — slot-checking belongs in `SlotCalculator` (step 1.1).

### 1.5 Rename `"delete"` callback to `"plan_done"`

The "Done" button in `/plan` currently uses `"delete"` as its callback data — a confusing name that suggests deletion
rather than finishing the planning flow. Rename it to `"plan_done"` in both `KeyboardGenerator.GeneratePlanKeyboard` (
button creation) and `UpdateHandler.OnCallbackQuery` (case handler).

### 1.6 Remove duplicate `CultureInfo` from `Jobs`

`Jobs` declares its own static `CultureInfo RussianCultureInfo = new("ru-RU")`, duplicating the same field in
`TimeZoneUtilities`. Inject `TimeZoneUtilities` into `Jobs` and use `timeZoneUtilities.GetRussianCultureInfo()` instead.

---

## Phase 2: Forum Thread Tracking (`ForumThread`)

The Telegram Bot API does not provide a way to list forum threads. The bot must track them reactively by listening to
service messages.

**Service messages to handle in `UpdateHandler.OnMessage`:**

| `Message.Type`       | Action                                                                                           |
|----------------------|--------------------------------------------------------------------------------------------------|
| `ForumTopicCreated`  | Insert `ForumThread` with `ThreadId = msg.MessageThreadId`, `Name = msg.ForumTopicCreated.Name`. |
| `ForumTopicEdited`   | Update `ForumThread.Name`.                                                                       |
| `ForumTopicClosed`   | Set `ForumThread.IsClosed = true`.                                                               |
| `ForumTopicReopened` | Set `ForumThread.IsClosed = false`.                                                              |

These are **upsert** operations keyed on `(ChatId, ThreadId)`.

### New entity: `ForumThread`

| Column     | Type        | Notes                        |
|------------|-------------|------------------------------|
| `Id`       | int, PK     | Auto-increment               |
| `ChatId`   | long        | Telegram chat ID             |
| `ThreadId` | int         | Telegram `message_thread_id` |
| `Name`     | string(128) | Current thread name          |
| `IsClosed` | bool        | Whether the thread is closed |

Unique constraint on `(ChatId, ThreadId)`.

**New service:** `ForumThreadTracker` — called from `OnMessage` for the four topic service message types. Keeps
`ForumThread` in sync with Telegram state.

---

## Phase 3: Campaigns and Members

### New entity: `Campaign`

| Column            | Type                       | Notes                 |
|-------------------|----------------------------|-----------------------|
| `Id`              | int, PK                    |                       |
| `DungeonMasterId` | long, FK → `User.Id`       | The DM who created it |
| `ForumThreadId`   | int, FK → `ForumThread.Id` | One-to-one            |
| `IsActive`        | bool                       | Soft-delete flag      |
| `CreatedAt`       | DateTime                   | UTC                   |

The campaign's name and chat ID are always derived from the linked `ForumThread` — no duplication. One `ForumThread` →
at most one active `Campaign`.

### New entity: `CampaignMember`

| Column       | Type                    | Notes |
|--------------|-------------------------|-------|
| `CampaignId` | int, FK → `Campaign.Id` |       |
| `UserId`     | long, FK → `User.Id`    |       |
| `JoinedAt`   | DateTime                | UTC   |

Unique constraint on `(CampaignId, UserId)`.

`User.IsActive` (the global pause) is orthogonal to campaign membership — a paused user is excluded from availability
checks across all campaigns. `CampaignMember` only records which campaigns a user belongs to.

### New entity: `ServiceThread`

| Column          | Type                       | Notes  |
|-----------------|----------------------------|--------|
| `Id`            | int, PK                    |        |
| `ForumThreadId` | int, FK → `ForumThread.Id` | Unique |

A `ServiceThread` row marks a forum thread as administrative (not a campaign thread). The `/service_thread` command
toggles this: if no row exists → inserts one; if a row exists → deletes it.

### New commands (Phase 3)

| Command            | Behaviour                                                                                                                                                                                             |
|--------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `/campaign_new`    | Creates a `Campaign` in the current thread. Sender becomes DM. Fails if: thread is a `ServiceThread`, thread already has an active campaign, or sender already has an active campaign in this thread. |
| `/campaign_join`   | Adds the sender to the current thread's campaign as a `CampaignMember`. In a service thread: shows an inline keyboard listing active campaigns.                                                       |
| `/campaign_leave`  | Removes the sender from the current thread's campaign. Inline keyboard in service threads.                                                                                                            |
| `/campaign_delete` | Sets `Campaign.IsActive = false` (soft-delete). DM-only. In a service thread: inline keyboard filtered to campaigns where sender is DM.                                                               |
| `/service_thread`  | Toggles `ServiceThread` for the current thread.                                                                                                                                                       |

---

## Phase 4: Database Migration

Add `CampaignId` foreign keys to `SavedGame` and `VoteSession`.

### Changes to existing entities

**`SavedGame`** — add `CampaignId int NOT NULL FK → Campaign.Id`.  
**`VoteSession`** — add `CampaignId int NOT NULL FK → Campaign.Id`.

### Migration strategy

- Both FK columns are **NOT NULL**.
- Existing data in `SavedGame` and `VoteSession` will be **deleted** in the migration — DMs will set up campaigns from
  scratch.
- If deletion is not feasible (e.g. data must be preserved), create a temporary "Legacy" campaign and assign all
  existing rows to it.
- Breaking changes are acceptable; backward compatibility is not required.

---

## Phase 5: Changes to `/plan`

### What stays the same

The player-facing interaction is unchanged: `/plan` shows the inline calendar, the player picks days and availability (
Yes/No/Probably), and for "Yes" days picks a start time. The "Done" button (`"plan_done"` callback, renamed in Phase 1)
still calls `SetUnavailableForUnmarkedDays`.

### What changes

After `SetUnavailableForUnmarkedDays` completes, instead of auto-creating voting sessions:

1. **No automatic vote creation** — remove the loop in the `"delete"` callback handler that called
   `votingManager.SendVotingMessage`.
2. **Compute per-campaign free slots** — call `SlotCalculator.GetAvailableSlotsForAllCampaigns` using the fresh
   `Response` data for the coming 8 days.
3. **Cache results** — write computed slots to `AvailableSlot`, replacing any existing rows for the same campaign (full
   replacement, not append).
4. **Send summary** — post a message in the same thread listing free slots grouped by campaign, e.g.:
   > **Throne of Darkness:** Saturday 18:00, Sunday 15:00  
   > **Lost Mines:** Saturday 18:00

### New entity: `AvailableSlot`

| Column       | Type                    | Notes                       |
|--------------|-------------------------|-----------------------------|
| `Id`         | int, PK                 |                             |
| `CampaignId` | int, FK → `Campaign.Id` |                             |
| `DateTime`   | DateTime                | UTC slot                    |
| `ComputedAt` | DateTime                | UTC, set on every recompute |

The cache is rebuilt whenever **any** player completes `/plan`. Old rows for the same campaign are deleted before
inserting new ones.

---

## Phase 6: `/steal` — Quick Vote Creation for DMs

`/steal` gives a DM a one-click way to schedule a game from the cached `AvailableSlot` table.

**Flow — in a campaign thread:**

1. Bot queries `AvailableSlot` for that campaign, filtering out slots in past.
2. If no slots cached → reply "No available slots found. Ask players to fill in `/plan` first."
3. Else → show an inline keyboard with one button per slot (formatted in Moscow time).
4. DM taps a slot → bot calls `votingManager.SendVotingMessage` for that campaign's thread, scoped to that campaign's
   active members.

**Flow — in a service thread:**

1. Show a campaign picker filtered to campaigns where the user is DM.
2. After campaign selection → show slot picker for that campaign, filtering out slots in past.
3. After slot selection → send vote in the campaign's thread (`ForumThread.ThreadId`).

**Restrictions:**

- Only the DM of a campaign can use `/steal` for that campaign.

---

## Phase 7: Restrict `/vote`, `/saved`, `/unsave` to Campaigns

### `/vote`

- **DM-only**: only the DM of the campaign associated with the current thread may run `/vote`.
- In a service thread: campaign picker, then date/time input (or drop date/time input entirely and redirect to
  `/steal`).
- The created `VoteSession` gets `CampaignId` set.
- The voting message mentions only active members of that campaign (not all active users globally).
- Voting outcome thresholds use `CampaignMember` count, not global `User.IsActive` count.

### `/saved`

- Shows only games for the campaign of the current thread.
- **No internal IDs** in the output — use a formatted date/time list only.

### `/unsave`

- **DM-only**.
- Instead of `/unsave <id>`, shows an inline keyboard listing future saved games for the current campaign. DM taps one
  to remove it.
- Cancels associated reminder TickerQ jobs.

---

## Phase 8: Collision Detection

Before creating a vote (via `/vote` or `/steal`), check `SavedGame` for conflicts:

- Query: does any active member of the target campaign have a `SavedGame` in **any other campaign** on the same date?
- If yes → send a warning message listing the conflicting campaigns and players. The DM can proceed or abort using
  inline keyboard.
- This is an informational warning, not a hard block.

---

## Phase 9: Route Reminders to Campaign Threads

### Game reminders

When `SavedGame` is created, schedule `send_game_reminder` jobs targeting `ForumThread.ThreadId` of the campaign.
Message format:

> 🎲 Game in **<Campaign Name>** in <time until game>.  
> DM: @DungeonMasterUsername  
> Players: @Player1 @Player2 ...

### Weekly planning reminder

`/weekly` remains global — not tied to any campaign. The cron job sends a reminder to prompt all users to fill in
`/plan`.

---

## Command Summary

### New commands

| Command            | Who    | Description                                                                              |
|--------------------|--------|------------------------------------------------------------------------------------------|
| `/campaign_new`    | Anyone | Create a campaign in the current thread (sender = DM). Rejected in service threads.      |
| `/campaign_join`   | Anyone | Join the campaign of the current thread. Inline picker in service threads.               |
| `/campaign_leave`  | Member | Leave the campaign of the current thread. Inline picker in service threads.              |
| `/campaign_delete` | DM     | Soft-delete a campaign. Inline picker in service threads (campaigns where sender is DM). |
| `/service_thread`  | Anyone | Toggle the current thread's service-thread status.                                       |
| `/steal`           | DM     | Pick a cached available slot and instantly start a vote.                                 |

### Global commands (no campaign scope)

| Command                        | Notes                                                                                         |
|--------------------------------|-----------------------------------------------------------------------------------------------|
| `/plan`                        | Unchanged interaction. After "Done": computes per-campaign slots, caches them, shows summary. |
| `/yes`, `/no`, `/prob`, `/get` | Unchanged.                                                                                    |
| `/pause`, `/unpause`           | Unchanged. Affects all campaigns.                                                             |
| `/weekly`                      | Unchanged.                                                                                    |

### Campaign-scoped commands (changed)

| Command   | Change                                                               |
|-----------|----------------------------------------------------------------------|
| `/vote`   | DM-only. Scoped to current campaign. Mentions campaign members only. |
| `/saved`  | Scoped to current campaign. No internal IDs in output.               |
| `/unsave` | DM-only. Inline keyboard instead of ID argument.                     |

---

## Implementation Order

1. **Phase 1** — Refactoring: remove auto-voting from `/yes` and simplify `UpdateResponseForDate`, extract
   `SlotCalculator`, standardise callback routing, add `ResolveCampaignFromContext` helper, rename `"delete"` callback
   to `"plan_done"`, remove duplicate `CultureInfo` from `Jobs`.
2. **Phase 2** — Forum thread tracking: `ForumThread` entity + service message handling in `UpdateHandler`.
3. **Phase 3** — Campaigns: `Campaign`, `CampaignMember`, `ServiceThread` entities + `/campaign_new`, `/campaign_join`,
   `/campaign_leave`, `/campaign_delete`, `/service_thread` commands.
4. **Phase 4** — DB migration: add `CampaignId` to `SavedGame` and `VoteSession`, delete stale data.
5. **Phase 5** — Update `/plan` "Done" handler: remove auto-voting, add slot computation, cache to `AvailableSlot`, send
   summary.
6. **Phase 6** — `/steal` command.
7. **Phase 7** — Restrict `/vote`, `/saved`, `/unsave` to campaigns with updated scoping and DM checks.
8. **Phase 8** — Collision detection before vote creation.
9. **Phase 9** — Route game reminder jobs to campaign threads with DM and player mentions.
