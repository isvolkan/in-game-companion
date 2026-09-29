using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using InGameCompanion.Ai;

namespace InGameCompanion.Core;

internal sealed class MemoryNote
{
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
}

internal sealed class MemoryExchange
{
    public string Id { get; set; } = "";
    public string Q { get; set; } = "";
    public string A { get; set; } = "";
    public DateTime At { get; set; }
    /// <summary>Cevabı veren model (biliniyorsa).</summary>
    public string Model { get; set; } = "";
}

/// <summary>Bir oyunun içindeki tek bir sohbet (yeni sohbet açılınca ayrı bağlam başlar).</summary>
internal sealed class ChatSession
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "Yeni sohbet";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public List<MemoryExchange> Messages { get; set; } = new();
}

internal sealed record ChatInfo(string Id, string Title, DateTime Updated, int Count);
internal sealed record GameInfo(string Name, int ChatCount, DateTime Updated);

/// <summary>Bir oyunun kalıcı hafızası — memory/&lt;Oyun&gt;.json</summary>
internal sealed class GameMemoryData
{
    public string Game { get; set; } = "";
    /// <summary>Eski soru-cevapların sıkıştırılmış özeti.</summary>
    public string Summary { get; set; } = "";
    public string? Chapter { get; set; }
    public string? CurrentQuest { get; set; }
    public string? CurrentRegion { get; set; }
    /// <summary>Ekranda görülen görevler (en yeni sonda).</summary>
    public List<string> Quests { get; set; } = new();
    /// <summary>Ekranda görülen bölgeler (en yeni sonda).</summary>
    public List<string> Regions { get; set; } = new();
    public List<MemoryNote> Notes { get; set; } = new();
    /// <summary>Henüz özete katılmamış soru-cevaplar.</summary>
    public List<MemoryExchange> Exchanges { get; set; } = new();
    /// <summary>ESKİ (v0.7 ve öncesi): tek sürekli geçmiş. Açılışta bir sohbete taşınır ve boşaltılır.</summary>
    public List<MemoryExchange> History { get; set; } = new();
    /// <summary>Sohbetler (özetlemeden etkilenmez, tam cevaplar).</summary>
    public List<ChatSession> Chats { get; set; } = new();
    public string ActiveChatId { get; set; } = "";
    public int TotalQuestions { get; set; }
    public DateTime FirstSeen { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>Hafızaya uygulanan değişikliklerin kullanıcıya gösterilecek özeti.</summary>
internal sealed record MemoryUpdate(bool NoteAdded, bool NotesCleared, string? NewQuest, string? NewRegion)
{
    public string? Footer =>
        NotesCleared ? "Notlar silindi"
        : NoteAdded ? "Hafızaya not eklendi"
        : null;
}

/// <summary>
/// Oyun başına kalıcı hafıza:
///  - otomatik ilerleme takibi (ekranda okunan bölüm / görev / bölge)
///  - oyuncunun "hatırla" dediği notlar
///  - son soru-cevaplar (devam soruları için) + eskilerin sıkıştırılmış özeti
/// Tüm üyeler iş parçacığı güvenlidir; her değişiklik diske atomik olarak yazılır.
/// </summary>
internal sealed class GameMemoryStore
{
    private const int MaxQuests = 30, MaxRegions = 20, MaxNotes = 60, MaxExchanges = 40, MaxChatMessages = 300, MaxChats = 60;

    private readonly Dictionary<string, GameMemoryData> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "memory");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ------------------------------------------------------------------ Okuma

    /// <summary>Sistem talimatına eklenecek hafıza bloğu (boşsa null).</summary>
    public string? BuildPromptBlock(string game, bool includeRecent = false)
    {
        lock (_gate)
        {
            var m = Get(game);
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(m.Summary))
                sb.AppendLine("Önceki oturumların özeti: " + m.Summary.Trim());
            if (m.Notes.Count > 0)
            {
                sb.AppendLine("Oyuncunun kaydettiği notlar (en yeni sonda):");
                foreach (var n in m.Notes.TakeLast(20)) sb.AppendLine($"- [{n.At:dd.MM}] {n.Text}");
            }
            if (m.Quests.Count > 0)
                sb.AppendLine("Ekranda görülmüş görevler (en yeni sonda): " + string.Join(" · ", m.Quests.TakeLast(10)));
            if (m.Regions.Count > 0)
                sb.AppendLine("Ekranda görülmüş bölgeler (en yeni sonda): " + string.Join(" · ", m.Regions.TakeLast(6)));
            if (m.TotalQuestions > 0)
                sb.AppendLine($"Bu oyunda toplam {m.TotalQuestions} soru sordu (ilk: {m.FirstSeen:dd.MM.yyyy}).");
            return sb.Length == 0 ? null : sb.ToString().TrimEnd();
        }
    }

