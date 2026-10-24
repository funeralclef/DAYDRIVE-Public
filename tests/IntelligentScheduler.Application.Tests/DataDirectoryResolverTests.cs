using IntelligentScheduler.Application;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class DataDirectoryResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daydrive-paths-" + Guid.NewGuid());

    public DataDirectoryResolverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PortableDataFollowsTheExecutableEvenWhenWorkingDirectoryDiffers()
    {
        File.WriteAllText(Path.Combine(_root, DataDirectoryResolver.PortableMarker), "");
        Assert.Equal(Path.Combine(_root, "Data"), Resolve([]));
    }

    [Fact]
    public void InstalledCopyKeepsExistingUserDataLocation()
        => Assert.Equal(Path.Combine(_root, "user", "IntelligentScheduler"), Resolve([]));

    [Fact]
    public void ExplicitDirectoryOverridesEnvironmentAndPortableMode()
    {
        File.WriteAllText(Path.Combine(_root, DataDirectoryResolver.PortableMarker), "");
        var chosen = Path.Combine(_root, "chosen data");
        Assert.Equal(chosen, Resolve([$"--data-dir=\"{chosen}\""], Path.Combine(_root, "environment")));
    }

    [Fact]
    public void EnvironmentOverridesPortableMode()
    {
        File.WriteAllText(Path.Combine(_root, DataDirectoryResolver.PortableMarker), "");
        var chosen = Path.Combine(_root, "environment");
        Assert.Equal(chosen, Resolve([], chosen));
    }

    private string Resolve(string[] args, string? environment = null)
        => DataDirectoryResolver.Resolve(args, environment, _root, Path.Combine(_root, "user"));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
