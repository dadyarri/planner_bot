# Roadmap: PlannerBot

## Overview

PlannerBot is a Telegram bot for coordinating D&D game sessions across multiple campaigns within a single
Telegram supergroup that uses forum threads. Each campaign is run by a separate Dungeon Master (DM), lives in
its own forum thread, and has its own set of players and game schedule. Player availability remains **global** —
a player fills in their schedule once and that data is shared across all campaigns.

**Tech stack:** .NET 10, C#, Entity Framework Core (PostgreSQL), Telegram.Bot SDK, TickerQ (scheduled jobs),
Humanizer (Russian time-span humanization in messages).

---

## Current State (as of Phase 9)

All foundational multi-campaign support is implemented. Below is a summary of what is live.

### Entities

- **`User`** — `Id`, `Username`, `Name`, `IsActive`. `IsActive` is a global pause flag toggled by `/pause`/`/unpause`.
- **`Response`** — stores a user's availability for a UTC date/time: `Availability` (Yes/No/Probably/Unknown) and
  `DateTime` (nullable start time).
- **`ForumThread`** — `Id`, `ChatId`, `ThreadId`, `Name`, `IsClosed`. Tracked reactively via service messages.
- **`ServiceThread`** — marks a forum thread as administrative. Toggled by `/service_thread`.
- **`Campaign`** — `Id`, `DungeonMasterId`, `ForumThreadId`, `IsActive`, `CreatedAt`. Name and chat ID derived from
  the linked `ForumThread`.
- **`CampaignMember`** — join table `(CampaignId, UserId, JoinedAt)`.
- **`SavedGame`** — `Id`, `DateTime` (UTC), `CampaignId`.
- **`VoteSession`** — `Id`, `ChatId`, `MessageId`, `ThreadId`, `GameDateTime`, `VoteCount`, `AgainstCount`,
  `Outcome`, `CreatedAt`, `ExpiresAt`, `CreatorUsername`, `CampaignId`.
- **`VoteSessionVote`** — per-user vote record (For/Against) with unique constraint on `(VoteSessionId, UserId)`.
- **`AvailableSlot`** — `Id`, `CampaignId`, `DateTime` (UTC), `ComputedAt`. Rebuilt on every `/plan` completion.

### Commands

| Command                   | Who    | Description                                                                                                                       |
|---------------------------|--------|-----------------------------------------------------------------------------------------------------------------------------------|
| `/start`                  | Anyone | Show command reference.                                                                                                           |
| `/yes hh:mm`              | Anyone | Mark today as available with a start time.                                                                                        |
| `/no`                     | Anyone | Mark today as unavailable. Cancels today's saved games and reminders.                                                             |
| `/prob`                   | Anyone | Mark today as "probably available".                                                                                               |
| `/get`                    | Anyone | Show 12-day availability grid for all active users.                                                                               |
| `/plan`                   | Anyone | Inline calendar for the next 8 days. On "Done": computes per-campaign slots, caches to `AvailableSlot`, posts summary.            |
| `/pause` / `/unpause`     | Anyone | Toggle `User.IsActive` globally.                                                                                                  |
| `/weekly`                 | Anyone | Schedule a weekly Saturday 21:00 UTC `/plan` reminder (cron job).                                                                |
| `/vote [dd.MM.yyyy HH:mm]`| DM     | Start a vote. Without args: pick a cached available slot (slot picker). With timestamp: manual date entry. In a service thread: shows a campaign picker first. DM-only. |
| `/saved`                  | Anyone | List upcoming saved games for the current campaign.                                                                               |
| `/unsave`                 | DM     | Inline keyboard to delete a saved game and cancel its reminders. DM-only.                                                        |
| `/campaign_new`           | Anyone | Create a campaign in the current thread (sender becomes DM).                                                                      |
| `/campaign_join`          | Anyone | Join the campaign of the current thread.                                                                                          |
| `/campaign_leave`         | Member | Leave the campaign of the current thread.                                                                                         |
| `/campaign_pause`         | DM     | Pause (soft-delete) a campaign (`IsActive = false`), transferring the turn to the next. DM-only.             |
| `/service_thread`         | Anyone | Toggle the current thread's service-thread status.                                                                                |

