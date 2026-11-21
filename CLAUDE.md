# ApplicationTracker

A personal, single-user, interactive tracker for job applications. It runs locally and stores everything in a SQLite file on this machine. There are no accounts, no cloud sync and no multi-tenancy, so keep it simple.

## Product brief

Core features, in priority order:

1. **Status pipeline**: each application moves through
   `Wishlist → Applied → Screening → Interview → Offer`, or ends as `Rejected` / `Withdrawn`.
   Show it as a board (columns per status) and as a sortable, filterable list. Every status change is recorded with a timestamp.
2. **Reminders / follow-ups**: a follow-up date per application (and ad-hoc reminders). Highlight overdue and due-today items prominently.
3. **Contacts & notes**: recruiters, hiring managers and interviewers linked to an application or company, plus free-text, timestamped notes per application.
4. **Stats dashboard**: counts by status, response rate (applications that got past `Applied`), applications per week, and average time in each stage.

Nice-to-have later: import a job posting from a URL or pasted text, CSV export, attach CV/cover-letter versions used.

## Stack

- **.NET 10 / C#**, nullable reference types enabled, implicit usings on.
- **SQLite** via **EF Core** (`Microsoft.EntityFrameworkCore.Sqlite`).
- **Tests**: xUnit; data tests use SQLite in-memory (`DataSource=:memory:`), not the EF InMemory provider.
- **UI host**: **Blazor Web App, Interactive Server render mode, run locally** on the user's PC and used in the browser at `localhost`. Decided 2026-11-21. Keep logic in `Core` so the app can later be hosted online (that would need auth, HTTPS and backups) without a rewrite.

### Solution layout

- `ApplicationTracker.slnx`
- `src/ApplicationTracker.Core`: domain entities, `TrackerDbContext`, EF migrations, business rules. No UI references.
- `src/ApplicationTracker.App`: Blazor Web App (UI only); references Core.
- `tests/ApplicationTracker.Tests`: xUnit; references Core.

## Domain model (sketch)

- `JobApplication`: Id, Company, RoleTitle, PostingUrl, Location, SalaryRange, Source, AppliedOn, CurrentStatus, FollowUpOn, CreatedAt, UpdatedAt. Change status only via `ChangeStatus(newStatus, utcNow)`, which writes the `StatusChange` history.
- `Company`: Id, Name, Website, Notes
- `Contact`: Id, Name, Role, Email, Phone, LinkedInUrl, CompanyId, linked to applications
- `Note`: Id, ApplicationId, Body, CreatedAt
- `StatusChange`: Id, ApplicationId, FromStatus, ToStatus, ChangedAt (the pipeline history; stats are computed from it)
- `Reminder`: Id, ApplicationId?, DueOn, Text, IsDone

`ApplicationStatus` is an enum stored as a string.

## Conventions

- Async all the way for I/O (`ToListAsync`, `SaveChangesAsync`); pass `CancellationToken` where available.
- Every schema change gets an EF migration; never edit an applied migration.
- Store timestamps in UTC; convert to local time only in the UI.
- Keep business rules (status transitions, overdue logic, stats) in `Core`, not in UI components, so they can be unit tested. UI pages talk to Core services (e.g. `ApplicationService`), never to `TrackerDbContext` directly.
- Get "now" and "today" from the injected `TimeProvider` (`ApplicationService.Today` is the local date); tests use `FakeTimeProvider`.
- Code style follows `dotnet format`. A hook runs it on every `.cs` file Claude edits.
- New logic comes with tests. Run `dotnet test` before saying a change is done.
- Small, focused commits with descriptive messages. Commit only when asked.

## Commands

Run from the repo root. The app applies pending migrations on startup and creates its database file if missing.

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/ApplicationTracker.App   # Development: data/tracker.dev.db with demo data
powershell -ExecutionPolicy Bypass -File .\run.ps1 # Real use: data/tracker.db, http://localhost:5180
dotnet ef migrations add <Name> --project src/ApplicationTracker.Core --startup-project src/ApplicationTracker.App
dotnet ef database update --project src/ApplicationTracker.Core --startup-project src/ApplicationTracker.App
```

## Data rules

- Databases live in the repo-root `data/` folder, which is gitignored:
  - `data/tracker.db` is the **real** database. Only `run.ps1` uses it, by passing `--Tracker:DatabaseFile tracker.db`; every other way of starting the app (including `dotnet ef`) gets the dev database.
  - `data/tracker.dev.db` is the default/Development database, seeded with fictional demo data by `DemoDataSeeder` when empty (Development only). Delete it to reseed.
- Always develop and test against Development mode, never the real database.
- The real database holds personal job-search data: **do not read, query, copy or commit it**. Settings deny Claude access to `data/`, `*.db` and `*.sqlite*` files.
- Use seed data / test fixtures for development and tests.
- Secrets and local overrides (`.env*`, `appsettings.*.local.json`, `secrets.json`) are also off-limits and gitignored.

## Subagents (`.claude/agents/`)

- **code-reviewer**: after a feature or before a commit; reviews the diff for bugs, EF/async pitfalls and convention drift. Read-only.
- **test-writer**: writes and runs xUnit tests for new or changed logic.
- **ui-ux-designer**: layout, accessibility and usability of the board, list, reminders and dashboard screens.
- **job-search-helper**: non-code help: turns a job posting (pasted text or URL) into tracker fields, drafts follow-up messages and suggests next steps. It drafts only and never sends anything.
- **agent-maintainer**: after a change that affects what agents need to know (a decision, a new helper/service/component, a new feature area, a convention or CLAUDE.md change), updates the files in `.claude/agents/` and reports any suggested new agents back to the main session. Edits agent files only; never creates agents or edits CLAUDE.md.
