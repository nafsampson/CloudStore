//summary//
// Keeps demo data beneath the project and refuses local mode in a deployed production environment.
//summary//
//-------------------Start Of File------------------//
namespace CloudStore.LocalDemo;

public sealed record DemoSettings(string Directory)
{
    public string AccountsFile => Path.Combine(Directory, "accounts.db");
    public string RecordsFile => Path.Combine(Directory, "retail.db");
    public string ReadyFile => Path.Combine(Directory, "ready");
    public static DemoSettings Create(string contentRoot, IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("LocalDemo is allowed only in Development. Azure mode remains the production default.");
        var root = Path.GetFullPath(contentRoot);
        var relative = configuration["LocalDemo:Directory"] ?? ".local-demo";
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var allowed = Path.Combine(root, ".local-demo");
        if (path != allowed && !path.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Demo data must stay inside the project's .local-demo directory.");
        return new(path);
    }
    public void RequireReady()
    {
        if (!File.Exists(ReadyFile) || !File.Exists(AccountsFile) || !File.Exists(RecordsFile))
            throw new InvalidOperationException("Local demo is not initialized. Run the LocalDemo profile with demo-setup first; Azure is never used as a fallback.");
    }
}
//-------------------End Of File------------------//
