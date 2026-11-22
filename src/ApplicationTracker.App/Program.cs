using ApplicationTracker.App.Components;
using ApplicationTracker.Core;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Contacts;
using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Documents;
using ApplicationTracker.Core.Feedback;
using ApplicationTracker.Core.Notes;
using ApplicationTracker.Core.Reminders;
using ApplicationTracker.Core.Stats;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Serve the build's static web assets (scoped CSS, Bootstrap) in every environment,
// so run.ps1 can start the app in Production mode without publishing first.
builder.WebHost.UseStaticWebAssets();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The database lives in the repo-root data/ folder (gitignored) when running from the repo, or
// in %LOCALAPPDATA%\ApplicationTracker\data for an installed copy (see DataLocation). The default
// is the dev database, tracker.dev.db; only run.ps1 and the release (its start script and its
// generated appsettings.Production.json) opt in to the real one, tracker.db. Development also seeds demo data into an
// empty dev DB.
var tracker = builder.Configuration.GetSection("Tracker");
var dataDirectory = DataLocation.ResolveDirectory(
    tracker["DataDirectory"] ?? "data",
    builder.Environment.ContentRootPath,
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
var databasePath = Path.Combine(dataDirectory, tracker["DatabaseFile"] ?? "tracker.dev.db");
var seedDemoData = tracker.GetValue<bool>("SeedDemoData");

// Blazor Server components are long-lived, so use a factory and a short-lived
// context per operation instead of one scoped context per circuit.
builder.Services.AddDbContextFactory<TrackerDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ApplicationService>();
builder.Services.AddScoped<NoteService>();
builder.Services.AddScoped<ContactService>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<StatsService>();
// Uploaded CVs and cover letters sit next to the database, in the same never-committed folder.
// Each database gets its own folder (tracker.db -> tracker-documents, tracker.dev.db -> tracker.dev-documents),
// so development files never mix with real ones and each database's rows match its own files.
var documentsDirectory = Path.Combine(dataDirectory, Path.GetFileNameWithoutExtension(databasePath) + "-documents");
builder.Services.AddSingleton<IDocumentStore>(new FileSystemDocumentStore(documentsDirectory));
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<FeedbackPackService>();

var app = builder.Build();

// Create the data folder and apply any pending migrations before serving requests.
Directory.CreateDirectory(dataDirectory);
using (var db = app.Services.GetRequiredService<IDbContextFactory<TrackerDbContext>>().CreateDbContext())
{
    db.Database.Migrate();
    if (seedDemoData)
    {
        await DemoDataSeeder.SeedIfEmptyAsync(db, app.Services.GetRequiredService<TimeProvider>());
    }
}

app.Logger.LogInformation("Using database {DatabasePath}", databasePath);

// The release turns this on (Tracker:OpenBrowser) so a double-clicked start opens the app in the
// default browser once it is actually listening; a failed start (port busy) opens nothing.
if (tracker.GetValue<bool>("OpenBrowser"))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault();
        if (url is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Couldn't open the browser; go to {Url} yourself", url);
        }
    });
}

// Configure the HTTP request pipeline.
// The app only runs on localhost over HTTP, so there is no HTTPS redirect or HSTS.
// Hosting it online would need those back, plus authentication.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();

// Downloads a stored CV or cover letter under its original name. Like the rest of the app this
// is only reachable on localhost; hosting it would need authentication in front of it.
app.MapGet("/documents/{id:int}/file", async (int id, DocumentService documents, CancellationToken ct) =>
    await documents.OpenFileAsync(id, ct) is { } file
        ? Results.File(file.Content, file.ContentType, file.FileName)
        : Results.NotFound());

// A CV feedback pack: the ready-made prompt and the document in one zip, for use with Claude Code.
app.MapGet("/applications/{applicationId:int}/documents/{documentId:int}/feedback-pack",
    async (int applicationId, int documentId, FeedbackPackService packs, CancellationToken ct) =>
        await packs.CreateZipAsync(applicationId, documentId, ct) is { } zip
            ? Results.File(zip.Content, "application/zip", zip.FileName)
            : Results.NotFound());

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
