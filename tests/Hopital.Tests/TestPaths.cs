using System.Runtime.CompilerServices;

namespace Hopital.Tests;

internal static class TestPaths
{
    /// <summary>The repository root, found from this source file so it works wherever the build output goes.</summary>
    public static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new FileInfo(thisFile).Directory;
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hopital.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Hopital.slnx not found above the test source.");
    }

    public static string Scenarios => Path.Combine(RepoRoot(), "scenarios");

    public static string ResultSamples => Path.Combine(RepoRoot(), "samples", "results");

    public static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
