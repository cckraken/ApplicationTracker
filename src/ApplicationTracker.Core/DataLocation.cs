namespace ApplicationTracker.Core;

/// <summary>Where the app keeps its database and uploaded documents.</summary>
public static class DataLocation
{
    /// <summary>The file that marks the repository root.</summary>
    public const string RepoMarkerFile = "ApplicationTracker.slnx";

    /// <summary>The folder under the user's local app data that an installed copy uses.</summary>
    public const string InstalledFolderName = "ApplicationTracker";

    /// <summary>
    /// Resolves the configured data directory to a full path.
    /// <list type="bullet">
    /// <item>An absolute path is used as it is, only normalised (<c>C:\x\..\data</c> becomes <c>C:\data</c>).
    /// Windows root- or drive-relative paths such as <c>\data</c> count as absolute here and resolve against
    /// the current drive, so configure a full path or a plain relative one.</item>
    /// <item>Running from the repository (dotnet run, run.ps1, the exe in bin/): a relative path is resolved
    /// against the nearest folder at or above <paramref name="contentRoot"/> holding <see cref="RepoMarkerFile"/>,
    /// so it is the same however the app starts.</item>
    /// <item>An installed copy (no repository above it): a relative path is resolved under
    /// <c>&lt;localAppData&gt;/ApplicationTracker</c>, so unzipping a newer version keeps the data.</item>
    /// </list>
    /// </summary>
    /// <param name="configured">The <c>Tracker:DataDirectory</c> setting, e.g. <c>data</c>.</param>
    /// <param name="contentRoot">The app's content root (where it runs from).</param>
    /// <param name="localAppData">The user's local application data folder (<c>%LOCALAPPDATA%</c> on Windows).</param>
    public static string ResolveDirectory(string configured, string contentRoot, string localAppData)
    {
        if (Path.IsPathRooted(configured))
        {
            return Path.GetFullPath(configured);
        }

        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, RepoMarkerFile)))
            {
                return Path.GetFullPath(Path.Combine(dir.FullName, configured));
            }
        }

        return Path.GetFullPath(Path.Combine(localAppData, InstalledFolderName, configured));
    }
}
