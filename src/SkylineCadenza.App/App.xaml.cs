using System.Windows;

namespace SkylineCadenza.App;

public partial class App : Application
{
    /// <summary>
    /// Command-line args captured at startup. When Skyline launches the
    /// tool, <c>args[0]</c> is the named-pipe name passed via the
    /// <c>Arguments=$(SkylineConnection)</c> entry in
    /// <c>tool-inf/SkylineCadenza.properties</c>. The view-model uses it
    /// to attach to the running document.
    /// </summary>
    public static string[] LaunchArgs { get; private set; } = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        LaunchArgs = e.Args ?? Array.Empty<string>();
        base.OnStartup(e);
    }
}
