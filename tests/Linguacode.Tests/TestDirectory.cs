namespace Linguacode.Tests;
public sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Linguacode-tests-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, true);
    }
}
