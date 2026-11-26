using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace We360Dashboard;

public sealed partial class AppSettings
{
    /// <summary>Папка агента з zs.db. Порожньо = %APPDATA%\ai.zs\zs.</summary>
    public string? DataFolder { get; set; }
    /// <summary>Папка з log_*.log. Порожньо = DataFolder\logs.</summary>
    public string? LogsFolder { get; set; }
    const int DefaultPort = 47360;
    /// <summary>Ім'я файлу бази агента в його папці.</summary>
    public const string AgentDbFile = "zs.db";
    public static bool HasAgentDb(string? folder) => !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, AgentDbFile));

    public int Port { get; set; } = DefaultPort;
    /// <summary>Секретна частина адреси, щоб сторінку не могли прочитати сторонні сайти чи програми.</summary>
    public string Token { get; set; } = "";
    public string? TimeZone { get; set; }
    /// <summary>Портабельно: усе зберігається в підпапці data поруч з exe.</summary>
    public static string AppDir => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "data");
    public static string TempDir => Path.Combine(AppDir, "tmp");
    public static string SettingsPath => Path.Combine(AppDir, "settings.json");
    public static string ArchivePath => Path.Combine(AppDir, "archive.db");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>settings.json є, але його не вдалося розібрати: працюємо з типовими значеннями й файл не перезаписуємо.</summary>
    [JsonIgnore] public bool IsBroken { get; private set; }

    // settings.json читають і пишуть із різних потоків (сторінка будується у фоні, меню - в UI)
    static readonly object FileLock = new();

    [GeneratedRegex("\"Token\"\\s*:\\s*\"([0-9a-fA-F]{8,64})\"")]
    private static partial Regex TokenRe();

    [GeneratedRegex("\"Port\"\\s*:\\s*(\\d{1,5})")]
    private static partial Regex PortRe();

    public static AppSettings Load()
    {
        lock (FileLock)
        {
            AppSettings s;
            string? text = File.Exists(SettingsPath) ? WithRetry(() => File.ReadAllText(SettingsPath)) : null;
            try { s = text != null ? JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new() : new(); }
            catch (JsonException)
            {
                s = new() { IsBroken = true };
                // токен і порт з битого файлу все одно беремо, щоб адреса сторінки не змінювалася з кожним запуском
                var m = TokenRe().Match(text!);
                if (m.Success) s.Token = m.Groups[1].Value;
                var mp = PortRe().Match(text!);
                if (mp.Success && int.TryParse(mp.Groups[1].Value, out var port)) s.Port = port;
            }
            bool changed = text == null;
            if (string.IsNullOrWhiteSpace(s.Token)) { s.Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)); changed = true; }
            if (s.Port is < 1024 or > 65535) { s.Port = DefaultPort; changed = true; }
            // файл з помилкою не чіпаємо, щоб не втратити налаштування: працюємо з типовими, доки його не виправлять
            if (changed && !s.IsBroken) s.SaveLocked();
            return s;
        }
    }

    public void Save()
    {
        if (IsBroken) throw new InvalidOperationException("settings.json містить помилку, його не перезаписуємо.");
        lock (FileLock) SaveLocked();
    }

    void SaveLocked()
    {
        Directory.CreateDirectory(AppDir);
        // пишемо в тимчасовий файл і підміняємо одним кроком, щоб ніхто не прочитав файл наполовину записаним
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        WithRetry(() => { File.Move(tmp, SettingsPath, overwrite: true); return 0; });
    }

    /// <summary>Файл може бути ненадовго зайнятий іншою програмою (редактор, антивірус) - пробуємо ще кілька разів.</summary>
    static T WithRetry<T>(Func<T> action)
    {
        for (int i = 0; ; i++)
        {
            try { return action(); }
            catch (IOException) when (i < 5) { Thread.Sleep(100); }
        }
    }

    public static string DefaultAgentFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ai.zs", "zs");

    /// <summary>Папка з zs.db або null, якщо не знайдено.</summary>
    public string? ResolveDataFolder()
    {
        foreach (var f in new[] { DataFolder, DefaultAgentFolder })
            if (HasAgentDb(f)) return f;
        return null;
    }

    public string? ResolveLogsFolder(string? dataFolder)
    {
        if (!string.IsNullOrWhiteSpace(LogsFolder) && Directory.Exists(LogsFolder)) return LogsFolder;
        if (dataFolder == null) return null;
        var sub = Path.Combine(dataFolder, "logs");
        if (Directory.Exists(sub)) return sub;
        return Directory.GetFiles(dataFolder, "log_*.log").Length > 0 ? dataFolder : null;
    }
}
