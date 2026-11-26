internal static class ServerPaths
{
    public static string FindContentRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "LoclessDisk.slnx")))
                return folder.FullName;
        }
        return AppContext.BaseDirectory;
    }

    public static string FindCatalog(string root)
    {
        string catalog = Path.Combine(root, "data", "catalog.db");
        string legacy = Path.Combine(root, "LoclessDisk.Server", "data", "catalog.db");
        return !File.Exists(catalog) && File.Exists(legacy) ? legacy : catalog;
    }
}
