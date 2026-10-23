namespace IntelligentScheduler.Application;

public static class DataDirectoryResolver
{
    public const string PortableMarker = "daydrive.portable";

    public static string Resolve(IEnumerable<string> arguments, string? environmentDirectory,
        string applicationDirectory, string localApplicationDataDirectory)
    {
        var argument = arguments.FirstOrDefault(value =>
            value.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase));
        var configured = argument is null ? environmentDirectory : argument["--data-dir=".Length..].Trim('"');
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        return File.Exists(Path.Combine(applicationDirectory, PortableMarker))
            ? Path.Combine(applicationDirectory, "Data")
            : Path.Combine(localApplicationDataDirectory, "IntelligentScheduler");
    }
}
