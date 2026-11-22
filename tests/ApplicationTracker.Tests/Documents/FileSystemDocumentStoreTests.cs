using System.Text;
using ApplicationTracker.Core.Documents;

namespace ApplicationTracker.Tests.Documents;

public sealed class FileSystemDocumentStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ApplicationTracker.Tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemDocumentStore _store;

    public FileSystemDocumentStoreTests() => _store = new FileSystemDocumentStore(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Save_creates_the_folder_and_open_read_returns_the_same_bytes()
    {
        await _store.SaveAsync("abc.txt", Bytes("hello"));

        using var stream = _store.OpenRead("abc.txt");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task A_write_that_fails_partway_leaves_no_file()
    {
        await Assert.ThrowsAsync<IOException>(() => _store.SaveAsync("partial.pdf", new FailingAfterSomeBytes()));

        Assert.False(File.Exists(Path.Combine(_folder, "partial.pdf")));
    }

    [Fact]
    public async Task Saving_over_an_existing_name_fails_and_keeps_the_original()
    {
        await _store.SaveAsync("taken.txt", Bytes("first"));

        await Assert.ThrowsAsync<IOException>(() => _store.SaveAsync("taken.txt", Bytes("second")));

        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(_folder, "taken.txt")));
    }

    /// <summary>Gives some bytes, then fails, like a dropped upload.</summary>
    private sealed class FailingAfterSomeBytes : Stream
    {
        private bool gaveBytes;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (gaveBytes)
            {
                throw new IOException("Connection lost.");
            }

            gaveBytes = true;
            buffer[offset] = (byte)'%';
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Open_read_of_a_missing_file_returns_null()
    {
        Assert.Null(_store.OpenRead("missing.pdf"));
    }

    [Fact]
    public async Task Delete_removes_the_file_and_a_missing_file_is_fine()
    {
        await _store.SaveAsync("abc.txt", Bytes("hello"));

        _store.Delete("abc.txt");
        _store.Delete("abc.txt");
        _store.Delete("never-there.pdf");

        Assert.Null(_store.OpenRead("abc.txt"));
    }

    [Theory]
    [InlineData("../escape.pdf")]
    [InlineData("..\\escape.pdf")]
    [InlineData("sub/file.pdf")]
    [InlineData("sub\\file.pdf")]
    [InlineData("..")]
    [InlineData("a..b.pdf")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Names_with_path_separators_or_dot_dot_are_refused(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(name, Bytes("x")));
        Assert.Throws<ArgumentException>(() => _store.OpenRead(name));
        Assert.Throws<ArgumentException>(() => _store.Delete(name));
    }

    [Fact]
    public async Task A_refused_name_writes_nothing_outside_the_folder()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync("../escape.pdf", Bytes("x")));

        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_folder)!, "escape.pdf")));
    }
}