    /// <summary>Otomatik ilerleme özeti (spoiler sınırı için).</summary>
    public string? AutoProgress(string game)
    {
        lock (_gate)
        {
            var m = Get(game);
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(m.Chapter)) parts.Add($"bölüm/perde: {m.Chapter}");
            if (!string.IsNullOrWhiteSpace(m.CurrentQuest)) parts.Add($"son görülen görev: {m.CurrentQuest}");
            if (!string.IsNullOrWhiteSpace(m.CurrentRegion)) parts.Add($"son görülen bölge: {m.CurrentRegion}");
            if (m.Quests.Count > 1) parts.Add($"şimdiye kadar {m.Quests.Count} görev görüldü");
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
    }

    // ------------------------------------------------------------------ Sohbetler

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Etkin sohbet (yoksa oluşturur).</summary>
    private ChatSession ActiveChat(GameMemoryData m)
    {
        var c = m.Chats.FirstOrDefault(x => x.Id == m.ActiveChatId);
        if (c != null) return c;
        c = m.Chats.OrderByDescending(x => x.Updated).FirstOrDefault();
        if (c == null)
        {
            c = new ChatSession { Id = NewId() };
            m.Chats.Add(c);
        }
        m.ActiveChatId = c.Id;
        return c;
    }

    public IReadOnlyList<ChatInfo> Chats(string game)
    {
        lock (_gate)
            return Get(game).Chats.OrderByDescending(c => c.Updated)
                .Select(c => new ChatInfo(c.Id, c.Title, c.Updated, c.Messages.Count)).ToList();
    }

    /// <summary>Etkin sohbetin kimliği (yoksa oluşturur).</summary>
    public string ActiveChatId(string game)
    {
        lock (_gate)
        {
            var m = Get(game);
            var before = m.ActiveChatId;
            var c = ActiveChat(m);
            if (before != m.ActiveChatId) Save(m);
            return c.Id;
        }
    }

    public void SetActiveChat(string game, string chatId)
    {
        lock (_gate)
        {
            var m = Get(game);
            if (m.Chats.Any(c => c.Id == chatId) && m.ActiveChatId != chatId) { m.ActiveChatId = chatId; Save(m); }
        }
    }

    /// <summary>Boş yeni sohbet açar ve etkin yapar. Zaten boş bir etkin sohbet varsa onu yeniden kullanır.</summary>
    public string NewChat(string game)
    {
        lock (_gate)
        {
            var m = Get(game);
            var empty = m.Chats.FirstOrDefault(c => c.Id == m.ActiveChatId && c.Messages.Count == 0);
            if (empty != null) return empty.Id;
            var c = new ChatSession { Id = NewId() };
            m.Chats.Add(c);
            m.ActiveChatId = c.Id;
            TrimChats(m);
            Save(m);
            return c.Id;
        }
    }

    /// <summary>Sohbetin mesajları, kronolojik (en eski başta). Kopya.</summary>
    public IReadOnlyList<MemoryExchange> Messages(string game, string chatId)
    {
        lock (_gate)
        {
            var c = Get(game).Chats.FirstOrDefault(x => x.Id == chatId);
            return c == null ? Array.Empty<MemoryExchange>()
                : c.Messages.Select(e => new MemoryExchange { Id = e.Id, Q = e.Q, A = e.A, At = e.At, Model = e.Model }).ToList();
        }
    }

