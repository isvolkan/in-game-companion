using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InGameCompanion.Core;

internal sealed class CaptureSettings
{
    /// <summary>Tam karenin uzun kenarı en fazla bu kadar piksele küçültülür.</summary>
    public int MaxLongEdge { get; set; } = 1600;
    public int JpegQuality { get; set; } = 80;
    /// <summary>İmleç / ekran merkezi çevresinden alınan tam çözünürlüklü kırpmanın kenarı (px). 0 = kapalı.</summary>
    public int FocusCropSize { get; set; } = 900;
    /// <summary>Hata ayıklama için gönderilen görüntüleri logs/captures altına kaydet.</summary>
    public bool SaveDebugCaptures { get; set; } = false;
}

internal sealed class OverlaySettings
{
    public int Width { get; set; } = 470;
    public int MarginTop { get; set; } = 36;
    public int MarginRight { get; set; } = 36;
    public float FontSize { get; set; } = 16f;
    public string FontFamily { get; set; } = "Segoe UI";
    public double BackgroundOpacity { get; set; } = 0.82;
    public string AccentColor { get; set; } = "#D2463C";
    /// <summary>Daktilo hızı (karakter/sn). Gelen metin birikirse otomatik hızlanır.</summary>
    public int CharsPerSecond { get; set; } = 95;
    public double MinVisibleSeconds { get; set; } = 10;
    public double MaxVisibleSeconds { get; set; } = 28;
    /// <summary>Okuma hızı (kelime/sn) — ekranda kalma süresi buna göre hesaplanır.</summary>
    public double ReadingWordsPerSecond { get; set; } = 3.2;
    /// <summary>Ekran yüksekliğinin en fazla bu oranı kadar uzayabilir.</summary>
    public double MaxHeightRatio { get; set; } = 0.6;
}

internal sealed class GameEntry
{
    public string Name { get; set; } = "";
    /// <summary>games/ klasöründeki oyun profili (isteğe bağlı).</summary>
    public string? Profile { get; set; }
    /// <summary>Bu oyundaki ilerlemen (isteğe bağlı). Boşsa genel ProgressNote + otomatik takip kullanılır.</summary>
    public string? ProgressNote { get; set; }
}

internal sealed class MemorySettings
{
    /// <summary>Oyun başına kalıcı hafıza (memory/&lt;Oyun&gt;.json).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Bekleyen soru-cevap sayısı buna ulaşınca eskiler arka planda özetlenir.</summary>
    public int SummarizeAfter { get; set; } = 12;
    /// <summary>Özetlemeden sonra ham hâliyle tutulacak son soru-cevap sayısı.</summary>
    public int KeepRecent { get; set; } = 4;
    /// <summary>Özetleme için kullanılan (ucuz, hızlı) model.</summary>
    public string SummaryModel { get; set; } = "gemini-3.5-flash-lite";
}

internal sealed class Settings
{
    /// <summary>Boşsa GEMINI_API_KEY ortam değişkeni kullanılır.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.8-flash";
    /// <summary>Ana model 429/404 verirse istek bu modelle tekrarlanır. "" = kapalı.</summary>
    public string FallbackModel { get; set; } = "gemini-3.5-flash-lite";
    /// <summary>low | medium | high | "" (gönderme). Gecikme için low önerilir.</summary>
    public string ThinkingLevel { get; set; } = "low";
    public bool UseWebSearch { get; set; } = true;
    /// <summary>Konuşurken söylenenler kutuda canlı yazı olarak görünür (Gemini Live API).</summary>
    public bool LiveTranscription { get; set; } = true;
    public string LiveTranscriptionModel { get; set; } = "gemini-3.5-transcribe-live";
    public int MaxOutputTokens { get; set; } = 4096;
    public int RequestTimeoutSeconds { get; set; } = 45;

    /// <summary>XButton2 (Mouse 5) | XButton1 (Mouse 4) | Middle</summary>
    public string Hotkey { get; set; } = "XButton2";
    /// <summary>true: tuş oyuna iletilmez (oyunda başka işe atanmışsa çakışmaz).</summary>
    public bool SwallowHotkey { get; set; } = true;
    /// <summary>Bundan kısa basışlar "kapat" dokunuşu sayılır.</summary>
    public int MinHoldMs { get; set; } = 350;
    /// <summary>Tuş bırakıldıktan sonra son hecenin kesilmemesi için ek kayıt süresi.</summary>
    public int TailRecordMs { get; set; } = 180;
    /// <summary>-1 = Windows varsayılan mikrofonu, 0..n = aygıt sırası.</summary>
    public int MicDevice { get; set; } = -1;

    /// <summary>Strict | Mild</summary>
    public string SpoilerLevel { get; set; } = "Strict";
    /// <summary>Genel ilerleme notu (oyuna özel not Games[...].ProgressNote içinde). Spoiler sınırını belirler.</summary>
    public string ProgressNote { get; set; } = "";
    public string AnswerLanguage { get; set; } = "Türkçe";

    public int HistoryTurns { get; set; } = 4;
    public int HistoryMinutes { get; set; } = 20;

    /// <summary>true: sadece Games listesindeki oyunlarda çalışır.</summary>
    public bool ActiveOnlyInKnownGames { get; set; } = false;

    public MemorySettings Memory { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();

    /// <summary>Anahtar: işlem adı (.exe olmadan), büyük/küçük harf duyarsız.</summary>
    public Dictionary<string, GameEntry> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CrimsonDesert"] = new GameEntry { Name = "Crimson Desert", Profile = "CrimsonDesert.md" },
        ["witcher3"] = new GameEntry { Name = "The Witcher 3: Wild Hunt" },
        ["bg3"] = new GameEntry { Name = "Baldur's Gate 3" },
        ["bg3_dx11"] = new GameEntry { Name = "Baldur's Gate 3" },
        ["eldenring"] = new GameEntry { Name = "Elden Ring" },
        ["Cyberpunk2077"] = new GameEntry { Name = "Cyberpunk 2077" },
    };

    // ------------------------------------------------------------------

    [JsonIgnore] public string ResolvedApiKey =>
        !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim()
        : (Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "").Trim();

    public static string BaseDir => AppContext.BaseDirectory;
    public static string FilePath => Path.Combine(BaseDir, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Settings Load()
    {
        if (!File.Exists(FilePath))
        {
            var s = new Settings();
            s.Save();
            return s;
        }
        var text = File.ReadAllText(FilePath);
        var loaded = JsonSerializer.Deserialize<Settings>(text, Json) ?? new Settings();
        // Sözlüğü büyük/küçük harf duyarsız yap
        loaded.Games = new Dictionary<string, GameEntry>(loaded.Games ?? new(), StringComparer.OrdinalIgnoreCase);
        loaded.Capture ??= new CaptureSettings();
        loaded.Overlay ??= new OverlaySettings();
        loaded.Memory ??= new MemorySettings();
        // Yeni sürümde eklenen ayarlar dosyada görünsün
        if (!text.Contains("\"Memory\"", StringComparison.OrdinalIgnoreCase) ||
            !text.Contains("\"FallbackModel\"", StringComparison.OrdinalIgnoreCase) ||
            !text.Contains("\"LiveTranscription\"", StringComparison.OrdinalIgnoreCase))
        {
            try { loaded.Save(); } catch { }
        }
        return loaded;
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
}
