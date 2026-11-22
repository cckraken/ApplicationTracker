using ApplicationTracker.Core;

namespace ApplicationTracker.Tests.Data;

public sealed class DataLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ApplicationTracker.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _localAppData;

    public DataLocationTests()
    {
        Directory.CreateDirectory(_root);
        _localAppData = Folder("localappdata");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Creates a folder under this test's temp root and returns its full path.</summary>
    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a folder holding the repository marker file and returns its full path.</summary>
    private string Repo(params string[] parts)
    {
        var path = Folder(parts);
        File.WriteAllText(Path.Combine(path, DataLocation.RepoMarkerFile), "");
        return path;
    }

    private string Resolve(string configured, string contentRoot) =>
        DataLocation.ResolveDirectory(configured, contentRoot, _localAppData);

    /// <summary>
    /// The no-marker cases rely on nothing above the temp folder holding the marker file.
    /// If something does on this machine, fail with a clear message rather than a misleading path mismatch.
    /// </summary>
    private void AssertNoMarkerAbove(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            Assert.False(
                File.Exists(Path.Combine(dir.FullName, DataLocation.RepoMarkerFile)),
                $"Test precondition: unexpected {DataLocation.RepoMarkerFile} in {dir.FullName}.");
        }
    }

    [Fact]
    public void An_absolute_path_is_returned_unchanged_without_a_marker()
    {
        var contentRoot = Folder("installed", "app");
        var absolute = Path.Combine(_root, "elsewhere", "data");

        Assert.Equal(Path.GetFullPath(absolute), Resolve(absolute, contentRoot));
    }

    [Fact]
    public void An_absolute_path_is_returned_unchanged_inside_a_repository()
    {
        var repo = Repo("repo");
        var contentRoot = Folder("repo", "src", "App");
        var absolute = Path.Combine(_root, "elsewhere", "data");

        var result = Resolve(absolute, contentRoot);

        Assert.Equal(Path.GetFullPath(absolute), result);
        Assert.False(result.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_absolute_path_with_dot_dot_segments_is_normalised()
    {
        var contentRoot = Folder("installed", "app");
        var absolute = Path.Combine(_root, "elsewhere", "..", "data");

        Assert.Equal(Path.Combine(_root, "data"), Resolve(absolute, contentRoot));
    }

    [Fact]
    public void A_relative_path_resolves_against_a_marker_in_the_content_root()
    {
        var repo = Repo("repo");

        Assert.Equal(Path.Combine(repo, "data"), Resolve("data", repo));
    }

    [Fact]
    public void A_relative_path_resolves_against_a_marker_in_a_parent_of_the_content_root()
    {
        var repo = Repo("repo");
        var contentRoot = Folder("repo", "src", "App", "bin", "Debug", "net10.0");

        Assert.Equal(Path.Combine(repo, "data"), Resolve("data", contentRoot));
    }

    [Theory]
    [InlineData("data/sub", new[] { "data", "sub" })]
    [InlineData("data/x/../sub", new[] { "data", "sub" })]
    [InlineData("./data", new[] { "data" })]
    public void A_nested_or_dotted_relative_path_resolves_against_the_marker_folder(string configured, string[] expected)
    {
        var repo = Repo("repo");
        var contentRoot = Folder("repo", "src", "App");

        Assert.Equal(Path.Combine([repo, .. expected]), Resolve(configured, contentRoot));
    }

    [Fact]
    public void A_relative_path_with_a_leading_dot_dot_is_normalised_above_the_marker_folder()
    {
        Repo("repo");
        var contentRoot = Folder("repo", "src", "App");

        Assert.Equal(Path.Combine(_root, "shared-data"), Resolve("../shared-data", contentRoot));
    }

    [Fact]
    public void The_nearest_marker_wins_when_there_are_two()
    {
        Repo("outer");
        var inner = Repo("outer", "inner");
        var contentRoot = Folder("outer", "inner", "src", "App");

        Assert.Equal(Path.Combine(inner, "data"), Resolve("data", contentRoot));
    }

    [Fact]
    public void A_marker_in_a_sibling_folder_is_not_used()
    {
        Repo("other-repo");
        var contentRoot = Folder("installed", "app");
        AssertNoMarkerAbove(contentRoot);

        Assert.Equal(Path.Combine(_localAppData, DataLocation.InstalledFolderName, "data"), Resolve("data", contentRoot));
    }

    [Fact]
    public void Without_a_marker_a_relative_path_resolves_under_local_app_data()
    {
        var contentRoot = Folder("installed", "app");
        AssertNoMarkerAbove(contentRoot);

        Assert.Equal(Path.Combine(_localAppData, "ApplicationTracker", "data"), Resolve("data", contentRoot));
    }

    [Fact]
    public void Without_a_marker_a_nested_relative_path_resolves_under_local_app_data()
    {
        var contentRoot = Folder("installed", "app");
        AssertNoMarkerAbove(contentRoot);

        Assert.Equal(
            Path.Combine(_localAppData, "ApplicationTracker", "data", "sub"),
            Resolve("data/x/../sub", contentRoot));
    }

    [Fact]
    public void The_result_is_a_full_path()
    {
        var contentRoot = Folder("installed", "app");
        AssertNoMarkerAbove(contentRoot);

        var result = Resolve("data", contentRoot);

        Assert.True(Path.IsPathFullyQualified(result));
        Assert.Equal(Path.GetFullPath(result), result);
    }
}
