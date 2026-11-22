using System.ComponentModel.DataAnnotations;
using System.Text;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Documents;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Documents;

public sealed class DocumentServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ApplicationTracker.Tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemDocumentStore _store;
    private readonly ApplicationService _applications;
    private readonly DocumentService _documents;

    public DocumentServiceTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _store = new FileSystemDocumentStore(_folder);
        _applications = new ApplicationService(_db, _time);
        _documents = new DocumentService(_db, _store, _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\nfake pdf body");
    private static readonly byte[] DocxBytes = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00];

    private static DocumentInput Input(string title = "CV v1", DocumentKind? kind = DocumentKind.Cv) =>
        new() { Kind = kind, Title = title };

    private static MemoryStream Content(byte[] bytes) => new(bytes);

    private static MemoryStream Text(string text) => new(Encoding.UTF8.GetBytes(text));

    private Task<int> Upload(string title = "CV v1", DocumentKind kind = DocumentKind.Cv, string fileName = "cv.pdf") =>
        _documents.UploadAsync(Input(title, kind), Content(PdfBytes), fileName);

    private Task<int> CreateApplication(string company = "Contoso", string role = "Analyst") =>
        _applications.CreateAsync(new ApplicationInput { Company = company, RoleTitle = role });

    private async Task<Document> Row(int id)
    {
        using var db = _db.CreateDbContext();
        return await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id);
    }

    private async Task<int> DocumentRows()
    {
        using var db = _db.CreateDbContext();
        return await db.Documents.CountAsync();
    }

    private async Task<int> LinkRows()
    {
        using var db = _db.CreateDbContext();
        return await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM ApplicationDocuments").SingleAsync();
    }

    private string[] StoredFiles() =>
        Directory.Exists(_folder) ? Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)).ToArray() : [];

    private async Task AssertNothingSaved()
    {
        Assert.Equal(0, await DocumentRows());
        Assert.Empty(StoredFiles());
    }

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    // Upload: accepted files

    [Theory]
    [InlineData("cv.pdf", "application/pdf")]
    [InlineData("CV.PDF", "application/pdf")]
    [InlineData("letter.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("Letter.DocX", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public async Task Upload_accepts_pdf_and_docx_with_the_right_signature_in_any_case(string fileName, string contentType)
    {
        var bytes = fileName.EndsWith("pdf", StringComparison.OrdinalIgnoreCase) ? PdfBytes : DocxBytes;

        var id = await _documents.UploadAsync(Input(), Content(bytes), fileName);

        var row = await Row(id);
        Assert.Equal(contentType, row.ContentType);
        Assert.Equal(fileName, row.OriginalFileName);
        Assert.Equal(bytes.Length, row.SizeBytes);
    }

    [Theory]
    [InlineData("notes.txt", "text/plain; charset=utf-8")]
    [InlineData("NOTES.TXT", "text/plain; charset=utf-8")]
    [InlineData("cv.md", "text/markdown; charset=utf-8")]
    [InlineData("cv.MD", "text/markdown; charset=utf-8")]
    public async Task Upload_accepts_utf8_text_and_markdown_in_any_case(string fileName, string contentType)
    {
        var id = await _documents.UploadAsync(Input(), Text("# Café résumé – v3\n"), fileName);

        Assert.Equal(contentType, (await Row(id)).ContentType);
    }

    [Fact]
    public async Task Upload_records_clock_time_size_trimmed_title_and_a_guid_stored_name()
    {
        var id = await _documents.UploadAsync(Input("  CV – data roles v3  ", DocumentKind.CoverLetter), Content(PdfBytes), "My CV.PDF");

        var row = await Row(id);
        Assert.Equal("CV – data roles v3", row.Title);
        Assert.Equal(DocumentKind.CoverLetter, row.Kind);
        Assert.Equal(Start.UtcDateTime, row.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, row.CreatedAt.Kind);
        Assert.Equal(PdfBytes.Length, row.SizeBytes);
        Assert.Equal("My CV.PDF", row.OriginalFileName);

        Assert.EndsWith(".pdf", row.StoredFileName, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(row.StoredFileName[..^4], "N", out _));
        Assert.DoesNotContain("CV", row.StoredFileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([row.StoredFileName], StoredFiles());
    }

    [Theory]
    [InlineData(@"C:\Users\someone\Documents\cv.pdf")]
    [InlineData("folder/sub/cv.pdf")]
    [InlineData("../../cv.pdf")]
    [InlineData("  cv.pdf  ")]
    public async Task Upload_strips_a_path_from_the_file_name(string fileName)
    {
        var id = await _documents.UploadAsync(Input(), Content(PdfBytes), fileName);

        Assert.Equal("cv.pdf", (await Row(id)).OriginalFileName);
    }

    [Fact]
    public async Task Upload_accepts_a_file_of_exactly_the_size_limit()
    {
        var bytes = new byte[Document.MaxSizeBytes];
        PdfBytes.CopyTo(bytes, 0);

        var id = await _documents.UploadAsync(Input(), Content(bytes), "big.pdf");

        Assert.Equal(Document.MaxSizeBytes, (await Row(id)).SizeBytes);
    }

    [Fact]
    public async Task Upload_accepts_a_file_name_of_255_characters()
    {
        var name = new string('a', 251) + ".pdf";

        var id = await _documents.UploadAsync(Input(), Content(PdfBytes), name);

        Assert.Equal(name, (await Row(id)).OriginalFileName);
    }

    [Fact]
    public async Task Upload_accepts_a_title_of_the_maximum_length()
    {
        var title = new string('t', Document.MaxTitleLength);

        var id = await _documents.UploadAsync(Input(title), Content(PdfBytes), "cv.pdf");

        Assert.Equal(title, (await Row(id)).Title);
    }

    // Upload: rejected files and input

    [Theory]
    [InlineData("cv.doc")]
    [InlineData("cv.exe")]
    [InlineData("cv.pdf.exe")]
    [InlineData("cv")]
    [InlineData("cv.html")]
    public async Task Upload_rejects_other_extensions_and_saves_nothing(string fileName)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UploadAsync(Input(), Content(PdfBytes), fileName));

        await AssertNothingSaved();
    }

    public static TheoryData<string, byte[]> WrongBytes => new()
    {
        { "cv.pdf", Encoding.ASCII.GetBytes("%PDF") },
        { "cv.pdf", Encoding.ASCII.GetBytes("not a pdf at all") },
        { "cv.pdf", [0x50, 0x4B, 0x03, 0x04, 0x00] },
        { "cv.docx", Encoding.ASCII.GetBytes("%PDF-1.7") },
        { "cv.docx", [0x50, 0x4B, 0x05, 0x06] },
        { "cv.txt", [0x68, 0x69, 0x00, 0x21] },
        { "cv.md", [0x23, 0x20, 0xFF, 0xFE] },
        { "cv.txt", [0xC3, 0x28] },
    };

    [Theory]
    [MemberData(nameof(WrongBytes))]
    public async Task Upload_rejects_the_right_extension_with_the_wrong_bytes_and_saves_nothing(string fileName, byte[] bytes)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UploadAsync(Input(), Content(bytes), fileName));

        await AssertNothingSaved();
    }

    [Theory]
    [InlineData("cv.pdf")]
    [InlineData("cv.txt")]
    public async Task Upload_rejects_an_empty_file_and_saves_nothing(string fileName)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UploadAsync(Input(), Content([]), fileName));

        await AssertNothingSaved();
    }

    [Fact]
    public async Task Upload_rejects_a_file_over_the_size_limit_and_saves_nothing()
    {
        var bytes = new byte[Document.MaxSizeBytes + 1];
        PdfBytes.CopyTo(bytes, 0);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UploadAsync(Input(), Content(bytes), "big.pdf"));

        await AssertNothingSaved();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task Upload_rejects_an_empty_title_and_saves_nothing(string title)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UploadAsync(Input(title), Content(PdfBytes), "cv.pdf"));

        await AssertNothingSaved();
    }

    [Fact]
    public async Task Upload_rejects_a_title_over_the_maximum_length_and_saves_nothing()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UploadAsync(Input(new string('t', Document.MaxTitleLength + 1)), Content(PdfBytes), "cv.pdf"));

        await AssertNothingSaved();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(99)]
    public async Task Upload_rejects_a_missing_or_unknown_kind_and_saves_nothing(int? kind)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UploadAsync(Input(kind: (DocumentKind?)kind), Content(PdfBytes), "cv.pdf"));

        await AssertNothingSaved();
    }

    [Fact]
    public async Task Title_length_is_checked_after_trimming()
    {
        var title = new string('a', Document.MaxTitleLength);

        var id = await _documents.UploadAsync(Input($"  {title}  "), Content(PdfBytes), "cv.pdf");

        Assert.Equal(title, (await _documents.GetForEditAsync(id))!.Title);
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData("  .pdf")]
    [InlineData(@"C:\docs\.pdf")]
    public async Task Upload_rejects_a_bare_extension_as_the_file_name(string fileName)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UploadAsync(Input(), Content(PdfBytes), fileName));

        await AssertNothingSaved();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\Users\someone\")]
    [InlineData("folder/")]
    public async Task Upload_rejects_an_empty_file_name_and_saves_nothing(string fileName)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UploadAsync(Input(), Content(PdfBytes), fileName));

        await AssertNothingSaved();
    }

    [Fact]
    public async Task Upload_rejects_a_file_name_over_255_characters_and_saves_nothing()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UploadAsync(Input(), Content(PdfBytes), new string('a', 252) + ".pdf"));

        await AssertNothingSaved();
    }

    [Fact]
    public async Task Upload_deletes_the_file_when_the_database_save_fails()
    {
        using (var db = _db.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_document_insert BEFORE INSERT ON Documents BEGIN SELECT RAISE(ABORT, 'save failed'); END;");
        }
        var store = new RecordingStore(_store);
        var documents = new DocumentService(_db, store, _time);

        await Assert.ThrowsAsync<DbUpdateException>(() => documents.UploadAsync(Input(), Content(PdfBytes), "cv.pdf"));

        var saved = Assert.Single(store.Saved);
        Assert.Equal([saved], store.Deleted);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Upload_saves_no_row_when_the_store_fails()
    {
        var documents = new DocumentService(_db, new FailingStore(), _time);

        await Assert.ThrowsAsync<IOException>(() => documents.UploadAsync(Input(), Content(PdfBytes), "cv.pdf"));

        Assert.Equal(0, await DocumentRows());
    }

    [Fact]
    public async Task Two_uploads_of_the_same_file_get_different_stored_names()
    {
        var first = await Upload();
        var second = await Upload();

        Assert.NotEqual((await Row(first)).StoredFileName, (await Row(second)).StoredFileName);
        Assert.Equal(2, StoredFiles().Length);
    }

    // List and get

    [Fact]
    public async Task List_is_newest_first_and_ties_go_by_id()
    {
        var oldest = await Upload("Oldest");
        _time.Advance(TimeSpan.FromMinutes(5));
        var tieA = await Upload("Tie A");
        var tieB = await Upload("Tie B");

        Assert.Equal([tieB, tieA, oldest], (await _documents.ListAsync()).Select(d => d.Id));
    }

    [Fact]
    public async Task List_filters_by_kind()
    {
        var cv = await Upload("CV", DocumentKind.Cv);
        var letter = await Upload("Letter", DocumentKind.CoverLetter);
        await Upload("Other", DocumentKind.Other);

        Assert.Equal([cv], (await _documents.ListAsync(DocumentKind.Cv)).Select(d => d.Id));
        Assert.Equal([letter], (await _documents.ListAsync(DocumentKind.CoverLetter)).Select(d => d.Id));
        Assert.Equal(3, (await _documents.ListAsync()).Count);
    }

    [Fact]
    public async Task List_returns_the_summary_with_the_application_count()
    {
        var first = await CreateApplication(role: "First");
        var second = await CreateApplication(role: "Second");
        var linked = await Upload("Linked", fileName: "linked.pdf");
        var unlinked = await Upload("Unlinked");
        await _documents.LinkAsync(first, linked);
        await _documents.LinkAsync(second, linked);

        var list = await _documents.ListAsync();

        Assert.Equal(
            new DocumentSummary(linked, DocumentKind.Cv, "Linked", "linked.pdf", PdfBytes.Length, Start.UtcDateTime, 2),
            list.Single(d => d.Id == linked));
        Assert.Equal(0, list.Single(d => d.Id == unlinked).ApplicationCount);
    }

    [Fact]
    public async Task Get_for_application_lists_only_linked_documents_newest_first()
    {
        var appId = await CreateApplication(role: "Mine");
        var otherApp = await CreateApplication(role: "Other");
        var older = await Upload("Older");
        _time.Advance(TimeSpan.FromHours(1));
        var newer = await Upload("Newer");
        var elsewhere = await Upload("Elsewhere");
        await Upload("Unlinked");
        await _documents.LinkAsync(appId, older);
        await _documents.LinkAsync(appId, newer);
        await _documents.LinkAsync(otherApp, elsewhere);

        Assert.Equal([newer, older], (await _documents.GetForApplicationAsync(appId)).Select(d => d.Id));
    }

    [Fact]
    public async Task Get_for_a_missing_application_is_empty()
    {
        await Upload();

        Assert.Empty(await _documents.GetForApplicationAsync(999));
    }

    [Fact]
    public async Task Get_for_edit_returns_kind_and_title_or_null_when_missing()
    {
        var id = await Upload("CV v2", DocumentKind.Other);

        var form = await _documents.GetForEditAsync(id);

        Assert.Equal(DocumentKind.Other, form!.Kind);
        Assert.Equal("CV v2", form.Title);
        Assert.Null(await _documents.GetForEditAsync(999));
    }

    // Update

    [Fact]
    public async Task Update_changes_kind_and_trimmed_title_and_leaves_the_file()
    {
        var id = await Upload("CV v1");
        var before = await Row(id);

        Assert.Equal(DocumentUpdateResult.Saved, await _documents.UpdateAsync(id, Input("  Cover letter v1 ", DocumentKind.CoverLetter)));

        var after = await Row(id);
        Assert.Equal("Cover letter v1", after.Title);
        Assert.Equal(DocumentKind.CoverLetter, after.Kind);
        Assert.Equal(before.StoredFileName, after.StoredFileName);
        Assert.Equal(before.OriginalFileName, after.OriginalFileName);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal([before.StoredFileName], StoredFiles());
    }

    [Fact]
    public async Task Update_of_a_missing_document_returns_not_found()
    {
        Assert.Equal(DocumentUpdateResult.NotFound, await _documents.UpdateAsync(999, Input()));
    }

    [Fact]
    public async Task Update_from_the_edit_form_saves_when_nothing_changed_meanwhile()
    {
        var id = await Upload("CV v1");
        var form = (await _documents.GetForEditAsync(id))!;
        form.Title = "CV v2";

        Assert.Equal(DocumentUpdateResult.Saved, await _documents.UpdateAsync(id, form));
        Assert.Equal("CV v2", (await Row(id)).Title);
    }

    [Fact]
    public async Task Update_from_a_stale_form_saves_nothing_when_the_title_changed_meanwhile()
    {
        var id = await Upload("CV v1");
        var staleForm = (await _documents.GetForEditAsync(id))!;
        var otherTab = (await _documents.GetForEditAsync(id))!;
        otherTab.Title = "CV v3";
        await _documents.UpdateAsync(id, otherTab);

        staleForm.Title = "CV v2-final";
        staleForm.Kind = DocumentKind.Other;

        Assert.Equal(DocumentUpdateResult.ChangedElsewhere, await _documents.UpdateAsync(id, staleForm));
        var row = await Row(id);
        Assert.Equal("CV v3", row.Title);
        Assert.Equal(DocumentKind.Cv, row.Kind);
    }

    [Fact]
    public async Task Update_from_a_stale_form_saves_nothing_when_the_kind_changed_meanwhile()
    {
        var id = await Upload("CV v1");
        var staleForm = (await _documents.GetForEditAsync(id))!;
        var otherTab = (await _documents.GetForEditAsync(id))!;
        otherTab.Kind = DocumentKind.CoverLetter;
        await _documents.UpdateAsync(id, otherTab);

        staleForm.Title = "CV v2";

        Assert.Equal(DocumentUpdateResult.ChangedElsewhere, await _documents.UpdateAsync(id, staleForm));
        Assert.Equal("CV v1", (await Row(id)).Title);
    }

    [Fact]
    public async Task Delete_still_succeeds_when_the_file_is_locked()
    {
        var documents = new DocumentService(_db, new UndeletableStore(_store), _time);
        var id = await documents.UploadAsync(Input(), Content(PdfBytes), "cv.pdf");

        Assert.True(await documents.DeleteAsync(id));

        Assert.Empty(await documents.ListAsync());
    }

    [Fact]
    public async Task A_failed_clean_up_does_not_hide_the_original_upload_error()
    {
        // The database save fails (trigger) and the clean-up delete fails too (locked file).
        using (var db = _db.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_documents BEFORE INSERT ON Documents BEGIN SELECT RAISE(ABORT, 'nope'); END;");
        }
        var documents = new DocumentService(_db, new UndeletableStore(_store), _time);

        await Assert.ThrowsAsync<DbUpdateException>(() => documents.UploadAsync(Input(), Content(PdfBytes), "cv.pdf"));
    }

    [Fact]
    public async Task Text_that_is_not_utf8_gets_a_message_saying_how_to_fix_it()
    {
        var utf16 = Encoding.Unicode.GetBytes("My CV");

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UploadAsync(Input(), Content(utf16), "cv.txt"));

        Assert.Contains("UTF-8", ex.Message);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("Valid", null)]
    [InlineData("Valid", 99)]
    public async Task Update_rejects_invalid_input_and_changes_nothing(string title, int? kind)
    {
        var id = await Upload("CV v1");

        await Assert.ThrowsAsync<ValidationException>(() =>
            _documents.UpdateAsync(id, Input(title, (DocumentKind?)kind)));

        var row = await Row(id);
        Assert.Equal("CV v1", row.Title);
        Assert.Equal(DocumentKind.Cv, row.Kind);
    }

    // Delete

    [Fact]
    public async Task Delete_removes_the_row_its_links_and_its_file_but_keeps_the_applications()
    {
        var appId = await CreateApplication();
        var id = await Upload();
        var kept = await Upload("Kept");
        await _documents.LinkAsync(appId, id);
        await _documents.LinkAsync(appId, kept);
        var stored = (await Row(id)).StoredFileName;

        Assert.True(await _documents.DeleteAsync(id));

        Assert.Equal([kept], (await _documents.ListAsync()).Select(d => d.Id));
        Assert.Equal([kept], (await _documents.GetForApplicationAsync(appId)).Select(d => d.Id));
        Assert.Equal(1, await LinkRows());
        Assert.DoesNotContain(stored, StoredFiles());
        Assert.Single(StoredFiles());
        Assert.NotNull(await _applications.GetDetailsAsync(appId));
    }

    [Fact]
    public async Task Delete_of_a_missing_document_returns_false()
    {
        await Upload();

        Assert.False(await _documents.DeleteAsync(999));
        Assert.Single(StoredFiles());
    }

    [Fact]
    public async Task Delete_works_when_the_file_is_already_gone()
    {
        var id = await Upload();
        _store.Delete((await Row(id)).StoredFileName);

        Assert.True(await _documents.DeleteAsync(id));

        Assert.Equal(0, await DocumentRows());
    }

    // Link and unlink

    [Fact]
    public async Task Link_is_idempotent()
    {
        var appId = await CreateApplication();
        var id = await Upload();

        Assert.True(await _documents.LinkAsync(appId, id));
        Assert.True(await _documents.LinkAsync(appId, id));

        Assert.Single(await _documents.GetForApplicationAsync(appId));
        Assert.Equal(1, Assert.Single(await _documents.ListAsync()).ApplicationCount);
    }

    [Fact]
    public async Task Link_with_a_missing_application_or_document_returns_false_and_links_nothing()
    {
        var appId = await CreateApplication();
        var id = await Upload();

        Assert.False(await _documents.LinkAsync(999, id));
        Assert.False(await _documents.LinkAsync(appId, 999));

        Assert.Equal(0, await LinkRows());
    }

    [Fact]
    public async Task Unlink_removes_only_that_link_and_keeps_the_document()
    {
        var first = await CreateApplication(role: "First");
        var second = await CreateApplication(role: "Second");
        var id = await Upload();
        await _documents.LinkAsync(first, id);
        await _documents.LinkAsync(second, id);

        Assert.True(await _documents.UnlinkAsync(first, id));

        Assert.Empty(await _documents.GetForApplicationAsync(first));
        Assert.Single(await _documents.GetForApplicationAsync(second));
        Assert.Equal(1, Assert.Single(await _documents.ListAsync()).ApplicationCount);
        Assert.Single(StoredFiles());
    }

    [Fact]
    public async Task Unlink_is_idempotent()
    {
        var appId = await CreateApplication();
        var id = await Upload();
        await _documents.LinkAsync(appId, id);

        Assert.True(await _documents.UnlinkAsync(appId, id));
        Assert.True(await _documents.UnlinkAsync(appId, id));
        Assert.True(await _documents.UnlinkAsync(appId, 999)); // never linked, missing document

        Assert.Empty(await _documents.GetForApplicationAsync(appId));
    }

    [Fact]
    public async Task Unlink_with_a_missing_application_returns_false()
    {
        var id = await Upload();

        Assert.False(await _documents.UnlinkAsync(999, id));
    }

    [Fact]
    public async Task Deleting_an_application_keeps_the_document_and_removes_the_link()
    {
        var appId = await CreateApplication();
        var otherApp = await CreateApplication(role: "Other");
        var id = await Upload();
        await _documents.LinkAsync(appId, id);
        await _documents.LinkAsync(otherApp, id);

        Assert.True(await _applications.DeleteAsync(appId));

        var summary = Assert.Single(await _documents.ListAsync());
        Assert.Equal(id, summary.Id);
        Assert.Equal(1, summary.ApplicationCount);
        Assert.Equal(1, await LinkRows());
        Assert.Single(StoredFiles());
        Assert.Empty(await _documents.GetForApplicationAsync(appId));
    }

    // Open file

    [Theory]
    [InlineData("cv.pdf", "application/pdf")]
    [InlineData("Notes.TXT", "text/plain; charset=utf-8")]
    public async Task Open_file_returns_the_original_bytes_content_type_and_name(string fileName, string contentType)
    {
        var bytes = fileName.EndsWith(".pdf") ? PdfBytes : Encoding.UTF8.GetBytes("Plain text – ü");
        var id = await _documents.UploadAsync(Input(), Content(bytes), @"C:\somewhere\" + fileName);

        var file = await _documents.OpenFileAsync(id);

        Assert.NotNull(file);
        await using (file.Content)
        {
            Assert.Equal(bytes, await ReadAll(file.Content));
        }
        Assert.Equal(contentType, file.ContentType);
        Assert.Equal(fileName, file.FileName);
    }

    [Fact]
    public async Task Open_file_of_a_missing_document_returns_null()
    {
        Assert.Null(await _documents.OpenFileAsync(999));
    }

    [Fact]
    public async Task Open_file_returns_null_when_the_file_is_missing_on_disk()
    {
        var id = await Upload();
        _store.Delete((await Row(id)).StoredFileName);

        Assert.Null(await _documents.OpenFileAsync(id));
    }

    [Fact]
    public void Accepted_extensions_list_the_four_types()
    {
        Assert.Equal(".pdf,.docx,.txt,.md", DocumentService.AcceptedExtensions);
    }

    /// <summary>Passes through to a real store and records what was saved and deleted.</summary>
    private sealed class RecordingStore(IDocumentStore inner) : IDocumentStore
    {
        public List<string> Saved { get; } = [];
        public List<string> Deleted { get; } = [];

        public async Task SaveAsync(string storedFileName, Stream content, CancellationToken ct = default)
        {
            await inner.SaveAsync(storedFileName, content, ct);
            Saved.Add(storedFileName);
        }

        public Stream? OpenRead(string storedFileName) => inner.OpenRead(storedFileName);

        public void Delete(string storedFileName)
        {
            Deleted.Add(storedFileName);
            inner.Delete(storedFileName);
        }
    }

    /// <summary>A store whose disk is full.</summary>
    /// <summary>Saves and reads normally, but deleting fails as if the file were locked.</summary>
    private sealed class UndeletableStore(IDocumentStore inner) : IDocumentStore
    {
        public Task SaveAsync(string storedFileName, Stream content, CancellationToken ct = default) =>
            inner.SaveAsync(storedFileName, content, ct);

        public Stream? OpenRead(string storedFileName) => inner.OpenRead(storedFileName);

        public void Delete(string storedFileName) =>
            throw new IOException("The file is being used by another process.");
    }

    private sealed class FailingStore : IDocumentStore
    {
        public Task SaveAsync(string storedFileName, Stream content, CancellationToken ct = default) =>
            throw new IOException("Disk full.");

        public Stream? OpenRead(string storedFileName) => null;

        public void Delete(string storedFileName)
        {
        }
    }
}
