---
paths:
  - "tests/**"
---

# Test rules

- Each area has its own folder and test file (`Applications/`, `Contacts/`, `Notes/`, `Reminders/`, `Stats/`, `Documents/`, `Data/`, `Domain/`); the existing file for an area is the style reference.
- Test names are readable sentences in underscores, e.g. `Deleting_the_application_deletes_its_notes`. Use `[Fact]`, and `[Theory]` with `[InlineData]` for tables of cases.
- Data tests use `TestSupport/SqliteTestDatabase`: SQLite in-memory with the real migrations, passed to services as their `IDbContextFactory`. Never use the EF InMemory provider or `EnsureCreated()`.
- Time comes from `FakeTimeProvider` with fixed dates, never the real clock. It can't move backwards: for an earlier start, create a separate provider and service (see `ServiceIn` in `Applications/BoardTests.cs`).
- Cover date boundaries: due today, due yesterday, week edges, late evening / early morning where the UTC and local dates differ, and DST change days (`SetLocalTimeZone` with IANA ids such as `Europe/London`).
- Every action that checks the value the screen saw (`OriginalStatus`, an `expected` date) gets a stale-screen test: the stored value has changed, so nothing is saved, including `UpdatedAt` (see `Applications/FollowUpActionTests.cs`).
- Test the behaviour of the code documents, not rules you think should exist. Suggest a missing rule in your report instead of writing a failing test for it.
- Build test data with small helper methods. Never use the real database or its documents folder. Document-store tests use a unique folder under `Path.GetTempPath()` and delete it afterwards (see `Documents/FileSystemDocumentStoreTests.cs`).
