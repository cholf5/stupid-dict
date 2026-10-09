using StupidDict.App;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Bundled-data lookup: exe-adjacent first (win/linux zip layout), then the
/// macOS .app bundle's Contents/Resources (the code seal keeps plain data
/// files out of MacOS/), then null so the caller falls through to the user
/// data directory. Tests drive the internal resolvers with a temp bundle-like
/// layout — AppContext.BaseDirectory cannot be faked in place, and no real
/// user data is touched.
/// </summary>
public class AppPathsTests : IDisposable
{
    private readonly string _root;
    private readonly string _baseDir;

    public AppPathsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "stupiddict-apppaths-" + Path.GetRandomFileName());
        _baseDir = Path.Combine(_root, "MacOS");
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MakeResources()
    {
        var resources = Path.Combine(_root, "Resources");
        Directory.CreateDirectory(resources);
        return resources;
    }

    // ---- file variant (dictionary.db) ----

    [Fact]
    public void File_ExeAdjacent_WinsOverResources()
    {
        File.WriteAllText(Path.Combine(_baseDir, "dictionary.db"), "exe");
        File.WriteAllText(Path.Combine(MakeResources(), "dictionary.db"), "res");

        Assert.Equal(
            Path.Combine(_baseDir, "dictionary.db"),
            AppPaths.ResolveBundledFile("dictionary.db", _baseDir));
    }

    [Fact]
    public void File_ResourcesFallback_WhenNothingAdjacent()
    {
        var resources = MakeResources();
        File.WriteAllText(Path.Combine(resources, "dictionary.db"), "res");

        Assert.Equal(
            Path.Combine(_baseDir, "..", "Resources", "dictionary.db"),
            AppPaths.ResolveBundledFile("dictionary.db", _baseDir));
    }

    [Fact]
    public void File_MissingEverywhere_ReturnsNull()
    {
        MakeResources();

        Assert.Null(AppPaths.ResolveBundledFile("dictionary.db", _baseDir));
    }

    // ---- directory variant (audio/) ----

    [Fact]
    public void Directory_ExeAdjacent_WinsOverResources()
    {
        Directory.CreateDirectory(Path.Combine(_baseDir, "audio"));
        Directory.CreateDirectory(Path.Combine(MakeResources(), "audio"));

        Assert.Equal(
            Path.Combine(_baseDir, "audio"),
            AppPaths.ResolveBundledDirectory("audio", _baseDir));
    }

    [Fact]
    public void Directory_ResourcesFallback_WhenNothingAdjacent()
    {
        Directory.CreateDirectory(Path.Combine(MakeResources(), "audio"));

        Assert.Equal(
            Path.Combine(_baseDir, "..", "Resources", "audio"),
            AppPaths.ResolveBundledDirectory("audio", _baseDir));
    }

    [Fact]
    public void Directory_MissingEverywhere_ReturnsNull()
    {
        MakeResources();

        Assert.Null(AppPaths.ResolveBundledDirectory("audio", _baseDir));
    }
}