    /// <summary>Tek mesajı siler. Modelin bağlamından da çıkar (özetlenmemiş kayıtlardan). Özet dokunulmaz.</summary>
    public void DeleteMessage(string game, string chatId, string messageId)
    {
        lock (_gate)
        {
            var m = Get(game);
            var c = m.Chats.FirstOrDefault(x => x.Id == chatId);
            var msg = c?.Messages.FirstOrDefault(x => x.Id == messageId);
            if (c == null || msg == null) return;
            c.Messages.Remove(msg);
            m.Exchanges.RemoveAll(e => e.At == msg.At);
            Save(m);
        }
    }

    /// <summary>Sohbeti siler (ilerleme notları ve özet kalır). Etkin sohbet silinirse en yenisi etkin olur.</summary>
    public void DeleteChat(string game, string chatId)
    {
        lock (_gate)
        {
            var m = Get(game);
            var c = m.Chats.FirstOrDefault(x => x.Id == chatId);
            if (c == null) return;
            var stamps = c.Messages.Select(e => e.At).ToHashSet();
            m.Exchanges.RemoveAll(e => stamps.Contains(e.At));
            m.Chats.Remove(c);
            if (m.ActiveChatId == chatId) m.ActiveChatId = m.Chats.OrderByDescending(x => x.Updated).FirstOrDefault()?.Id ?? "";
            Save(m);
        }
    }

    /// <summary>Oyunun tüm sohbetlerini siler (ilerleme notları ve özet kalır).</summary>
    public void DeleteAllChats(string game)
    {
        lock (_gate)
        {
            var m = Get(game);
            m.Chats.Clear();
            m.ActiveChatId = "";
            m.Exchanges.Clear();
            Save(m);
        }
    }

    /// <summary>Hafızası olan oyunlar, en son kullanılan başta.</summary>
    public IReadOnlyList<GameInfo> GameInfos()
    {
        var list = new List<GameInfo>();
        foreach (var name in KnownGames())
        {
            lock (_gate)
            {
                var m = Get(name);
                list.Add(new GameInfo(m.Game.Length > 0 ? m.Game : name, m.Chats.Count, m.UpdatedAt));
            }
        }
        return list.OrderByDescending(g => g.Updated).ToList();
    }

    private static void TrimChats(GameMemoryData m)
    {
        while (m.Chats.Count > MaxChats)
        {
            var oldest = m.Chats.Where(c => c.Id != m.ActiveChatId).OrderBy(c => c.Updated).FirstOrDefault();
            if (oldest == null) break;
            m.Chats.Remove(oldest);
        }
    }

    /// <summary>Modele bağlam olarak gidecek son turlar: ETKİN sohbetten, zaman penceresi içinde.</summary>
    public IReadOnlyList<ChatTurn> Recent(string game, int maxTurns, int maxMinutes)
    {
        var cutoff = DateTime.Now.AddMinutes(-maxMinutes);
        lock (_gate)
        {
            var m = Get(game);
            var chat = m.Chats.FirstOrDefault(c => c.Id == m.ActiveChatId);
            if (chat == null) return Array.Empty<ChatTurn>();
            return chat.Messages
                .Where(e => e.At >= cutoff)
                .TakeLast(Math.Max(0, maxTurns))
                .Select(e => new ChatTurn(e.Q, e.A))
                .ToList();
        }
    }

    // ------------------------------------------------------------------ Yazma

