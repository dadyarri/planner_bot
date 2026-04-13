# Roadmap

## Multi-DM & Multi-Campaign Support

### Concept

Support multiple D&D campaigns within the same Telegram group chat. Each campaign has its own dungeon master (DM), dedicated thread, and independent scheduling — but shares a common availability pool so campaigns don't collide.

### Data Model Changes

#### New Entity: `Campaign`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | int, PK | |
| `DungeonMasterId` | long, FK → User | The DM who created and runs this campaign |
| `ForumThreadId` | int, FK → ForumThread | Link to the tracked forum thread (thread name = campaign name) |
| `IsActive` | bool | Soft-delete flag |
| `CreatedAt` | DateTime | |

Campaign name and chat ID are resolved via the linked `ForumThread` — no duplication needed.

#### New Entity: `CampaignMember`

| Column | Type | Description |
|--------|------|-------------|
| `CampaignId` | int, FK → Campaign | |
| `UserId` | long, FK → User | |
| `JoinedAt` | DateTime | |

Unique constraint on `(CampaignId, UserId)`. The global `IsActive` flag on `User` is preserved — it allows a user to temporarily pause participation across all campaigns (via `/pause` and `/unpause`). `CampaignMember` tracks which campaigns a user belongs to, while `IsActive` controls whether they are currently participating.

#### New Entity: `ServiceThread`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | int, PK | |
| `ForumThreadId` | int, FK → ForumThread | Reference to the tracked forum thread |

#### Modified Entities

- **SavedGame** — add `CampaignId` (FK → Campaign, **not null**) for per-campaign scoping.
- **VoteSession** — add `CampaignId` (FK → Campaign, **not null**) for per-campaign scoping.

### Pre-Implementation Refactoring

Before implementing multi-campaign support, a refactoring pass is needed to make the codebase more maintainable:

- **Extract `SlotCalculator` service** — a dedicated service that computes per-campaign availability from the shared `Response` table, scoped to each campaign's member list.
- **Clean up callback handler routing** in `UpdateHandler` — standardize how callback data is parsed and dispatched.
- **Standardize campaign-scoping pattern** across commands — a reusable helper that resolves the current campaign from thread context or shows an inline keyboard selector in service threads.

### Key Design Decisions

#### 1. Shared Availability Across Campaigns

Availability responses (yes/no/probably for a date) are **shared** — not per-campaign. When a player says "I'm available Tuesday", that applies across all campaigns they belong to. This avoids asking them to fill the same calendar multiple times.

- `Response` keeps its current structure (user + date + availability) with no campaign reference
- `CheckIfDateIsAvailable` is scoped to campaign members (not all active users), but reads from the shared `Response` table

#### 2. Per-Campaign `/plan` Output

`/plan` no longer auto-creates voting sessions. Instead, after all active users fill in availability:

1. `/plan` computes available time slots **per campaign** (each campaign may have different member sets)
2. Output groups results by campaign, e.g.:
   - "Кампания «Throne of Darkness»: все игроки свободны в субботу 18:00, воскресенье 15:00"
   - "Кампания «Lost Mines»: все игроки свободны в субботу 18:00"
3. The computed slots are cached in the `AvailableSlot` table for quick access by DMs

#### New Entity: `AvailableSlot`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | int, PK | |
| `CampaignId` | int, FK → Campaign | Which campaign this slot is for |
| `DateTime` | DateTime | Available time slot (UTC) |
| `ComputedAt` | DateTime | When this cache entry was computed |

The cache is refreshed whenever `/plan` runs or any player updates their availability.

#### 3. `/steal` Command — Quick Voting from Cached Slots

DMs use `/steal` to quickly start a voting session from cached available slots:

1. In a campaign thread: shows inline keyboard with cached available slots for that campaign
2. In a service thread: first shows campaign selector, then available slots for the selected campaign
3. DM picks a slot → bot immediately starts a voting session for that slot (no manual date/time entry)

This replaces the old auto-vote behavior of `/plan` with an explicit, campaign-scoped flow.

#### 4. Collision Checks Between Campaigns

Before creating a voting session, check `SavedGame` for existing games on the same date where any campaign member overlaps. Show a warning if a player already has a game with another campaign at that time.

