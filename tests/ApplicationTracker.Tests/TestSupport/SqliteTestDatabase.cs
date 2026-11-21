using ApplicationTracker.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ApplicationTracker.Tests.TestSupport;

/// <summary>
/// A migrated in-memory SQLite database shared by every context the factory creates,
/// for testing code that uses <see cref="IDbContextFactory{TContext}"/>.
/// </summary>
public sealed class SqliteTestDatabase : IDbContextFactory<TrackerDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TrackerDbContext> _options;

    public SqliteTestDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(_connection).Options;

        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public TrackerDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
