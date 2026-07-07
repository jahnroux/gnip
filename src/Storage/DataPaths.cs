namespace Gnip;

/// <summary>Resolves configured relative paths against the content root; absolute paths pass through.</summary>
public static class DataPaths
{
    public static string Resolve(IHostEnvironment env, string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
}
