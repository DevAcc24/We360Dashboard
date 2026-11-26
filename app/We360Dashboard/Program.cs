namespace We360Dashboard;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // SQLite, вбудований у Windows (winsqlite3.dll): нічого не розпаковується і не встановлюється
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

        // Режим без інтерфейсу для перевірки: We360Dashboard.exe --build <папка з zs.db> <вихідний.html> [archive.db]
        if (args.Length >= 3 && args[0] == "--build")
        {
            var s = new AppSettings(); // типові налаштування, без запису на диск
            var html = TrayContext.Build(s, args[1], args.Length > 3 ? args[3] : Path.Combine(AppSettings.AppDir, "check.db"));
            File.WriteAllText(args[2], html);
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\We360Dashboard", out bool first);
        if (!first)
        {
            // уже запущено — просто відкриваємо сторінку
            try
            {
                var url = File.ReadAllText(TrayContext.UrlFile).Trim();
                if (url.StartsWith("http://127.0.0.1:")) TrayContext.Shell(url);
            }
            catch { }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
        return 0;
    }
}
