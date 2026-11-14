using Velopack;

namespace Linguacode.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
#if UPDATE_SMOKE
        if (Environment.GetEnvironmentVariable("LINGUACODE_UPDATE_TEST_DATA") is { Length: > 0 } testData)
        {
            try { UpdatePackageSmoke.Run(testData, args).GetAwaiter().GetResult(); }
            catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(testData, "probe-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
#endif
        var app = new App(); app.InitializeComponent(); app.Run();
    }
}
