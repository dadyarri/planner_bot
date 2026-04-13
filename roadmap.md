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
| `ChatId` | long | Telegram chat ID |
| `ThreadId` | int | Dedicated Telegram thread for this campaign (thread name = campaign name) |
| `IsActive` | bool | Soft-delete flag |
| `CreatedAt` | DateTime | |

Campaign name is derived from the thread name — no separate `Name` column needed.

#### New Entity: `CampaignMember`

| Column | Type | Description |
|--------|------|-------------|
| `CampaignId` | int, FK → Campaign | |
| `UserId` | long, FK → User | |
| `JoinedAt` | DateTime | |

Unique constraint on `(CampaignId, UserId)`. Replaces global `IsActive` for per-campaign membership.

#### New Entity: `ServiceThread`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | int, PK | |
| `ChatId` | long | Telegram chat ID |
| `ThreadId` | int | Thread marked as service (not tied to any campaign) |

#### Modified Entities

- **Response** — no changes. Responses are global and not tied to any campaign.
- **SavedGame** — add `CampaignId` (FK → Campaign, **not null**) for per-campaign scoping.
- **VoteSession** — add `CampaignId` (FK → Campaign, **not null**) for per-campaign scoping.

### Key Design Decisions

#### 1. Shared Availability Across Campaigns

Availability responses (yes/no/probably for a date) are **shared** — not per-campaign. When a player says "I'm available Tuesday", that applies across all campaigns they belong to. This avoids asking them to fill the same calendar multiple times.

- `Response` keeps its current structure (user + date + availability) with no campaign reference
- `CheckIfDateIsAvailable` is scoped to campaign members (not all active users), but reads from the shared `Response` table

#### 2. Collision Checks Between Campaigns

Before creating a voting session, check `SavedGame` for existing games on the same date where any campaign member overlaps. Show a warning if a player already has a game with another campaign at that time.

#### 3. Thread Routing & Service Threads

- Each campaign is tied to one Telegram thread. Campaign name = thread name.
- Use `/service_thread` to mark the current thread as a **service thread** — not related to any campaign. Service threads are used for general coordination (e.g., main chat or an admin thread).
- Refuse to create a campaign in a service thread.

Thread routing logic:

- Commands in a campaign's thread auto-scope to that campaign
- Commands in a service thread: show campaign selector inline keyboard (if applicable)

#### 4. Reminders

- Game reminders go to the campaign's specific thread via `messageThreadId`
- Reminder format: "The game is in <thread link> at <time>. Players: <player mentions>"
- Weekly planning reminders (`/weekly`) are **global** — not tied to a specific campaign

### Command Changes

#### New Commands

| Command | Description |
|---------|-------------|
| `/campaign_new` | Create campaign bound to current thread (sender = DM). Thread name = campaign name. Refuses to create in service threads. |
| `/campaign_join` | Join the campaign of the current thread, or select from inline keyboard if in service thread |
| `/campaign_leave` | Leave the campaign of the current thread, or select from inline keyboard if in service thread |
| `/campaign_delete` | Archive the campaign (DM only). Tied to current thread, or inline keyboard filtered to DM's campaigns if in service thread |
| `/service_thread` | Mark the current thread as a service thread (not tied to any campaign) |

#### Modified Commands — Global (not campaign-scoped)

- `/plan`, `/yes`, `/no`, `/prob`, `/get` — remain global, not tied to a specific campaign
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
