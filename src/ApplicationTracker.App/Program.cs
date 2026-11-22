using ApplicationTracker.App.Components;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Contacts;
using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Notes;
using ApplicationTracker.Core.Reminders;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Serve the build's static web assets (scoped CSS, Bootstrap) in every environment,
// so run.ps1 can start the app in Production mode without publishing first.
builder.WebHost.UseStaticWebAssets();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The database lives in the repo-root data/ folder (gitignored). The default is the dev
// database, tracker.dev.db; only run.ps1 opts in to the real one by passing
// --Tracker:DatabaseFile tracker.db. Development also seeds demo data into an empty dev DB.
var tracker = builder.Configuration.GetSection("Tracker");
var dataDirectory = ResolveDataDirectory(tracker["DataDirectory"] ?? "data", builder.Environment.ContentRootPath);
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// A relative data directory is resolved against the repo root (the folder holding
// ApplicationTracker.slnx), so it lands in the same place however the app is started
// (dotnet run, run.ps1, or the exe in bin/). Falls back to the content root.
static string ResolveDataDirectory(string configured, string contentRoot)
{
    if (Path.IsPathRooted(configured))
    {
        return configured;
    }

    for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ApplicationTracker.slnx")))
        {
            return Path.GetFullPath(Path.Combine(dir.FullName, configured));
        }
    }

    return Path.GetFullPath(Path.Combine(contentRoot, configured));
}
