using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Core.Notes;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Notes;

public sealed class NoteServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly ApplicationService _applications;
    private readonly NoteService _notes;

    public NoteServiceTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _applications = new ApplicationService(_db, _time);
        _notes = new NoteService(_db, _time);
    }

    public void Dispose() => _db.Dispose();

    private Task<int> CreateApplication() =>
        _applications.CreateAsync(new ApplicationInput { Company = "Contoso", RoleTitle = "Analyst" });

    private async Task<IReadOnlyList<NoteView>> NotesOf(int applicationId) =>
        (await _applications.GetDetailsAsync(applicationId))!.Notes;

    [Fact]
    public async Task Add_trims_the_body_keeps_inner_line_breaks_and_stamps_utc_now()
    {
        var id = await CreateApplication();

        var note = await _notes.AddAsync(id, "  First line\nSecond line \n ");

        Assert.NotNull(note);
        Assert.Equal("First line\nSecond line", note.Body);
        Assert.Equal(Start.UtcDateTime, note.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, note.CreatedAt.Kind);
        Assert.Null(note.UpdatedAt);
        Assert.Equal([note], await NotesOf(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public async Task Add_rejects_an_empty_body(string body)
    {
        var id = await CreateApplication();

        await Assert.ThrowsAsync<ValidationException>(() => _notes.AddAsync(id, body));
        Assert.Empty(await NotesOf(id));
    }

    [Fact]
    public async Task Add_accepts_the_maximum_length_and_rejects_one_more()
    {
        var id = await CreateApplication();

        Assert.NotNull(await _notes.AddAsync(id, new string('a', Note.MaxBodyLength)));
        await Assert.ThrowsAsync<ValidationException>(() => _notes.AddAsync(id, new string('a', Note.MaxBodyLength + 1)));
    }

    [Fact]
    public async Task Add_to_a_missing_application_returns_null()
    {
        Assert.Null(await _notes.AddAsync(999, "Hello"));
    }

    [Fact]
    public async Task Notes_are_listed_newest_first()
    {
        var id = await CreateApplication();
        await _notes.AddAsync(id, "Older");
        _time.Advance(TimeSpan.FromMinutes(5));
        await _notes.AddAsync(id, "Newer");

        Assert.Equal(["Newer", "Older"], (await NotesOf(id)).Select(n => n.Body));
    }

    [Fact]
    public async Task Notes_added_at_the_same_moment_keep_the_latest_first()
    {
        var id = await CreateApplication();
        await _notes.AddAsync(id, "First");
        await _notes.AddAsync(id, "Second");

        Assert.Equal(["Second", "First"], (await NotesOf(id)).Select(n => n.Body));
    }

    [Fact]
    public async Task Update_changes_the_body_and_marks_it_edited()
    {
        var id = await CreateApplication();
        var note = await _notes.AddAsync(id, "Draft");
        _time.Advance(TimeSpan.FromHours(1));

        var updated = await _notes.UpdateAsync(note!.Id, " Final ");

        Assert.Equal(new NoteView(note.Id, "Final", Start.UtcDateTime, Start.UtcDateTime.AddHours(1)), updated);
        Assert.Equal([updated!], await NotesOf(id));
    }

    [Fact]
    public async Task Update_with_the_same_text_does_not_mark_it_edited()
    {
        var id = await CreateApplication();
        var note = await _notes.AddAsync(id, "Same");
        _time.Advance(TimeSpan.FromHours(1));

        var updated = await _notes.UpdateAsync(note!.Id, "Same  ");

        Assert.Null(updated!.UpdatedAt);
    }

    [Fact]
    public async Task Update_rejects_an_empty_body_and_keeps_the_old_one()
    {
        var id = await CreateApplication();
        var note = await _notes.AddAsync(id, "Keep me");

        await Assert.ThrowsAsync<ValidationException>(() => _notes.UpdateAsync(note!.Id, " "));
        Assert.Equal("Keep me", Assert.Single(await NotesOf(id)).Body);
    }

    [Fact]
    public async Task Update_or_delete_a_missing_note_reports_it()
    {
        Assert.Null(await _notes.UpdateAsync(999, "Hello"));
        Assert.False(await _notes.DeleteAsync(999));
    }

    [Fact]
    public async Task Delete_removes_only_that_note()
    {
        var id = await CreateApplication();
        var keep = await _notes.AddAsync(id, "Keep");
        var remove = await _notes.AddAsync(id, "Remove");

        Assert.True(await _notes.DeleteAsync(remove!.Id));

        Assert.Equal([keep!], await NotesOf(id));
    }

    [Fact]
    public async Task Deleting_the_application_deletes_its_notes()
    {
        var id = await CreateApplication();
        var note = await _notes.AddAsync(id, "Gone with it");

        await _applications.DeleteAsync(id);

        Assert.Null(await _notes.UpdateAsync(note!.Id, "Still here?"));
    }

    [Fact]
    public async Task UpdateAsync_ReadBackFromTheDatabase_UpdatedAtIsUtc()
    {
        var id = await CreateApplication();
        var note = await _notes.AddAsync(id, "Draft");
        _time.Advance(TimeSpan.FromHours(2));
        await _notes.UpdateAsync(note!.Id, "Final");

        var read = Assert.Single(await NotesOf(id));

        Assert.Equal(Start.UtcDateTime.AddHours(2), read.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, read.UpdatedAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, read.CreatedAt.Kind);
    }

    [Fact]
    public async Task UpdateAsync_EditedAgain_MovesUpdatedAtForwardAndKeepsCreatedAt()
    {
        var id = await CreateApplication();
        var note = await _notes.AddAsync(id, "One");
        _time.Advance(TimeSpan.FromHours(1));
        await _notes.UpdateAsync(note!.Id, "Two");
        _time.Advance(TimeSpan.FromHours(1));

        var updated = await _notes.UpdateAsync(note.Id, "Three");

        Assert.Equal(new NoteView(note.Id, "Three", Start.UtcDateTime, Start.UtcDateTime.AddHours(2)), updated);
    }

    [Fact]
    public async Task UpdateAsync_SameTextAfterAnEdit_KeepsTheFirstEditTime()
    {
        var id = await CreateApplication();
        var note = await _notes.AddAsync(id, "Draft");
        _time.Advance(TimeSpan.FromHours(1));
        await _notes.UpdateAsync(note!.Id, "Final");
        _time.Advance(TimeSpan.FromHours(1));

        await _notes.UpdateAsync(note.Id, "  Final\n");

        Assert.Equal(Start.UtcDateTime.AddHours(1), Assert.Single(await NotesOf(id)).UpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_TooLong_ThrowsAndKeepsTheOldBody()
    {
        var id = await CreateApplication();
        var note = await _notes.AddAsync(id, "Keep me");

        await Assert.ThrowsAsync<ValidationException>(() =>
            _notes.UpdateAsync(note!.Id, new string('a', Note.MaxBodyLength + 1)));

        var read = Assert.Single(await NotesOf(id));
        Assert.Equal("Keep me", read.Body);
        Assert.Null(read.UpdatedAt);
    }

    [Fact]
    public async Task AddAsync_MaximumLengthPlusSurroundingWhitespace_IsAcceptedBecauseLengthIsCheckedAfterTrimming()
    {
        var id = await CreateApplication();
        var body = new string('a', Note.MaxBodyLength);

        var note = await _notes.AddAsync(id, "  \n" + body + "\n  ");

        Assert.Equal(body, note!.Body);
    }

    [Fact]
    public async Task GetDetailsAsync_EditedOlderNote_KeepsItsPlaceByCreationTime()
    {
        var id = await CreateApplication();
        var older = await _notes.AddAsync(id, "Older");
        _time.Advance(TimeSpan.FromMinutes(5));
        await _notes.AddAsync(id, "Newer");
        _time.Advance(TimeSpan.FromMinutes(5));

        await _notes.UpdateAsync(older!.Id, "Older, edited");

        Assert.Equal(["Newer", "Older, edited"], (await NotesOf(id)).Select(n => n.Body));
    }

    [Fact]
    public async Task GetDetailsAsync_NotesOfAnotherApplication_AreNotIncluded()
    {
        var first = await CreateApplication();
        var second = await CreateApplication();
        await _notes.AddAsync(first, "Mine");
        await _notes.AddAsync(second, "Theirs");

        Assert.Equal(["Mine"], (await NotesOf(first)).Select(n => n.Body));
        Assert.Equal(["Theirs"], (await NotesOf(second)).Select(n => n.Body));
    }

    [Fact]
    public async Task AddAsync_LocalZoneIsNotUtc_StampsTheUtcInstant()
    {
        // 22:00 on 20 November in New York is 03:00 UTC on the 21st.
        var instant = new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(instant);
        time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var id = await CreateApplication();

        var note = await new NoteService(_db, time).AddAsync(id, "Late call");

        Assert.Equal(instant.UtcDateTime, note!.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, note.CreatedAt.Kind);
        Assert.Equal(instant.UtcDateTime, Assert.Single(await NotesOf(id)).CreatedAt);
    }
}
