using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InGameCompanion.Ai;

namespace InGameCompanion.Core;

/// <summary>
/// Yeni bir oyun ilk kez sorulduğunda ya da profili olmayan bir oyun algılandığında arka planda, sormadan,
/// oyunu tanıyıp spoilersiz bir profil (games/&lt;Oyun&gt;.md) oluşturur ve settings.json'daki Games tablosuna ekler.
/// Sonraki sorularda profil sistem talimatına girer (oyunun adı, türü, arayüz terimleri, karıştırılmaması gerekenler).
/// Oyun olmayan uygulamalar (tarayıcı, editör…) hem sabit bir listeyle hem de modelin "oyun değil" yanıtıyla elenir.
/// </summary>
internal sealed class GameProfiler
{
    /// <summary>Kesinlikle oyun olmayan işlemler: model çağrısı bile yapılmaz.</summary>
    private static readonly HashSet<string> NeverGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore", "code", "devenv", "notepad", "notepad++",
        "cmd", "powershell", "pwsh", "WindowsTerminal", "claude", "discord", "slack", "teams", "ms-teams", "spotify", "outlook",
        "winword", "excel", "powerpnt", "onenote", "obsidian", "steam", "steamwebhelper", "epicgameslauncher", "galaxyclient",
        "battle.net", "eadesktop", "ubisoftconnect", "upc", "vlc", "mpv", "obs64", "obs", "idea64", "rider64", "pycharm64", "studio64",
        "sublime_text", "InGameCompanion", "ApplicationFrameHost", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "TextInputHost", "LockApp", "SystemSettings", "mspaint", "Photos", "Calculator", "WhatsApp", "Telegram", "Zoom",
    };

    private readonly Func<Settings> _settings;
    private readonly GeminiClient _gemini;
    private readonly GameMemoryStore _memory;
    private readonly GameDetector _detector;
    private readonly Action<string, string> _notify;
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _ignoredGate = new();

    public static string GamesDir => Path.Combine(AppContext.BaseDirectory, "games");
    private static string IgnoredFile => Path.Combine(GamesDir, "ignored.json");

    public GameProfiler(Func<Settings> settings, GeminiClient gemini, GameMemoryStore memory, GameDetector detector,
                        Action<string, string> notify)
    {
        _settings = settings;
        _gemini = gemini;
        _memory = memory;
        _detector = detector;
        _notify = notify;
        try
        {
            if (File.Exists(IgnoredFile))
                foreach (var n in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(IgnoredFile)) ?? new()) _ignored.Add(n);
        }
        catch (Exception ex) { Log.Warn("ignored.json okunamadı: " + ex.Message); }
    }

    /// <summary>Profili olmayan bir oyun soruluyorsa arka planda profil oluşturmayı başlatır (bloklamaz).</summary>
    public void Ensure(GameContext? game, byte[] frameJpeg)
    {
        if (game == null || !_settings().AutoGameProfile) return;
        if (!string.IsNullOrWhiteSpace(game.ProfileText)) return;
        var proc = game.ProcessName;
        if (string.IsNullOrWhiteSpace(proc) || NeverGames.Contains(proc)) return;
        lock (_ignoredGate) if (_ignored.Contains(proc)) return;
        if (_retryAfter.TryGetValue(proc, out var t) && DateTime.UtcNow < t) return;
        _ = Task.Run(() => RunAsync(game, frameJpeg, force: false));
    }

    /// <summary>Mevcut profil olsa bile yeniden oluşturur (elle yazılmış profil önce .bak olarak yedeklenir).</summary>
    public void Regenerate(GameContext game, byte[] frameJpeg)
    {
        lock (_ignoredGate) _ignored.Remove(game.ProcessName);
        _ = Task.Run(() => RunAsync(game, frameJpeg, force: true));
    }

    private async Task RunAsync(GameContext game, byte[] jpeg, bool force)
    {
        var proc = game.ProcessName;
        if (!_inFlight.TryAdd(proc, 0)) return;
        try
        {
            Log.Info($"Oyun profili oluşturuluyor: {proc} ('{game.WindowTitle}')");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            var text = await _gemini.GenerateProfileAsync(proc, game.WindowTitle, jpeg, cts.Token).ConfigureAwait(false);
            var (isGame, name, profile) = Parse(text);

            if (!isGame)
            {
                Log.Info($"'{proc}' oyun değil (modelin yanıtı); bir daha denenmeyecek");
                AddIgnored(proc);
                return;
            }
            if (string.IsNullOrWhiteSpace(name) || profile.Length < 40)
            {
                Log.Warn($"Oyun profili anlaşılamadı ({proc}); 30 dk sonra tekrar denenecek");
                _retryAfter[proc] = DateTime.UtcNow.AddMinutes(30);
                return;
            }

            var s = _settings();
            string finalName;
            string file;
            lock (Settings.SaveLock)
            {
                s.Games.TryGetValue(proc, out var entry);
                finalName = !string.IsNullOrWhiteSpace(entry?.Name) ? entry!.Name : name;
                file = !string.IsNullOrWhiteSpace(entry?.Profile) ? entry!.Profile! : Slug(finalName) + ".md";
                Directory.CreateDirectory(GamesDir);
                var path = Path.Combine(GamesDir, file);
                if (File.Exists(path) && !File.ReadAllText(path).Contains(AutoMarker))
                    File.Copy(path, path + ".bak", overwrite: true);          // elle yazılmış profili ezme
                File.WriteAllText(path, Header(finalName) + profile.Trim() + Environment.NewLine, new UTF8Encoding(false));
                entry ??= new GameEntry { Name = finalName };
                entry.Profile = file;
                s.Games[proc] = entry;
                s.Save();
            }
            if (!string.Equals(game.GameName, finalName, StringComparison.OrdinalIgnoreCase))
                _memory.Rename(game.GameName, finalName);
            _detector.ClearCache();
            _retryAfter.TryRemove(proc, out _);
            Log.Info($"Oyun profili hazır: {finalName} → games\\{file}");
            _notify("Oyun tanındı", $"**{finalName}** için profil oluşturuldu. Bundan sonra sohbetin bu oyuna özel açılır.");
        }
        catch (OperationCanceledException) { Log.Warn($"Oyun profili zaman aşımı ({proc})"); _retryAfter[proc] = DateTime.UtcNow.AddMinutes(30); }
        catch (Exception ex)
        {
            Log.Warn($"Oyun profili oluşturulamadı ({proc}): {ex.Message.Split('\n')[0]}");
            _retryAfter[proc] = DateTime.UtcNow.AddMinutes(30);
        }
        finally { _inFlight.TryRemove(proc, out _); }
    }

    private const string AutoMarker = "<!-- otomatik-profil -->";

    private static string Header(string name) =>
        $"{AutoMarker}{Environment.NewLine}" +
        $"> Bu profil {DateTime.Now:dd.MM.yyyy} tarihinde otomatik oluşturuldu ve doğrulanmadı ({name}). " +
        $"Emin olmadığın bilgiyi kullanma; düzeltmek için dosyayı elle düzenleyebilirsin.{Environment.NewLine}{Environment.NewLine}";

    /// <summary>Model çıktısı: ilk satır "OYUN: &lt;ad&gt;" (ya da "OYUN: YOK"), geri kalanı profil metni.</summary>
    internal static (bool isGame, string name, string profile) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, "", "");
        var lines = text.Replace("\r", "").Split('\n').ToList();
        while (lines.Count > 0 && (string.IsNullOrWhiteSpace(lines[0]) || lines[0].TrimStart().StartsWith("```"))) lines.RemoveAt(0);
        if (lines.Count == 0) return (false, "", "");
        var first = lines[0].Trim().Trim('*', '#', ' ');
        if (!first.StartsWith("OYUN", StringComparison.OrdinalIgnoreCase)) return (false, "", "");
        int colon = first.IndexOf(':');
        var name = (colon >= 0 ? first[(colon + 1)..] : "").Trim().Trim('*', '"', ' ');
        lines.RemoveAt(0);
        var body = string.Join("\n", lines).Trim();
        if (body.EndsWith("```")) body = body[..^3].TrimEnd();
        if (name.Length == 0 || name.Equals("YOK", StringComparison.OrdinalIgnoreCase) || name.Equals("NOT_A_GAME", StringComparison.OrdinalIgnoreCase))
            return (false, "", "");
        if (name.Length > 80) name = name[..80];
        return (true, name, body);
    }

    private void AddIgnored(string proc)
    {
        lock (_ignoredGate)
        {
            _ignored.Add(proc);
            try
            {
                Directory.CreateDirectory(GamesDir);
                File.WriteAllText(IgnoredFile, JsonSerializer.Serialize(_ignored.OrderBy(x => x).ToList()));
            }
            catch (Exception ex) { Log.Warn("ignored.json yazılamadı: " + ex.Message); }
        }
    }

    private static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_' or ':' or '.') { if (sb.Length > 0 && sb[^1] != '_') sb.Append('_'); }
        }
        var s = sb.ToString().Trim('_');
        return s.Length == 0 ? "oyun" : (s.Length > 60 ? s[..60] : s);
    }
}
