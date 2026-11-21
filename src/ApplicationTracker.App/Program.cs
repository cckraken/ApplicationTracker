using ApplicationTracker.App.Components;
using ApplicationTracker.Core.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SQLite database lives in the repo-root data/ folder by default (gitignored).
// "Tracker:DataDirectory" is resolved relative to the app's content root.
var dataDirectory = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["Tracker:DataDirectory"] ?? "data"));
var databasePath = Path.Combine(dataDirectory, "tracker.db");

// Blazor Server components are long-lived, so use a factory and a short-lived
// context per operation instead of one scoped context per circuit.
builder.Services.AddDbContextFactory<TrackerDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

// Create the data folder and apply any pending migrations before serving requests.
Directory.CreateDirectory(dataDirectory);
using (var db = app.Services.GetRequiredService<IDbContextFactory<TrackerDbContext>>().CreateDbContext())
{
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