    /// <summary>Modelin @@MEM satırını uygular.</summary>
    public MemoryUpdate ApplyMeta(string game, JsonObject? meta)
    {
        if (meta == null) return new MemoryUpdate(false, false, null, null);
        lock (_gate)
        {
            var m = Get(game);
            bool noteAdded = false, cleared = false;
            string? newQuest = null, newRegion = null;

            if (Bool(meta["forget"]))
            {
                m.Notes.Clear();
                cleared = true;
            }

            var chapter = Str(meta["chapter"]);
            if (chapter != null) m.Chapter = chapter;

            var quest = Str(meta["quest"]);
            if (quest != null)
            {
                if (!m.Quests.Contains(quest, StringComparer.OrdinalIgnoreCase)) newQuest = quest;
                m.CurrentQuest = quest;
                PushUnique(m.Quests, quest, MaxQuests);
            }

            var region = Str(meta["region"]);
            if (region != null)
            {
                if (!m.Regions.Contains(region, StringComparer.OrdinalIgnoreCase)) newRegion = region;
                m.CurrentRegion = region;
                PushUnique(m.Regions, region, MaxRegions);
            }

            var note = Str(meta["note"]);
            if (note != null)
            {
                m.Notes.Add(new MemoryNote { Text = note, At = DateTime.Now });
                if (m.Notes.Count > MaxNotes) m.Notes.RemoveAt(0);
                noteAdded = true;
            }

            if (noteAdded || cleared || chapter != null || quest != null || region != null) Save(m);
            return new MemoryUpdate(noteAdded, cleared, newQuest, newRegion);
        }
    }

    /// <summary>Soru-cevabı kaydeder. Özetleme gerekiyorsa true döner.</summary>
    public bool AddExchange(string game, string question, string answer, int summarizeAfter, string model = "")
    {
        if (string.IsNullOrWhiteSpace(answer)) return false;
        lock (_gate)
        {
            var m = Get(game);
            m.Exchanges.Add(new MemoryExchange
            {
                Q = question.Trim(),
                A = answer.Length > 500 ? answer[..500] + "…" : answer.Trim(),
                At = DateTime.Now,
            });
            if (m.Exchanges.Count > MaxExchanges) m.Exchanges.RemoveAt(0);
            var chat = ActiveChat(m);
            chat.Messages.Add(new MemoryExchange
            {
                Id = NewId(),
                Q = question.Trim(),
                A = answer.Length > 4000 ? answer[..4000] + "…" : answer.Trim(),
                At = m.Exchanges[^1].At,
                Model = model,
            });
            if (chat.Messages.Count > MaxChatMessages) chat.Messages.RemoveAt(0);
            chat.Updated = DateTime.Now;
            if (chat.Title == "Yeni sohbet" && chat.Messages.Count == 1)
                chat.Title = question.Trim().Length > 48 ? question.Trim()[..48] + "…" : question.Trim();
            TrimChats(m);
            m.TotalQuestions++;
            Save(m);
            return summarizeAfter > 0 && m.Exchanges.Count >= summarizeAfter;
        }
    }

    /// <summary>Özetlenecek eski kayıtları verir (son <paramref name="keepRecent"/> tanesi hariç).</summary>
    public (string oldSummary, List<MemoryExchange> fold)? TakeForSummary(string game, int keepRecent)
    {
        lock (_gate)
        {
            var m = Get(game);
            int n = m.Exchanges.Count - Math.Max(0, keepRecent);
            if (n <= 0) return null;
            return (m.Summary, m.Exchanges.Take(n).Select(e => new MemoryExchange { Q = e.Q, A = e.A, At = e.At }).ToList());
        }
    }

    /// <summary>Yeni özeti yazar ve özete katılan kayıtları siler (zaman damgasıyla eşleştirerek).</summary>
    public void ApplySummary(string game, string newSummary, IReadOnlyCollection<MemoryExchange> folded)
    {
        lock (_gate)
        {
            var m = Get(game);
            var stamps = folded.Select(f => f.At).ToHashSet();
            m.Exchanges.RemoveAll(e => stamps.Contains(e.At));
            m.Summary = newSummary.Trim();
            Save(m);
        }
    }

