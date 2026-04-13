# Roadmap

## Multi-DM & Multi-Campaign Support

### Concept

Support multiple D&D campaigns within the same Telegram group chat. Each campaign has its own dungeon master (DM), dedicated thread, and independent scheduling — but shares a common availability pool so campaigns don't collide.

### Data Model Changes

#### New Entity: `Campaign`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | int, PK | |
| `Name` | string, max 100 | e.g. "Curse of Strahd", matches thread name |
| `DungeonMasterId` | long, FK → User | The DM who created and runs this campaign |
| `ChatId` | long | Telegram chat ID |
| `ThreadId` | int? | Dedicated Telegram thread for this campaign |
| `IsActive` | bool | Soft-delete flag |
| `CreatedAt` | DateTime | |

#### New Entity: `CampaignMember`

| Column | Type | Description |
|--------|------|-------------|
| `CampaignId` | int, FK → Campaign | |
| `UserId` | long, FK → User | |
| `JoinedAt` | DateTime | |

Unique constraint on `(CampaignId, UserId)`. Replaces global `IsActive` for per-campaign membership.

#### Modified Entities

- **Response**, **SavedGame**, **VoteSession** — add nullable `CampaignId` (FK → Campaign) for per-campaign scoping. Null = legacy/global mode.

### Key Design Decisions

#### 1. Shared Availability Across Campaigns

Availability responses (yes/no/probably for a date) are **shared** — not per-campaign. When a player says "I'm available Tuesday", that applies across all campaigns they belong to. This avoids asking them to fill the same calendar multiple times.

- `Response` keeps its current structure (user + date + availability)
- `CheckIfDateIsAvailable` is scoped to campaign members (not all active users), but reads from the shared `Response` table
- The `CampaignId` on `Response` tracks which campaign *triggered* the response, not isolating availability

#### 2. Collision Checks Between Campaigns

Before creating a voting session, check `SavedGame` for existing games on the same date where any campaign member overlaps. Show a warning if a player already has a game with another campaign at that time.

#### 3. Wiring Existing Threads to Campaigns

- `/campaign_new <name>` — If sent inside a thread, automatically binds `ThreadId` to that thread
- `/campaign_wire <campaign_id>` — Bind an existing campaign to the current thread (DM only)
- Thread name should ideally match campaign name (bot can suggest renaming)

#### 4. Campaign-Scoped Commands

Thread routing logic:

- Commands in a campaign's thread auto-scope to that campaign
- Commands in main chat: if user belongs to 1 campaign, auto-scope; if multiple, show campaign selector inline keyboard; if none, legacy global mode

#### 5. Reminders

- Game reminders go to the campaign's specific thread via `messageThreadId`
- Reminder message includes campaign name (= thread name) and DM display name separate from player list
- Weekly reminders are per-campaign, sent to each campaign's thread

### Command Changes

#### New Commands

| Command | Description |
|---------|-------------|
| `/campaign_new <name>` | Create campaign (sender = DM). Binds to current thread if in one. |
| `/campaign_join <id>` | Join a campaign as player |
| `/campaign_leave <id>` | Leave a campaign |
| `/campaign_list` | List all active campaigns in this chat |
| `/campaign_delete <id>` | Archive a campaign (DM only) |
| `/campaign_wire <id>` | Bind campaign to current thread (DM only) |

#### Modified Commands

- `/plan`, `/yes`, `/no`, `/prob`, `/get`, `/vote`, `/saved`, `/unsave` — auto-scope to campaign when in a campaign thread; show campaign selector otherwise
- `/weekly` — per-campaign weekly reminders sent to campaign's thread

### DM-Specific Features

- Only the DM can `/vote` for their campaign
- DM can `/campaign_delete` to archive
- DM gets a dedicated reminder if they haven't confirmed availability

### Migration Strategy

- All new FK columns are nullable for backward compatibility
- Null `CampaignId` = legacy single-campaign mode (everything works as before)
- Optional one-time migration: create a default "Main Campaign" and assign all existing data to it
- No breaking changes — existing bot behavior is preserved until campaigns are created
