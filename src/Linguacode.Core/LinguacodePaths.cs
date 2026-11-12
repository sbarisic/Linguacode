namespace Linguacode.Core;

public static class LinguacodePaths
{
    public static string DataDirectory(string? directory) => Path.GetFullPath(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linguacode"));
}
