namespace Hopital.Web;

/// <summary>The single-file web page (index.html), embedded in the program.</summary>
public static class Page
{
    public static string Html { get; } = Load();

    private static string Load()
    {
        using var stream = typeof(Page).Assembly.GetManifestResourceStream("Hopital.Web.index.html")
            ?? throw new InvalidOperationException("The web page is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