### Background jobs

- `expire_vote_session` — marks a vote session as Expired after 24 h.
- `send_vote_reminder` — pings non-voters after 12 h.
- `send_game_reminder` — notifies campaign members before a saved game (48 h, 24 h, 5 h, 3 h, 1 h, 10 min).
  Posted in the campaign's forum thread, mentioning the DM and all members.
- `send_weekly_voting_reminder` — Saturday cron job (global).

---

## Phase 10: Campaign Order (Round-Robin Turn Queue)

### Problem

When multiple DMs want to schedule sessions there is no coordination about whose turn it is. One DM can
monopolise slots while others wait. This phase introduces a lightweight, implicit turn-queue so campaigns rotate
fairly.

### Design Principles

- **Explicit circular order.** Each campaign has a fixed `OrderIndex` (integer position). The order pointer does
  **not** advance automatically. A DM must explicitly pass the turn by pausing their campaign (`/campaign_pause`). Order is configured via `/order_set`.
- **Soft enforcement.** DMs are warned, not hard-blocked, when acting out of turn. This avoids frustrating
  deadlocks when the leading DM has no available slots.
- **Turn transfer on pause.** When a DM pauses their campaign (`/campaign_pause`), the order pointer advances
  to the next campaign in the ring.

---

### 10.1 Data Model Changes

Add one column to `Campaign` and one new table:

**`Campaign` — new column:**

| Column       | Type   | Default | Notes                                                    |
|--------------|--------|---------|----------------------------------------------------------|
| `OrderIndex` | `int?` | `null`  | Position in the rotation ring. `null` = not participating in the order. Lower = earlier in ring. |

**New entity: `CampaignOrderState`** — a single-row table (per chat) holding the global pointer:

| Column          | Type   | Notes                                                                   |
|-----------------|--------|-------------------------------------------------------------------------|
| `Id`            | int PK |                                                                         |
| `ChatId`        | long   | Telegram chat ID. Unique.                                               |
| `CurrentIndex`  | int    | The `OrderIndex` of the campaign whose turn it currently is. Starts at 0. |

**EF migration name:** `AddCampaignOrderFields`

```bash
cd PlannerBot
DATABASE_URL="Host=localhost;Database=planner_bot;Username=postgres;Password=postgres" \
  dotnet ef migrations add AddCampaignOrderFields
```

---

### 10.2 New Service: `CampaignOrderService`

New file: `Services/CampaignOrderService.cs`

Injected dependencies: `AppDbContext`, `ITelegramBotClient`.

#### `GetOrderedCampaigns(long chatId) → Task<List<Campaign>>`

Returns all active campaigns in the chat that have a non-null `OrderIndex`, ordered by `OrderIndex ASC`.
Eagerly loads `ForumThread` and `DungeonMaster`.

#### `GetCurrentCampaign(long chatId) → Task<Campaign?>`

1. Load `CampaignOrderState` for the chat. If none exists, return `null`.
2. Find the active campaign whose `OrderIndex == state.CurrentIndex`. If not found (e.g. campaign was deleted),
   advance the pointer once and retry (up to N attempts to avoid infinite loop).

#### `AdvanceTurn(long chatId) → Task`

1. Loads the ordered campaigns list and `CampaignOrderState`.
2. Increments `CurrentIndex` to the `OrderIndex` of the next campaign in the ring (wraps around).
3. New campaign's `IsActive` becomes `true`
4. Saves changes.
5. Posts a Russian fantasy announcement to the next campaign's forum thread in the same chat (see §10.6 for message format).

#### `SetOrder(long chatId, IReadOnlyList<int> campaignIds) → Task`

