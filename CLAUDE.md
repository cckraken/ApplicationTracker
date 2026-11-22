# ApplicationTracker

A personal, single-user tracker for job applications. It runs locally (Blazor Server at `localhost`) and stores everything in a SQLite file on this machine. No accounts, no cloud sync, no multi-tenancy: keep it simple.

Feature priorities and the main daily jobs: `docs/product-brief.md`. Read it before planning a feature.

## Stack

- .NET 10 / C#, nullable reference types and implicit usings on.
- EF Core with SQLite.
- Blazor Web App, Interactive Server render mode, run locally. Keep logic in `Core` so the app could be hosted later (that would need auth, HTTPS and backups) without a rewrite.
- xUnit tests.

## Layout and ownership

- `src/ApplicationTracker.Core`: domain, `TrackerDbContext`, migrations, business rules and services. No UI references.
- `src/ApplicationTracker.App`: Blazor UI only. Pages and components call `Core` services; they never inject `TrackerDbContext` or contain business rules.
- `tests/ApplicationTracker.Tests`: xUnit, organised by area like `Core`.

One owner per job (use these; don't re-implement them):

- Status changes: only `JobApplication.ChangeStatus`, which writes the `StatusChange` history.
- Board and list moves: `ApplicationService` decides the target status and returns a `MoveResult`; the UI never works out a status itself.
- Company find-or-create and unused-company cleanup: `Core/Applications/CompanyLookup.cs`.
- Notes, contacts and reminders: `NoteService`, `ContactService` and `ReminderService`. A reminder's done state changes only through `Reminder.Complete`/`Reopen`; overdue and due-today for reminders come from `Reminder.IsOverdueOn`/`IsDueTodayOn`.
- Snooze dates (follow-ups and reminders): `SnoozePeriod.DueFrom` in `Core/Applications/ApplicationModels.cs`.
- Stats dashboard figures and week buckets: `StatsService` (`GetDashboardAsync`; Monday weeks via `StatsService.WeekStart`). The definitions are on the records in `Core/Stats/StatsModels.cs`; the UI shows them and never recomputes them.
- Before adding logic anywhere, search `Core` for a method that already does it.

Area-specific rules live in `.claude/rules/` and load when you open files in that area (Blazor pages, `Core` data, tests).

## Conventions

- Async all the way for I/O; pass `CancellationToken` where available.
- Store timestamps in UTC; convert to local time only in the UI.
- Get "now" and "today" from the injected `TimeProvider` (the local date is `TimeProvider.GetLocalToday()` from `Core/TimeProviderExtensions.cs`). Never `DateTime.Now`, `DateTime.UtcNow` or `DateTime.Today` in logic.
- New logic comes with tests. Run `dotnet test` before saying a change is done.
- Code style follows `dotnet format`; a hook runs it on every `.cs` file Claude edits.
- Known gaps and follow-ups are GitHub issues (`gh issue list`). Fix one only when asked or when the change touches it.

## Git and GitHub

- Never commit directly to `main`. Commit only when asked; keep commits small and focused.
- Branches: `<type>/<issue-number>-<short-description>` (`feat/42-csv-export`), or without the number when there's no issue (`feat/board`). Types: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`.
- Commits and PR titles: Conventional Commits, `<type>(<scope>): <subject>`, subject under 50 characters, imperative, no trailing period. Footer `Refs #42` when there's an issue.
- PRs (with `gh`, when asked): run `dotnet build` and `dotnet test` first. Description: **Summary** (1–3 bullets: what and why), **Testing** (how it was verified), `Closes #<n>` if there's an issue.

## Commands

Run from the repo root. The app applies pending migrations on startup and creates its database file if missing.

```powershell
dotnet build
dotnet test
dotnet run --project src/ApplicationTracker.App   # Development: data/tracker.dev.db with demo data
dotnet ef migrations add <Name> --project src/ApplicationTracker.Core --startup-project src/ApplicationTracker.App
dotnet ef database update --project src/ApplicationTracker.Core --startup-project src/ApplicationTracker.App
```

`run.ps1` starts the app for real use against the real database. Never run it, and never pass `--Tracker:DatabaseFile tracker.db` to any command.

## Data rules

- `data/tracker.db` is the **real** database with personal job-search data: never read, query, copy or commit it. Only `run.ps1` uses it.
- `data/tracker.dev.db` is the Development database, seeded with fictional demo data when empty. Develop and test only against Development mode, seed data and test fixtures.
- Never put secrets (API keys, tokens, passwords, connection strings with credentials) in code, `appsettings*.json` or commits. Locally they go in user-secrets (`dotnet user-secrets`).
- `.claude/settings.json` denies file access to `data/`, `*.db`, `*.sqlite*` and secrets; that doesn't cover programs you run, so the rules above still apply to commands.

## Subagents

In `.claude/agents/`; each one's `description` says when to use it. They load this file and `.claude/rules/` by default, unless their frontmatter sets `omitClaudeMd: true`.