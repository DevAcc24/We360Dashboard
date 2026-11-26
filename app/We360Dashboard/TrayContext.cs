using System.Diagnostics;

namespace We360Dashboard;

public sealed class TrayContext : ApplicationContext
{
    public const string AppName = "Моя статистика we360";

    readonly NotifyIcon _tray;
    readonly LocalServer _server;
    readonly SemaphoreSlim _buildLock = new(1, 1);
    string? _cachedHtml;
    DateTime _cachedAt;

    public static string UrlFile => Path.Combine(AppSettings.AppDir, "url.txt");

    public TrayContext()
    {
        var settings = AppSettings.Load();
        _server = new LocalServer(settings.Port, settings.Token, RenderAsync);
        File.WriteAllText(UrlFile, _server.Url);

        var menu = new ContextMenuStrip();
        var open = menu.Items.Add("Відкрити статистику", null, (_, _) => OpenInBrowser());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add("Копіювати адресу", null, (_, _) => Clipboard.SetText(_server.Url));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Вибрати папку з даними агента…", null, (_, _) => PickDataFolder());
        menu.Items.Add("Відкрити налаштування (settings.json)", null, (_, _) => Shell(AppSettings.SettingsPath));
        menu.Items.Add("Відкрити папку застосунку", null, (_, _) => Shell(AppSettings.AppDir));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Вихід", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenInBrowser(); };

        if (settings.IsBroken) WarnBrokenSettings();
        else if (settings.ResolveDataFolder() == null)
            Balloon("Не знайшов базу агента. Клацніть по іконці правою кнопкою → «Вибрати папку з даними агента…».", ToolTipIcon.Info);
    }

    void Balloon(string text, ToolTipIcon icon) => _tray.ShowBalloonTip(10000, AppName, text, icon);

    void WarnBrokenSettings() =>
        Balloon("У settings.json помилка, тому зараз діють типові налаштування. Виправте файл: меню іконки → «Відкрити налаштування».", ToolTipIcon.Warning);

    static void Warn(string text) => MessageBox.Show(text, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    static Icon LoadIcon()
    {
        using var s = typeof(TrayContext).Assembly.GetManifestResourceStream("app.ico");
        return s != null ? new Icon(s, SystemInformation.SmallIconSize) : SystemIcons.Application;
    }

    void OpenInBrowser()
    {
        // налаштування читаємо щоразу, бо файл могли змінити вручну
        var s = AppSettings.Load();
        if (s.IsBroken) WarnBrokenSettings();
        if (s.ResolveDataFolder() == null && !PickDataFolder()) return;
        _cachedHtml = null; // при явному відкритті завжди беремо свіжі дані
        Shell(_server.Url);
    }

    /// <summary>Відкриває адресу, файл чи папку програмою за замовчуванням.</summary>
    public static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Warn(ex.Message); }
    }

    bool PickDataFolder()
    {
        const string broken = "У settings.json помилка. Виправте його або видаліть, щоб створився новий, і спробуйте ще раз.";
        var current = AppSettings.Load();
        if (current.IsBroken) { Warn(broken); return false; }
        using var dlg = new FolderBrowserDialog
        {
            Description = $"Папка, де лежить {AppSettings.AgentDbFile} (зазвичай %APPDATA%\\ai.zs\\zs)",
            UseDescriptionForTitle = true,
            InitialDirectory = current.DataFolder ?? AppSettings.DefaultAgentFolder,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return false;
        if (!AppSettings.HasAgentDb(dlg.SelectedPath)) { Warn($"У цій папці немає {AppSettings.AgentDbFile}."); return false; }
        // читаємо файл ще раз уже після діалогу: поки він був відкритий, settings.json могли змінити
        var fresh = AppSettings.Load();
        if (fresh.IsBroken) { Warn(broken); return false; }
        fresh.DataFolder = dlg.SelectedPath;
        fresh.Save();
        _cachedHtml = null;
        return true;
    }

    async Task<string> RenderAsync(CancellationToken ct)
    {
        await _buildLock.WaitAsync(ct);
        try
        {
            // кілька запитів поспіль (наприклад, F5) не перебудовують дані частіше ніж раз на 15 с
            if (_cachedHtml != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(15)) return _cachedHtml;
            var s = AppSettings.Load(); // підхоплюємо зміни в settings.json без перезапуску
            var data = s.ResolveDataFolder();
            if (data == null)
                return $"<!doctype html><meta charset=utf-8><title>{AppName}</title>" +
                       $"<p style='font:16px system-ui;padding:40px'>Не знайшов базу агента ({AppSettings.AgentDbFile}). Виберіть папку через меню іконки в треї.</p>";
            var html = await Task.Run(() => Build(s, data), ct);
            _cachedHtml = html;
            _cachedAt = DateTime.UtcNow;
            return html;
        }
        finally { _buildLock.Release(); }
    }

    public static string Build(AppSettings s, string dataFolder, string? archivePath = null)
    {
        var builder = new DataBuilder(archivePath ?? AppSettings.ArchivePath, DataBuilder.ResolveTimeZone(s.TimeZone));
        builder.Sync(Path.Combine(dataFolder, AppSettings.AgentDbFile), s.ResolveLogsFolder(dataFolder));
        return DataBuilder.RenderHtml(builder.BuildJson());
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _server.Dispose();
        try { File.Delete(UrlFile); } catch { }
        base.ExitThreadCore();
    }
}
