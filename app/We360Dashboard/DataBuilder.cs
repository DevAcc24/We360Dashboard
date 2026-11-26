using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.Data.Sqlite;

namespace We360Dashboard;

/// <summary>
/// Переносить дані агента в архів (лише читаючи файли агента) і будує JSON для сторінки.
/// </summary>
public sealed partial class DataBuilder
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const string IsoFmt = "yyyy-MM-dd'T'HH:mm:sszzz";

    readonly string _archivePath;
    readonly TimeZoneInfo _tz;

    public DataBuilder(string archivePath, TimeZoneInfo tz)
    {
        _archivePath = archivePath;
        _tz = tz;
    }

    /// <summary>Зона з налаштувань, а якщо її не задано або не знайдено - зона цього комп'ютера.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); } catch { }
        return TimeZoneInfo.Local;
    }

    const string Schema = """
        CREATE TABLE IF NOT EXISTS std  (id TEXT PRIMARY KEY, start TEXT, dur INT, act INT, idle INT, keys INT, clicks INT, apps TEXT);
        CREATE TABLE IF NOT EXISTS app  (id TEXT PRIMARY KEY, start TEXT, app TEXT, title TEXT, url TEXT, dur INT, act INT, idle INT, keys INT, clicks INT);
        CREATE TABLE IF NOT EXISTS pwr  (id TEXT PRIMARY KEY, start TEXT, kind TEXT);
        CREATE TABLE IF NOT EXISTS sec  (id TEXT PRIMARY KEY, start TEXT, app TEXT, comment TEXT);
        CREATE TABLE IF NOT EXISTS logfiles (name TEXT PRIMARY KEY, size INT, mtime INT);
        CREATE TABLE IF NOT EXISTS logstd (file TEXT, bucket TEXT, dur_ms INT, act_ms INT, idle_ms INT, keys INT, clicks INT, PRIMARY KEY (file, bucket));
        """;

    /// <summary>Оновлює архів із папки агента. Файли агента лише копіюються на читання.</summary>
    public void Sync(string? dbPath, string? logsDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_archivePath)!);
        using var arch = Open(_archivePath);
        Exec(arch, Schema);
        if (dbPath != null && File.Exists(dbPath)) SyncDb(arch, dbPath);
        if (logsDir != null && Directory.Exists(logsDir)) SyncLogs(arch, logsDir);
    }

    static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    static void Exec(SqliteConnection c, string sql, params (string, object?)[] ps) => Exec(c, null, sql, ps);

    static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] ps)
    {
        using var cmd = Command(c, tx, sql, ps);
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = Command(c, null, sql, ps);
        return cmd.ExecuteScalar();
    }

    /// <summary>Рядки запиту по одному; читач дійсний лише всередині ітерації.</summary>
    static IEnumerable<SqliteDataReader> Query(SqliteConnection c, string sql)
    {
        using var cmd = Command(c, null, sql, []);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return r;
    }

    /// <summary>Файл агента на читання, не заважаючи агентові писати в нього чи видаляти його.</summary>
    static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    static void CopyShared(string src, string dst)
    {
        using var i = OpenShared(src);
        using var o = new FileStream(dst, FileMode.Create, FileAccess.Write);
        i.CopyTo(o);
    }

    /// <summary>
    /// Узгоджений знімок бази агента. SQLite backup API читає базу однією транзакцією читання і бачить її
    /// в одному стані, навіть якщо агент саме пише. Базу відкриваємо лише на читання.
    /// </summary>
    const int SQLITE_BUSY = 5, SQLITE_LOCKED = 6;

    static void Snapshot(string dbPath, string copy)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var src = Open(dbPath, SqliteOpenMode.ReadOnly);
                using var dst = Open(copy);
                src.BackupDatabase(dst);
                return;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SQLITE_BUSY or SQLITE_LOCKED && attempt < 3)
            {
                // база зайнята або агент саме перебудовує журнал - трохи чекаємо і пробуємо ще раз
                try { File.Delete(copy); } catch { }
                Thread.Sleep(300 * (attempt + 1));
            }
            catch (SqliteException)
            {
                // запасний шлях, якщо базу не вдалося відкрити на читання (наприклад, немає -shm і папка лише для читання):
                // копіюємо файли (-shm SQLite відтворює сам); знімок може бути неузгодженим, тоді наступне оновлення сторінки його виправить
                try { File.Delete(copy); } catch { }
                CopyShared(dbPath, copy);
                if (File.Exists(dbPath + "-wal")) CopyShared(dbPath + "-wal", copy + "-wal");
                return;
            }
        }
    }

    void SyncDb(SqliteConnection arch, string dbPath)
    {
        // знімок у тимчасову папку, щоб не тримати базу агента відкритою, поки переносимо дані
        var tmp = Path.Combine(AppSettings.TempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var copy = Path.Combine(tmp, "zs.db");
            Snapshot(dbPath, copy);

            Exec(arch, "ATTACH DATABASE $p AS src", ("$p", copy));
            try
            {
                using var tx = arch.BeginTransaction();
                // агент дописує поточний інтервал у той самий рядок, тому в std і app оновлюємо рядки, що змінилися
                // (незмінені не переписуємо); рядки, які агент уже видалив, лишаються в архіві як є.
                // Рядки без часу початку пропускаємо: показати їх ніде, а в архіві вони лишилися б назавжди.
                Exec(arch, tx, """
                    INSERT INTO std SELECT id,start_time,duration,active_time,idle_time,keystroke_count,mouse_clicks,app_title
                      FROM src.app_stat_events WHERE event_type='STANDARD' AND start_time IS NOT NULL
                    ON CONFLICT(id) DO UPDATE SET start=excluded.start, dur=excluded.dur, act=excluded.act, idle=excluded.idle,
                      keys=excluded.keys, clicks=excluded.clicks, apps=excluded.apps
                    WHERE (start, dur, act, idle, keys, clicks, apps) IS NOT
                          (excluded.start, excluded.dur, excluded.act, excluded.idle, excluded.keys, excluded.clicks, excluded.apps)
                    """);
                Exec(arch, tx, """
                    INSERT INTO app SELECT id,start_time,app_name,app_title,url,duration,active_time,idle_time,keystroke_count,mouse_clicks
                      FROM src.app_stat_events WHERE event_type='APP_LOG' AND start_time IS NOT NULL
                    ON CONFLICT(id) DO UPDATE SET start=excluded.start, app=excluded.app, title=excluded.title, url=excluded.url,
                      dur=excluded.dur, act=excluded.act, idle=excluded.idle, keys=excluded.keys, clicks=excluded.clicks
                    WHERE (start, app, title, url, dur, act, idle, keys, clicks) IS NOT
                          (excluded.start, excluded.app, excluded.title, excluded.url, excluded.dur, excluded.act, excluded.idle, excluded.keys, excluded.clicks)
                    """);
                Exec(arch, tx, "INSERT OR IGNORE INTO pwr SELECT id,start_time,app_title FROM src.app_stat_events WHERE event_type='POWER_LOG' AND start_time IS NOT NULL");
                Exec(arch, tx, "INSERT OR IGNORE INTO sec SELECT id,start_time,application_name,comment FROM src.security_events WHERE start_time IS NOT NULL");
                tx.Commit();
            }
            finally { Exec(arch, "DETACH DATABASE src"); }
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [GeneratedRegex("""^(\S+)\s+INFO\s+APP_LOGS:\s+"Apps: (.*?) \| IO: Keys: (\d+) Clicks: (\d+).*?Duration: (\d+)ms Active: (\d+)ms Idle: (\d+)ms""")]
    private static partial Regex LogRe();

    void SyncLogs(SqliteConnection arch, string logsDir)
    {
        foreach (var path in Directory.GetFiles(logsDir, "log_*.log").Order())
        {
            var fi = new FileInfo(path);
            var name = fi.Name;
            long size = fi.Length, mtime = fi.LastWriteTimeUtc.Ticks;
            (string, object?)[] fileKey = [("$n", name), ("$s", size), ("$m", mtime)];
            if (Scalar(arch, "SELECT 1 FROM logfiles WHERE name=$n AND size=$s AND mtime=$m", fileKey) != null) continue; // файл не змінився

            var buckets = new Dictionary<long, long[]>();
            using (var fs = OpenShared(path))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (!line.Contains("APP_LOGS:", StringComparison.Ordinal)) continue;
                    var m = LogRe().Match(line);
                    if (!m.Success) continue;
                    if (!DateTimeOffset.TryParse(m.Groups[1].Value, Inv, DateTimeStyles.None, out var end)) continue;
                    long dur = long.Parse(m.Groups[5].Value, Inv);
                    var key = Bucket(end.AddMilliseconds(-dur));
                    if (!buckets.TryGetValue(key, out var b)) buckets[key] = b = new long[5];
                    b[0] += dur;
                    b[1] += long.Parse(m.Groups[6].Value, Inv);
                    b[2] += long.Parse(m.Groups[7].Value, Inv);
                    b[3] += long.Parse(m.Groups[3].Value, Inv);
                    b[4] += long.Parse(m.Groups[4].Value, Inv);
                }
            }

            using var tx = arch.BeginTransaction();
            Exec(arch, tx, "DELETE FROM logstd WHERE file=$n", fileKey[0]);
            using (var ins = arch.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO logstd VALUES ($f,$b,$d,$a,$i,$k,$c)";
                var pf = ins.Parameters.Add("$f", SqliteType.Text); var pb = ins.Parameters.Add("$b", SqliteType.Text);
                var pd = ins.Parameters.Add("$d", SqliteType.Integer); var pa = ins.Parameters.Add("$a", SqliteType.Integer);
                var pi = ins.Parameters.Add("$i", SqliteType.Integer); var pk = ins.Parameters.Add("$k", SqliteType.Integer);
                var pc = ins.Parameters.Add("$c", SqliteType.Integer);
                pf.Value = name;
                foreach (var (k, v) in buckets)
                {
                    // інтервал зберігаємо в UTC, щоб зміна часової зони не псувала вже розібрані логи
                    pb.Value = BucketStart(k).ToString(IsoFmt, Inv); pd.Value = v[0]; pa.Value = v[1]; pi.Value = v[2]; pk.Value = v[3]; pc.Value = v[4];
                    ins.ExecuteNonQuery();
                }
            }
            Exec(arch, tx, "INSERT OR REPLACE INTO logfiles VALUES ($n,$s,$m)", fileKey);
            tx.Commit();
        }
    }

    // ---------------- payload ----------------

    /// <summary>Час з бази агента (UTC, напр. 'YYYY-MM-DDThh:mm:ss.fffz') або null, якщо його немає чи він не розбирається.</summary>
    static DateTimeOffset? ParseUtc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd('z', 'Z');
        if (!DateTime.TryParse(s, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)) return null;
        return new DateTimeOffset(dt, TimeSpan.Zero);
    }

    /// <summary>Час у вибраній зоні у форматі, який читає сторінка.</summary>
    string Iso(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, _tz).ToString(IsoFmt, Inv);

    string? ToLocalIso(string? utc) => ParseUtc(utc) is { } t ? Iso(t) : null;

    static readonly long BucketTicks = TimeSpan.FromMinutes(5).Ticks;
    /// <summary>Номер 5-хвилинного проміжку (в UTC), у який потрапляє момент t.</summary>
    static long Bucket(DateTimeOffset t) => t.UtcTicks / BucketTicks;
    static DateTimeOffset BucketStart(long bucket) => new(bucket * BucketTicks, TimeSpan.Zero);

    /// <summary>Наступна місцева північ після t (з урахуванням переходу на літній/зимовий час).</summary>
    DateTimeOffset NextMidnight(DateTimeOffset local)
    {
        var d = local.Date.AddDays(1);
        while (_tz.IsInvalidTime(d)) d = d.AddMinutes(30); // у деяких зонах о 00:00 переводять годинник
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(d, _tz)), _tz);
    }

    /// <summary>Додає інтервал у місцевому часі; якщо він переходить через північ, ділить його між днями пропорційно.</summary>
    void AddInterval(List<object[]> list, DateTimeOffset start, long dur, long act, long idle, long keys, long clicks, int src)
    {
        var loc = TimeZoneInfo.ConvertTime(start, _tz);
        for (int guard = 0; guard < 400; guard++)
        {
            var next = NextMidnight(loc);
            long first = (long)(next - loc).TotalSeconds;
            if (dur <= first || first <= 0) break;
            double f = (double)first / dur;
            long a1 = (long)Math.Round(act * f), i1 = (long)Math.Round(idle * f), k1 = (long)Math.Round(keys * f), c1 = (long)Math.Round(clicks * f);
            list.Add([Iso(loc), first, a1, i1, k1, c1, src]);
            dur -= first; act -= a1; idle -= i1; keys -= k1; clicks -= c1;
            loc = next;
        }
        list.Add([Iso(loc), dur, act, idle, keys, clicks, src]);
    }

    static string? Domain(string? url)
    {
        if (string.IsNullOrEmpty(url) || url == "None") return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null; // агент іноді пише сюди заголовки вікон
        var h = u.Host;
        if (string.IsNullOrEmpty(h)) return null;
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }

    static string Truncate(string s, int n)
    {
        if (s.Length <= n) return s;
        if (char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n];
    }

    static long L(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
    static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();

    public string BuildJson()
    {
        using var arch = Open(_archivePath);
        Exec(arch, Schema);

        var intervals = new List<object[]>();
        // 5-хвилинні проміжки, які вже покриває база агента: логи за них не додаємо, щоб не рахувати час двічі
        var covered = new HashSet<long>();
        foreach (var r in Query(arch, "SELECT start,dur,act,idle,keys,clicks FROM std"))
        {
            if (ParseUtc(S(r, 0)) is not { } st) continue;
            long dur = L(r, 1);
            long b0 = Bucket(st), b1 = Bucket(st.AddSeconds(Math.Max(dur, 1) - 1));
            for (long b = b0; b <= b1 && b - b0 < 1000; b++) covered.Add(b); // обмеження - захист від битих тривалостей
            AddInterval(intervals, st, dur, L(r, 2), L(r, 3), L(r, 4), L(r, 5), 0);
        }
        // bucket записано зі зсувом (старі архіви - у зоні на момент розбору, нові - в UTC), тож той самий момент
        // може лежати двома рядками; зводимо за номером проміжку
        var logs = new Dictionary<long, long[]>();
        foreach (var r in Query(arch, "SELECT bucket,sum(dur_ms),sum(act_ms),sum(idle_ms),sum(keys),sum(clicks) FROM logstd GROUP BY bucket"))
        {
            if (!DateTimeOffset.TryParse(S(r, 0), Inv, DateTimeStyles.None, out var b)) continue;
            var key = Bucket(b);
            if (!logs.TryGetValue(key, out var a)) logs[key] = a = new long[5];
            for (int i = 0; i < 5; i++) a[i] += L(r, i + 1);
        }
        foreach (var (bucket, a) in logs)
        {
            long dur = (long)Math.Round(a[0] / 1000.0);
            if (covered.Contains(bucket) || dur <= 0) continue;
            AddInterval(intervals, BucketStart(bucket), dur, (long)Math.Round(a[1] / 1000.0), (long)Math.Round(a[2] / 1000.0), a[3], a[4], 1);
        }
        intervals.Sort((a, b) => string.CompareOrdinal((string)a[0], (string)b[0]));

        var agg = new Dictionary<(string, string, string, string), long[]>();
        foreach (var r in Query(arch, "SELECT start,app,title,url,dur,act,keys,clicks FROM app"))
        {
            if (ParseUtc(S(r, 0)) is not { } st) continue;
            var day = TimeZoneInfo.ConvertTime(st, _tz).ToString("yyyy-MM-dd", Inv);
            var key = (day, S(r, 1) ?? "?", Domain(S(r, 3)) ?? "", Truncate(S(r, 2) ?? "", 160));
            if (!agg.TryGetValue(key, out var a)) agg[key] = a = new long[4];
            a[0] += L(r, 4); a[1] += L(r, 5); a[2] += L(r, 6); a[3] += L(r, 7);
        }
        var apps = agg.Select(kv => new object[] { kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Key.Item4, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3] }).ToList();

        var power = new List<object[]>();
        foreach (var r in Query(arch, "SELECT start,kind FROM pwr ORDER BY start")) // сторінка зводить пари блокування по порядку
            if (ToLocalIso(S(r, 0)) is { } t) power.Add([t, S(r, 1) ?? ""]);
        var sec = new List<object[]>();
        foreach (var r in Query(arch, "SELECT start,app,comment FROM sec ORDER BY start"))
            if (ToLocalIso(S(r, 0)) is { } t) sec.Add([t, S(r, 1) ?? "", S(r, 2) ?? ""]);

        var payload = new Dictionary<string, object>
        {
            ["generated"] = Iso(DateTimeOffset.UtcNow),
            ["tz"] = _tz.Id,
            ["intervals"] = intervals,
            ["apps"] = apps,
            ["power"] = power,
            ["security"] = sec,
        };
        // кодувальник за замовчуванням екранує < > & — заголовок вікна не закриє <script>
        var opts = new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
        return JsonSerializer.Serialize(payload, opts);
    }

    // шаблон вшитий у exe і не змінюється, тож читаємо його один раз
    static readonly Lazy<string> Template = new(() =>
    {
        using var s = typeof(DataBuilder).Assembly.GetManifestResourceStream("dashboard_template.html")!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    });

    public static string RenderHtml(string json) => Template.Value.Replace("/*__DATA__*/null", json);
}