    /// <summary>
    /// Oyunun adı sonradan düzeldiğinde (ör. pencere başlığından "Elden Ring") eski adla kaydedilmiş hafıza dosyasını yeni ada taşır.
    /// Yeni adın zaten bir dosyası varsa dokunmaz.
    /// </summary>
    public void Rename(string from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate)
        {
            try
            {
                var oldPath = PathFor(from);
                var newPath = PathFor(to);
                if (!File.Exists(oldPath) || File.Exists(newPath)) return;
                File.Move(oldPath, newPath);
                _cache.Remove(from);
                _cache.Remove(to);
                var m = Get(to);
                m.Game = to;
                Save(m);
                Log.Info($"Hafıza taşındı: '{from}' → '{to}'");
            }
            catch (Exception ex) { Log.Warn("Hafıza taşınamadı: " + ex.Message); }
        }
    }

    public void Clear(string game)
    {
        lock (_gate)
        {
            _cache.Remove(game);
            try { File.Delete(PathFor(game)); } catch { }
        }
    }

    /// <summary>Dosyası en son güncellenen oyunun adı (yoksa null).</summary>
    public string? MostRecentGame()
    {
        try
        {
            if (!Directory.Exists(Dir)) return null;
            var file = new DirectoryInfo(Dir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file == null) return null;
            lock (_gate) return Get(Path.GetFileNameWithoutExtension(file.Name)).Game;
        }
        catch { return null; }
    }

    public IReadOnlyList<string> KnownGames()
    {
        try
        {
            if (!Directory.Exists(Dir)) return Array.Empty<string>();
            return Directory.GetFiles(Dir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    // ------------------------------------------------------------------ İç

    private GameMemoryData Get(string game)
    {
        if (_cache.TryGetValue(game, out var m)) return m;
        m = Load(game) ?? new GameMemoryData { Game = game };
        _cache[game] = m;
        return m;
    }

    private static GameMemoryData? Load(string game)
    {
        var path = PathFor(game);
        try
        {
            if (!File.Exists(path)) return null;
            var m = JsonSerializer.Deserialize<GameMemoryData>(File.ReadAllText(path), Json);
            if (m == null) return null;
            m.Quests ??= new(); m.Regions ??= new(); m.Notes ??= new(); m.Exchanges ??= new();
            m.History ??= new();
            m.Chats ??= new();
            // v0.2 dosyaları: geçmiş henüz yoksa özetlenmemiş kayıtlardan başlat
            if (m.History.Count == 0 && m.Chats.Count == 0 && m.Exchanges.Count > 0)
                m.History.AddRange(m.Exchanges.Select(e => new MemoryExchange { Q = e.Q, A = e.A, At = e.At }));
            // v0.7 ve öncesi: tek sürekli geçmişi tek bir sohbete taşı
            if (m.History.Count > 0)
            {
                m.Chats.Add(new ChatSession
                {
                    Id = NewId(),
                    Title = "Önceki sorular",
                    Created = m.History[0].At,
                    Updated = m.History[^1].At,
                    Messages = m.History.ToList(),
                });
                m.ActiveChatId = m.Chats[^1].Id;
                m.History.Clear();
            }
            foreach (var c in m.Chats)
            {
                c.Messages ??= new();
                foreach (var e in c.Messages) if (string.IsNullOrEmpty(e.Id)) e.Id = NewId();
            }
            m.Summary ??= "";
            return m;
        }
        catch (Exception ex)
        {
            Log.Error("Hafıza okunamadı: " + path, ex);
            // Bozuk dosyayı yedekle, sıfırdan başla
            try { File.Move(path, path + ".bozuk-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
            return null;
        }
    }

    private static void Save(GameMemoryData m)
    {
        try
        {
            m.UpdatedAt = DateTime.Now;
            Directory.CreateDirectory(Dir);
            var path = PathFor(m.Game);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { Log.Error("Hafıza yazılamadı", ex); }
    }

    public static string PathFor(string game)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(game.Select(c => invalid.Contains(c) || c == ':' ? '_' : c).ToArray()).Trim();
        if (safe.Length == 0) safe = "Bilinmeyen";
        if (safe.Length > 80) safe = safe[..80];
        return Path.Combine(Dir, safe + ".json");
    }

    private static void PushUnique(List<string> list, string item, int max)
    {
        list.RemoveAll(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase));
        list.Add(item);
        while (list.Count > max) list.RemoveAt(0);
    }

    private static string? Str(JsonNode? n)
    {
        try
        {
            var s = n?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(s) || s.Length > 200) return null;
            return s;
        }
        catch { return null; }
    }

    private static bool Bool(JsonNode? n)
    {
        try { return n?.GetValue<bool>() == true; } catch { return false; }
    }
}