Sets `OrderIndex` on each campaign according to the provided list position. Campaigns not in the list get
`OrderIndex = null`. Resets `CampaignOrderState.CurrentIndex` to the first position. Saves changes.

---

### 10.3 Turn Transfer Triggers

The pointer advances in exactly two situations:

1. **`/campaign_pause`** — the DM pauses their campaign. After `Campaign.IsActive` is set to `false`,
   `CampaignOrderService.AdvanceTurn(chatId)` is called.

**Saving a game does NOT advance the pointer.** The DM retains their turn until they explicitly give it up.

---

### 10.4 Soft Enforcement in `/steal` and `/vote`

In `CommandHandler.HandleStealCommand` and `CommandHandler.HandleVoteCommand`, after the campaign is resolved
and the DM check passes:

1. Call `campaignOrderService.GetCurrentCampaign(chatId)`.
2. If `currentCampaign == null` or `currentCampaign.Id == resolvedCampaign.Id` → it is their turn; proceed normally.
3. If the resolved campaign has `OrderIndex == null` → not in the rotation; proceed normally (no enforcement).
4. Otherwise → send an inline keyboard with two buttons:
   - ✅ **Продолжить** — callback `oo;{campaignId};{flowType};{paramUnixTime};{username}`
   - ❌ **Отмена** — callback `oc;{username}`

   With a Russian D&D-flavored warning message explaining whose turn it actually is.

`flowType` is `steal` or `vote`. `paramUnixTime` encodes the selected slot or requested date/time as Unix timestamp.

---

### 10.5 New Commands

#### `/order`

- Available in any thread (campaign or service).
- Calls `GetOrderedCampaigns(chatId)`.
- Posts a numbered queue list showing position, campaign name, DM name, and whose turn it currently is (example):

  > 📜 **Очерёдность походов:**
  > 🎯 1. **Трон Тьмы** (Мастер: Ярослав)
  > 2. **Потерянные Копи** (Мастер: Андрей)
  > 3. **Море Клинков** (Мастер: Саша)

  The current campaign is prefixed with 🎯. If no rotation is set up, replies with a prompt to use `/order_set`.

#### `/order_set`

- Admin/DM-only (any DM of a campaign in the chat).
- Shows an **inline keyboard** listing all registered campaigns in the chat as buttons.
- **Interaction model:**
  - Each campaign button shows the campaign name and its current assigned position number (empty if unassigned).
  - Tapping a campaign button assigns it the next available position number (1, 2, 3 … N), cycling: if already
    at the last position, it wraps back to unassigned on the next tap.
  - State is held in the callback data — no server-side draft is stored; the full ordering is encoded in the
    callback on each interaction.
  - Four control buttons at the bottom:
    - **🔄 Сброс** — resets all assignments to the current saved state (before this keyboard was opened).
    - **❌ Отмена** — removes the keyboard without saving any changes.
    - **💾 Сохранить** — calls `CampaignOrderService.SetOrder(chatId, campaignIds)` with the final ordered list,
      resets the pointer to position 1, then edits the message to show the new `/order` list.
- Callback data format for campaign buttons:
  `ost;{campaignId};{serialisedState};{username}`
  where `serialisedState` is a comma-separated list of `campaignId:position` pairs for all currently-assigned
  campaigns (unassigned campaigns are omitted).
- Callback data format for control buttons:
  `osr;{savedState};{username}` / `osc;{username}` / `oss;{serialisedState};{username}`

---

### 10.6 Message Formats (Russian, D&D Flavor)