#### 5. Thread Tracking via Forum Topic Service Messages

The Telegram Bot API does not provide a direct way to list or query threads in a chat. To work around this, the bot must track threads itself by listening to forum topic service messages on the `Message` update:

- `forum_topic_created` — a new thread was created. Store `ThreadId` and topic name.
- `forum_topic_edited` — a thread was renamed. Update `ForumThread.Name` (campaigns linked via FK see the change automatically).
- `forum_topic_closed` — a thread was closed. Mark it accordingly.
- `forum_topic_reopened` — a thread was reopened. Clear the closed flag.

This requires a new entity to store thread metadata separately from campaigns:

**New Entity: `ForumThread`**

| Column | Type | Description |
|--------|------|-------------|
| `Id` | int, PK | |
| `ChatId` | long | Telegram chat ID |
| `ThreadId` | int | Telegram message thread ID |
| `Name` | string | Thread name (from `forum_topic_created` / `forum_topic_edited`) |
| `IsClosed` | bool | Whether the thread is currently closed |

The `Campaign` table links to `ForumThread` via `ForumThreadId` FK — campaign name is always resolved from `ForumThread.Name`, no duplication needed. When a `forum_topic_edited` event fires, only the `ForumThread` record is updated, and all linked campaigns automatically see the new name.

The `UpdateHandler` must route these service messages to a handler that upserts `ForumThread` records.

#### 6. Thread Routing & Service Threads

- Each campaign is tied to one Telegram thread via `ForumThreadId` FK. Campaign name = `ForumThread.Name`.
- Use `/service_thread` to **toggle** the service thread status of the current thread. If the thread is not a service thread, it marks it as one; if it already is a service thread, it unmarks it. Service threads are used for general coordination (e.g., main chat or an admin thread).
- `/campaign_new` **refuses** to create a campaign in a service thread — the user must first unmark the thread via `/service_thread`.

Thread routing logic:

- Commands in a campaign's thread auto-scope to that campaign
- Commands in a service thread: show campaign selector inline keyboard (if applicable)

#### 7. Reminders

- Game reminders go to the campaign's specific thread via `messageThreadId`
- Reminder format: "The game is in <thread link> at <time>. Players: <player mentions>"
- Weekly planning reminders (`/weekly`) are **global** — not tied to a specific campaign

### Command Changes

#### New Commands

| Command | Description |
|---------|-------------|
| `/campaign_new` | Create campaign bound to current thread (sender = DM). Thread name = campaign name. **Refuses** if thread is marked as service — user must unmark it first via `/service_thread`. |
| `/campaign_join` | Join the campaign of the current thread, or select from inline keyboard if in service thread |
| `/campaign_leave` | Leave the campaign of the current thread, or select from inline keyboard if in service thread |
| `/campaign_delete` | Archive the campaign (DM only). Tied to current thread, or inline keyboard filtered to DM's campaigns if in service thread |
| `/service_thread` | **Toggle** service thread status for the current thread. If not service — marks it; if already service — unmarks it. |
| `/steal` | DM only. Show cached available slots for the campaign (inline keyboard). DM picks a slot → starts a voting session immediately. In service thread: shows campaign selector first. |

#### Modified Commands — Global (not campaign-scoped)

- `/plan`, `/yes`, `/no`, `/prob`, `/get` — remain global, not tied to a specific campaign. `/plan` shows per-campaign slot availability (no auto-vote) and caches results in `AvailableSlot` table.
- `/weekly` — global weekly planning reminders

#### Modified Commands — Campaign-scoped

- `/vote` — tied to specific campaign, **limited to DM** of that campaign only
- `/saved` — tied to specific campaign, **no internal IDs shown** in output
- `/unsave` — tied to specific campaign, **limited to DM** only. Uses inline keyboard to select which game slot to remove (instead of passing internal ID)

### Migration Strategy

- `CampaignId` is **not null** on `SavedGame`, `VoteSession` (not on `Response` — responses are global)
- Existing data is dropped in the migration — DMs must configure campaigns from scratch
- If dropping data is not possible in migration, create a temporary campaign named "Временная" and assign existing records to it
- Breaking changes are allowed — no backward compatibility needed
