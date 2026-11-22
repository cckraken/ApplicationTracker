---
paths:
  - "src/ApplicationTracker.Core/**"
---

# Core and data rules

## Services and queries

- Each service method creates its own short-lived context from `IDbContextFactory<TrackerDbContext>`; use `AsNoTracking()` for reads.
- Case-insensitive matching uses `TrackerDbContext.UnicodeLower` (SQLite's `lower()` and `LIKE` only handle A–Z), compared against a value lowered with `ToLowerInvariant()`.
- Invalid input throws `ValidationException`; the UI turns it into a field error.

## Migrations

- Every model change gets a new migration (command in `CLAUDE.md`). Never edit a migration that has been applied anywhere.
- Read every generated migration before using it: any `Drop…` or `Rename…` needs a deliberate reason.
- A bad migration that hasn't been applied: `dotnet ef migrations remove` and regenerate it. Don't fix it forward by hand.
- Migrations run against the Development database only. Never point `dotnet ef` at `data/tracker.db`.