**Turn advance notification (posted to next campaign's thread):**
> 🎲 Теперь ваш ход, Мастер **{DM mention}**! Очередь кампании **{Campaign name}** пришла —
> самое время объявить дату следующей битвы.

**Turn advance notification (posted to service threads):**
> ⚔️ Кампания **{previous campaign name}** передала ход. Следующий в очереди —
> **{next campaign name}** (Мастер: {next DM mention}).

**Out-of-turn warning:**
> ⚠️ Сейчас не ваш черёд, Мастер! Первой в очереди стоит кампания **{current campaign name}**
> (Мастер: {DM name}). Вы всё равно хотите продолжить?

**Explicit yield (`/order_next`) confirmation:**
> 🔄 Ход передан. Следующий в очереди — **{next campaign name}** (Мастер: {DM mention}).

---

### 10.7 Callback Routing Changes

In `UpdateHandler.OnCallbackQuery` (switch on `split[0]`), add:

| Action | Handler                                                                                                                              |
|-------|--------------------------------------------------------------------------------------------------------------------------------------|
| `oo`  | Verify username. Parse `flowType` and `paramUnixTime`, re-invoke steal or vote flow without the turn check.                          |
| `oc`  | Verify username. Edit the message to "Отменено.".                                                                                    |
| `ons` | Verify username. Resolve campaign, call `CampaignOrderService.AdvanceTurn(chatId)`. Send turn-transfer announcement.                 |
| `ost` | Verify username. Parse `serialisedState`, toggle position for the tapped campaign, rebuild and edit the keyboard in place.           |
| `osr` | Verify username. Parse `savedState`, restore it, rebuild and edit the keyboard in place.                                             |
| `osc` | Verify username. Delete (or clear reply markup of) the keyboard message.                                                             |
| `oss` | Verify username. Parse `serialisedState`, call `CampaignOrderService.SetOrder(chatId, ...)`, edit message to show new `/order` list. |

Add constants to `CallbackActions.cs`:

```csharp
public const string OrderOverride    = "oo";
public const string OrderCancel      = "oc";
public const string OrderNextSelect  = "ons";
public const string OrderSetToggle   = "ost";
public const string OrderSetReset    = "osr";
public const string OrderSetCancel   = "osc";
public const string OrderSetSave     = "oss";
```

---

### 10.8 Edge Cases

| Scenario | Handling |
|---|---|
| Campaign paused mid-queue (`/campaign_pause`) | `IsActive = false` → excluded from `GetOrderedCampaigns`. `OrderIndex` set to `null`. `AdvanceTurn` called automatically. |
| Campaign has `OrderIndex = null` | Not in the rotation; no enforcement applied when its DM uses `/steal` or `/vote`. |
| Only one campaign in rotation | Turn check always passes; `/order_next` advances pointer to itself (no visible effect). |
| No rotation set up yet | `CampaignOrderState` does not exist → `GetCurrentCampaign` returns `null` → enforcement skipped everywhere. |
| All campaigns in rotation are paused/deleted | Pointer can't resolve → log warning, treat as no current campaign. |

---

### 10.9 Implementation Order

1. Add `OrderIndex` column to `Campaign` entity (`Data/Campaign.cs`).
2. Add `CampaignOrderState` entity (`Data/CampaignOrderState.cs`) and register in `AppDbContext`.
3. Generate EF migration `AddCampaignOrderFields`.
4. Implement `CampaignOrderService` (`Services/CampaignOrderService.cs`) with all five methods.
5. Register `CampaignOrderService` in DI (`Program.cs`).
6. Call `AdvanceTurn` in `CampaignManager.DeleteCampaign` (the method backing `/campaign_pause`) after deactivating the campaign.
7. Add soft enforcement to `HandleStealCommand` and `HandleVoteCommand` in `CommandHandler.cs`.
8. Add callback handlers for `oo`, `oc`, `ons`, `ost`, `osr`, `osc`, `oss` in `UpdateHandler.OnCallbackQuery`.
9. Add constants to `CallbackActions.cs`.
10. Implement `/order` command in `CommandHandler.cs`.
11. Implement `/order_set` command in `CommandHandler.cs` (sends the inline keyboard).
13. Add `/order`, `/order_set` to the `/start` help text.
14. Run `dotnet format`.
