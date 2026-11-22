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

- `JobApplication`: Id, Company, RoleTitle, PostingUrl, Location, SalaryRange, Source, AppliedOn, CurrentStatus, FollowUpOn, CreatedAt, UpdatedAt. Change status only via `ChangeStatus(newStatus, utcNow, localToday)`, which writes the `StatusChange` history (`localToday` fills `AppliedOn`, because the UTC and local dates can differ).
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

## Page patterns (Blazor Server)

`Components/Pages/ApplicationDetail.razor` is the reference implementation of these patterns (`ContactList.razor` follows it). Copy its approach rather than inventing a new one.

- Interactive Server is set globally on `Routes` in `App.razor`, so pages don't need their own `@rendermode`.
- Create a `DbContext` per operation through `IDbContextFactory` (the `Core` services already do); never hold one for the life of a component or circuit.
- `OnInitializedAsync` can run twice (prerendering, then the interactive render), so it should only load data: no side effects that break or repeat when it runs again.
- Catch, log and keep the current state in event handlers and load methods: an unhandled exception ends the circuit and the page stops responding.
- Serialize or version async work that quick repeated clicks or typing can overlap. Wrap the whole handler, including its reload or reset, so a late second click is ignored; a handler whose form has already closed must return without doing anything.
- Turn a `ValidationException` from a `Core` service into a field error on the form.
- Restore focus after a re-render by checking the data, not just an `@ref` dictionary: entries outlive the elements they pointed to.
- Render the page's one `<h1>` in every state (loading, not found, loaded), so `FocusOnNavigate` always finds it.
- Live-region announcements must be re-read even when the text repeats: use an `Announce` helper like `ApplicationDetail.razor`'s, not a direct assignment.

Gotchas:

- A `string` component parameter given a bare name is a literal: `Error="contactError"` passes the text "contactError"; write `Error="@contactError"`. Non-string parameters (bool, lists) are evaluated as C# without `@`.
- Razor attribute values can't contain double-quoted strings or tuple keys (`@ref="dict[(id, "x")]"` doesn't compile); use constants or separate dictionaries.
- Focus styles use `outline: 2px solid transparent`, never `outline: none`, so they still show in forced-colours mode.

## GitHub & Git Conventions

### Branching
- Never commit directly to `main`.
- Create feature branches from `main` using the format: `<type>/<issue-number>-<short-description>` (e.g., `feat/42-user-auth`, `fix/108-null-pointer`). Git does not allow `:` in branch names. When there is no issue, leave the number out (e.g., `feat/board`).
- Allowed types: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`.

### Commit Messages
- Follow Conventional Commits: `<type>(<scope>): <subject>`
- Keep the subject line under 50 characters, imperative mood, no trailing period (e.g., `feat(api): add rate limiting to login endpoint`).
- Reference the related GitHub issue in the commit footer if applicable (`Refs #42`).

### Pull Requests & GitHub CLI (`gh`)
- Use the `gh` CLI when asked to create issues or pull requests.
- PR titles must follow the same Conventional Commits format as commit messages.
- Before opening a PR, run all local linters and test suites to ensure checks pass.
- Populate PR descriptions with:
  - **Summary:** 1–3 bullet points explaining *what* changed and *why*. Only exceed this if the change is large and 1-3 points would exclude important information.
  - **Testing:** Steps taken to verify the changes.
  - **Issue Link:** Use `Closes #<issue-number>` to auto-close linked issues.

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