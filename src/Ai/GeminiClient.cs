using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using InGameCompanion.Core;

namespace InGameCompanion.Ai;

internal sealed record AskRequest(
    string SystemInstruction,
    string TurnContext,
    byte[] FullJpeg,
    byte[]? FocusJpeg,
    byte[]? Wav,
    IReadOnlyList<ChatTurn> History,
    string? TypedQuestion = null);

internal sealed record AskResult(
    string Text,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> SearchQueries,
    int PromptTokens,
    int OutputTokens,
    int ThoughtTokens,
    long FirstTokenMs,
    long TotalMs,
    string Model,
    string? Notice = null);

/// <summary>
/// Gemini API istemcisi: görüntü + ses + metin tek istekte, Google Search grounding açık,
/// yanıt Server-Sent Events ile token token akar (streamGenerateContent?alt=sse).
/// </summary>
internal sealed class GeminiClient
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        EnableMultipleHttp2Connections = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // zaman aşımını CancellationToken yönetir
        DefaultRequestVersion = new Version(2, 0),
    };

    private readonly Func<Settings> _settings;

    public GeminiClient(Func<Settings> settings) => _settings = settings;

    /// <summary>İlk sorudaki TLS/HTTP2 el sıkışma gecikmesini önlemek için bağlantıyı önceden ısıtır.</summary>
    public async Task WarmUpAsync()
    {
        try
        {
            var s = _settings();
            if (string.IsNullOrEmpty(s.ResolvedApiKey)) return;
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + s.Model);
            req.Headers.Add("x-goog-api-key", s.ResolvedApiKey);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            Log.Info($"Isınma: {s.Model} → {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                Log.Warn("Model bilgisi alınamadı: " + await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        catch (Exception ex) { Log.Warn("Isınma başarısız: " + ex.Message); }
    }

    /// <summary>
    /// Web araması kota verirse (ücretsiz katmanda kotası 0) bir süre aramasız devam edilir;
    /// her soruda boşuna 429 turu atılmasın.
    /// </summary>
    private long _searchBlockedUntilTicks;

    /// <summary>
    /// Kotası dolan modeller atlanır: günlük kotada (PerDay) sıfırlanmaya kadar, dakikalıkta yanıttaki süre kadar.
    /// Böylece her soruda önce dolu modele gidip vakit kaybedilmez.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _modelBlockedUntil = new(StringComparer.OrdinalIgnoreCase);

    private bool IsBlocked(string model) => _modelBlockedUntil.TryGetValue(model, out var t) && DateTime.UtcNow.Ticks < t;

    private void BlockModel(string model, string message)
    {
        TimeSpan span;
        if (message.Contains("PerDay", StringComparison.OrdinalIgnoreCase))
            span = UntilQuotaReset();
        else
        {
            double secs = 300;
            var m = Regex.Match(message, @"retry in ([0-9]+(?:\.[0-9]+)?)s", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                secs = Math.Clamp(d + 1, 5, 900);
            span = TimeSpan.FromSeconds(secs);
        }
        _modelBlockedUntil[model] = (DateTime.UtcNow + span).Ticks;
        Log.Warn($"{model} kotası dolu; {(span.TotalMinutes < 2 ? span.TotalSeconds.ToString("F0") + " sn" : span.TotalMinutes.ToString("F0") + " dk")} boyunca atlanacak");
    }

    /// <summary>Günlük ücretsiz kota Pasifik saatiyle gece yarısı sıfırlanır.</summary>
    private static TimeSpan UntilQuotaReset()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
            var next = new DateTimeOffset(now.Date.AddDays(1), now.Offset);
            return Clamp(next - now + TimeSpan.FromMinutes(2));
        }
        catch { return TimeSpan.FromHours(6); }
        static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : (t > TimeSpan.FromHours(25) ? TimeSpan.FromHours(25) : t);
    }

    /// <summary>Denenecek modeller sırayla: Model, ModelChain…, FallbackModel (yinelenenler ve boşlar atılır).</summary>
    internal static List<string> BuildChain(Settings s)
    {
        var list = new List<string>();
        void Add(string? m)
        {
            m = m?.Trim();
            if (!string.IsNullOrEmpty(m) && !list.Exists(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase))) list.Add(m);
        }
        Add(s.Model);
        if (s.ModelChain != null) foreach (var m in s.ModelChain) Add(m);
        Add(s.FallbackModel);
        return list;
    }

    /// <summary>Zincirde <paramref name="idx"/>'ten sonraki ilk kotası dolmamış model (yoksa sonraki; hiç yoksa -1).</summary>
    private int NextIndex(List<string> chain, int idx)
    {
        for (int i = idx + 1; i < chain.Count; i++)
            if (!IsBlocked(chain[i])) return i;
        return idx + 1 < chain.Count ? idx + 1 : -1;
    }

    private static readonly TimeSpan SearchBlockDuration = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Soruyu sorar. Sırayla: web araması kotası doluysa aramasız; 5xx'te aynı model bir kez daha; 429/404/5xx'te zincirdeki
    /// sıradaki modele geçer. 429/404 yanıtı başlıkta döndüğü için o ana kadar ekrana hiçbir metin akmamış olur.
    /// </summary>
    /// <param name="onReset">
    /// Akış metin göndermeye başladıktan sonra koptuysa (ör. cevabın ortasında 503) ve yeniden denenecekse çağrılır:
    /// tüketici o ana kadar aldığı metni atmalıdır, yoksa yeni deneme metni sonuna eklenir.
    /// </param>
    public async Task<AskResult> AskStreamAsync(AskRequest r, Action<string> onText, CancellationToken ct, Action? onReset = null)
    {
        bool emitted = false;
        Action<string> tracked = t => { emitted = true; onText(t); };
        void Retrying()
        {
            if (!emitted) return;
            emitted = false;
            onReset?.Invoke();
        }

        var s = _settings();
        var chain = BuildChain(s);
        if (chain.Count == 0) chain.Add(s.Model);
        int idx = 0;
        while (idx < chain.Count - 1 && IsBlocked(chain[idx])) idx++;
        var model = chain[idx];
        bool search = s.UseWebSearch && DateTime.UtcNow.Ticks >= Interlocked.Read(ref _searchBlockedUntilTicks);
        var notices = new List<string>();
        if (s.UseWebSearch && !search) notices.Add("Web araması kotası dolu, aramasız cevaplandı.");
        if (idx > 0) notices.Add($"{chain[0]} kullanılamadı, {model} ile cevaplandı.");
        bool sameModelRetried = false;

        while (true)
        {
            try
            {
                var res = await AskOnceAsync(r, s, model, search, tracked, ct).ConfigureAwait(false);
                return res with { Notice = notices.Count == 0 ? null : string.Join(" ", notices) };
            }
            catch (GeminiException ex) when (ex.Status == 429 && search)
            {
                Log.Warn($"Gemini 429 (arama açık, {model}): {ex.Message} → aramasız tekrar deneniyor");
                Interlocked.Exchange(ref _searchBlockedUntilTicks, (DateTime.UtcNow + SearchBlockDuration).Ticks);
                search = false;
                notices.Add("Web araması kotası dolu, aramasız cevaplandı.");
                Retrying();
            }
            catch (GeminiException ex) when (ex.Status >= 500 && !sameModelRetried && NextIndex(chain, idx) < 0)
            {
                // Zincirde başka model kalmadıysa 503 "yoğun talep" için aynı modeli bir kez daha dene
                sameModelRetried = true;
                Log.Warn($"Gemini {ex.Status} ({model}): {ex.Message} → aynı model bir kez daha deneniyor");
                await Task.Delay(700, ct).ConfigureAwait(false);
                Retrying();
            }
            catch (GeminiException ex) when ((ex.Status == 429 || ex.Status == 404 || ex.Status >= 500) && NextIndex(chain, idx) >= 0)
            {
                if (ex.Status == 429) BlockModel(model, ex.Message);
                else if (ex.Status >= 500) _modelBlockedUntil[model] = (DateTime.UtcNow + TimeSpan.FromSeconds(45)).Ticks;   // kısa soğuma
                int next = NextIndex(chain, idx);
                Log.Warn($"Gemini {ex.Status} ({model}): {ShortMsg(ex.Message)} → sıradaki model {chain[next]}");
                idx = next;
                model = chain[idx];
                sameModelRetried = false;
                notices.Add($"{chain[0]} kullanılamadı ({ex.Status}), {model} ile cevaplandı.");
                Retrying();
            }
        }
    }

    private static string ShortMsg(string m)
    {
        var line = m.Split('\n')[0];
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    private async Task<AskResult> AskOnceAsync(AskRequest r, Settings s, string model, bool search,
                                               Action<string> onText, CancellationToken ct)
    {
        var key = s.ResolvedApiKey;
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Gemini API anahtarı yok. settings.json → ApiKey ya da GEMINI_API_KEY ortam değişkeni.");

        var body = BuildBody(r, s, search);
        var url = $"{BaseUrl}{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, s.RequestTimeoutSeconds)));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // Yoğun modeller hata vermeden önce 10-25 sn bekletebiliyor: başlık çok gecikirse pes edip sıradaki modele geç
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        headerCts.CancelAfter(TimeSpan.FromSeconds(14));
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (headerCts.IsCancellationRequested && !timeout.IsCancellationRequested)
        {
            throw new GeminiException(503, $"{model} yanıt vermedi (14 sn)");
        }
        using var respScope = resp;
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            throw new GeminiException((int)resp.StatusCode, ExtractError(err));
        }

        var text = new StringBuilder();
        var sources = new List<string>();
        var queries = new List<string>();
        int promptTok = 0, outTok = 0, thoughtTok = 0;
        long firstTokenMs = -1;
        string? finishReason = null;

        await using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var eventData = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line == null) break;
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                eventData.Append(line.AsSpan(5).TrimStart());
                continue;
            }
            if (line.Length != 0 || eventData.Length == 0) continue;

            // Olay tamamlandı
            var json = eventData.ToString();
            eventData.Clear();
            JsonNode? node;
            try { node = JsonNode.Parse(json); } catch { continue; }
            if (node == null) continue;

            if (node["error"] is JsonNode errNode)
                throw new GeminiException(errNode["code"]?.GetValue<int>() ?? 0, errNode["message"]?.GetValue<string>() ?? json);

            var cand = node["candidates"]?[0];
            var parts = cand?["content"]?["parts"]?.AsArray();
            if (parts != null)
            {
                foreach (var p in parts)
                {
                    if (p?["thought"]?.GetValue<bool>() == true) continue; // düşünme özetlerini gösterme
                    var t = p?["text"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(t)) continue;
                    if (firstTokenMs < 0) firstTokenMs = sw.ElapsedMilliseconds;
                    text.Append(t);
                    onText(t);
                }
            }

            finishReason = cand?["finishReason"]?.GetValue<string>() ?? finishReason;

            if (cand?["groundingMetadata"] is JsonNode gm)
            {
                if (gm["groundingChunks"] is JsonArray chunks)
                    foreach (var c in chunks)
                    {
                        var title = c?["web"]?["title"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(title) && !sources.Contains(title)) sources.Add(title);
                    }
                if (gm["webSearchQueries"] is JsonArray qs)
                    foreach (var q in qs)
                    {
                        var qq = q?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(qq) && !queries.Contains(qq)) queries.Add(qq);
                    }
            }

            if (node["usageMetadata"] is JsonNode u)
            {
                promptTok = u["promptTokenCount"]?.GetValue<int>() ?? promptTok;
                outTok = u["candidatesTokenCount"]?.GetValue<int>() ?? outTok;
                thoughtTok = u["thoughtsTokenCount"]?.GetValue<int>() ?? thoughtTok;
            }
        }

        if (text.Length == 0 && finishReason is not null and not "STOP")
            throw new GeminiException(0, $"Model yanıt üretmedi (finishReason={finishReason}).");

        return new AskResult(text.ToString(), sources, queries, promptTok, outTok, thoughtTok, firstTokenMs, sw.ElapsedMilliseconds, model);
    }

    private static bool IsLite(string model) => model.Contains("lite", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Kırpmalarda aranan nesnelerin sınırlayıcı kutusunu ister (tek istekte toplu). Her kırpma için
    /// [ymin,xmin,ymax,xmax] (0-1000, o kırpmaya göre) ya da bulunamadıysa null döner.
    /// </summary>
    public Task<double[]?[]> LocateAsync(IReadOnlyList<(string Label, string Desc, byte[] Jpeg)> crops, string context, CancellationToken ct)
    {
        var parts = new JsonArray
        {
            new JsonObject
            {
                ["text"] = "Aşağıdaki her görüntü, bir oyun ekranından alınmış küçük bir kırpma. Her kırpma için aranan nesneyi bul; nesne büyük olasılıkla kırpmanın merkezine yakındır." +
                           (string.IsNullOrWhiteSpace(context) ? "" : "\nBağlam — " + context.Trim()),
            },
        };
        for (int i = 0; i < crops.Count; i++)
        {
            parts.Add(new JsonObject
            {
                ["text"] = $"Kırpma {i + 1} — aranan nesne: \"{crops[i].Label}\"" +
                           (string.IsNullOrWhiteSpace(crops[i].Desc) ? "" : $" — görünüşü/yeri: {crops[i].Desc}"),
            });
            parts.Add(InlineData("image/jpeg", crops[i].Jpeg));
        }
        parts.Add(new JsonObject
        {
            ["text"] = "Her kırpma için aranan nesnenin SIKI sınırlayıcı kutusunu ver (yalnızca nesnenin kendisi, çevresi değil). " +
                       "Sadece JSON dizisi döndür: [{\"i\":1,\"box_2d\":[ymin,xmin,ymax,xmax]},{\"i\":2,\"none\":true}]. " +
                       "Koordinatlar 0-1000 ölçeğinde ve İLGİLİ KIRPMAYA göre (x soldan sağa, y yukarıdan aşağı). " +
                       "Kırpmada birbirine benzeyen birçok öğe olabilir: tarife ve bağlama EN ÇOK uyanı seç. " +
                       "Nesne o kırpmada yoksa ya da tarife uyan öğeyi kesin ayırt edemiyorsan none:true yaz; tahmin etme.",
        });
        return LocateCoreAsync(parts, crops.Count, strongOnly: false, ct);
    }

    /// <summary>
    /// Hedefleri TAM karede yeniden konumlandırır (güçlü model gerekir; hafif model atlanır, hiçbiri yoksa hepsi null).
    /// Hafif modelin yoğun ekranlarda (yetenek ağacı gibi) yanlış öğeyi göstermesini düzeltmek için.
    /// </summary>
    public Task<double[]?[]> LocateOnFrameAsync(byte[] fullJpeg, IReadOnlyList<(string Label, string Desc)> targets, string context, CancellationToken ct,
                                                bool allowLite = false)
    {
        var parts = new JsonArray
        {
            new JsonObject
            {
                ["text"] = "Aşağıdaki görüntü bir oyun ekranının tamamı. Aşağıdaki her hedefi bu ekranda bul." +
                           (string.IsNullOrWhiteSpace(context) ? "" : "\nBağlam — " + context.Trim()),
            },
            InlineData("image/jpeg", fullJpeg),
        };
        var sb = new StringBuilder();
        for (int i = 0; i < targets.Count; i++)
        {
            sb.Append($"Hedef {i + 1} — aranan: \"{targets[i].Label}\"");
            if (!string.IsNullOrWhiteSpace(targets[i].Desc)) sb.Append($" — görünüşü/yeri: {targets[i].Desc}");
            sb.Append('\n');
        }
        sb.Append("Her hedef için SIKI sınırlayıcı kutuyu ver (yalnızca hedefin kendisi). Sadece JSON dizisi döndür: " +
                  "[{\"i\":1,\"box_2d\":[ymin,xmin,ymax,xmax]},{\"i\":2,\"none\":true}]. " +
                  "Koordinatlar 0-1000 ölçeğinde ve TÜM görüntüye göre (x soldan sağa, y yukarıdan aşağı). " +
                  "Birbirine benzeyen birçok öğe olabilir: tarife, bağlama, seçili/vurgulu olma ve komşuluklara EN ÇOK uyanı seç. " +
                  "Hangisi olduğunu kesin ayırt edemiyorsan none:true yaz; tahmin etme.");
        parts.Add(new JsonObject { ["text"] = sb.ToString() });
        return LocateCoreAsync(parts, targets.Count, strongOnly: !allowLite, ct);
    }

    private async Task<double[]?[]> LocateCoreAsync(JsonArray parts, int count, bool strongOnly, CancellationToken ct)
    {
        var s = _settings();
        var key = s.ResolvedApiKey;
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("API anahtarı yok");

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = new JsonObject
            {
                ["maxOutputTokens"] = 600,
                ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "low" },
            },
        };
        var payload = body.ToJsonString();

        var res = await SendViaChainAsync(payload, strongOnly, 16, ct, s.PointerRefineModel).ConfigureAwait(false);
        if (res == null) return new double[count][];
        Log.Info($"  Konum sorgusu: {res.Value.Model}");
        return ParseBoxes(res.Value.Json, count);
    }

    /// <summary>
    /// Yükü sırayla uygun modellere gönderir: (tercih edilen) → zincirdeki güçlü modeller → (strongOnly değilse) hafif modeller.
    /// Kotası dolanlar atlanır. İlk başarılı yanıtı ve modelini döner; hiç uygun model yoksa null, hepsi başarısızsa son hata.
    /// </summary>
    private async Task<(string Json, string Model)?> SendViaChainAsync(string payload, bool strongOnly, int timeoutSeconds,
                                                                       CancellationToken ct, string? preferred = null)
    {
        var s = _settings();
        var key = s.ResolvedApiKey;
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("API anahtarı yok");

        var models = new List<string>();
        void Add(string? m)
        {
            m = m?.Trim();
            if (string.IsNullOrEmpty(m) || (strongOnly && IsLite(m)) || IsBlocked(m)) return;
            if (!models.Exists(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase))) models.Add(m);
        }
        Add(preferred);
        var chain = BuildChain(s);
        foreach (var m in chain) if (!IsLite(m)) Add(m);
        foreach (var m in chain) if (IsLite(m)) Add(m);
        if (models.Count == 0) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        GeminiException? last = null;
        foreach (var model in models)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{Uri.EscapeDataString(model)}:generateContent")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("x-goog-api-key", key);
                using var resp = await Http.SendAsync(req, timeout.Token).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return (json, model);
                last = new GeminiException((int)resp.StatusCode, ExtractError(json));
                if ((int)resp.StatusCode == 429) { BlockModel(model, last.Message); break; }   // kota: aynı modeli tekrar deneme
                if ((int)resp.StatusCode >= 500) { await Task.Delay(500, timeout.Token).ConfigureAwait(false); continue; }
                break; // 4xx: bu modelle olmaz, sıradakine geç
            }
        }
        throw last ?? new GeminiException(0, "Model sorgusu başarısız");
    }

    /// <summary>
    /// Yeni bir oyun için profil ister (güçlü model öncelikli). Yanıt: ilk satır "OYUN: &lt;ad&gt;" (oyun değilse "OYUN: YOK"),
    /// sonrası spoilersiz profil metni.
    /// </summary>
    public async Task<string> GenerateProfileAsync(string processName, string windowTitle, byte[] frameJpeg, CancellationToken ct)
    {
        var instruction = $"""
Bir oyun yardımcı uygulaması, ön plandaki pencereyi aşağıdaki bilgi ve ekran görüntüsüyle sana gösteriyor.
İşlem adı: {processName}.exe
Pencere başlığı: "{windowTitle}"

Görev:
1) Bu bir VİDEO OYUNU mu? (Tarayıcı, editör, sohbet, medya oynatıcı, masaüstü vb. ise oyun DEĞİLDİR.)
2) Oyunsa tam adını belirle (işlem adı, pencere başlığı ve ekrandaki arayüzden).
3) O oyun için spoilersiz bir profil yaz.

ÇIKTI BİÇİMİ (kesin):
İlk satır: "OYUN: <oyunun tam adı>"  — oyun değilse yalnızca "OYUN: YOK" yaz ve dur.
Sonraki satırlardan itibaren profil (düz metin/kısa Markdown, en fazla ~230 kelime), şu başlıklarla:
- Oyun: ad, yapımcı, çıkış yılı, tür (yalnızca emin olduğun kadarı)
- Dünya ve oynanış: spoilersiz, 2-3 cümle
- Temel mekanikler: envanter, harita, yetenek/karakter gelişimi, üretim, savaş gibi soru sorulabilecek sistemler
- Arayüz dili ve terimler: ekran görüntüsündeki arayüz hangi dildeyse o dilde menü/terim adlarını yaz; oyuncu bunları ekranda aynen görür
- Karıştırılmaması gerekenler: benzer adlı başka oyun/seri varsa uyar
KURALLAR: Yalnızca EMİN olduğun bilgiyi yaz. Oyunu tanımıyorsan ya da yeni çıkmışsa "Bilgi sınırlı" de ve yalnızca ekranda gördüklerini yaz. Hikâye olayları, karakter kaderleri, sürprizler, bölüm sonları YASAK. Uydurma yok.
""";
        var parts = new JsonArray
        {
            InlineData("image/jpeg", frameJpeg),
            new JsonObject { ["text"] = instruction },
        };
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = new JsonObject
            {
                ["maxOutputTokens"] = 1500,
                ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "low" },
            },
        };
        var res = await SendViaChainAsync(body.ToJsonString(), strongOnly: false, timeoutSeconds: 60, ct).ConfigureAwait(false)
                  ?? throw new GeminiException(429, "Kullanılabilir model yok (kotalar dolu)");
        Log.Info($"  Oyun profili modeli: {res.Model}");
        var partsOut = JsonNode.Parse(res.Json)?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        var sb = new StringBuilder();
        if (partsOut != null)
            foreach (var p in partsOut)
                if (p?["thought"]?.GetValue<bool>() != true) sb.Append(p?["text"]?.GetValue<string>());
        return sb.ToString();
    }

    private static double[]?[] ParseBoxes(string responseJson, int count)
    {
        var result = new double[count][];
        var parts = JsonNode.Parse(responseJson)?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        var sb = new StringBuilder();
        if (parts != null)
            foreach (var p in parts)
                if (p?["thought"]?.GetValue<bool>() != true) sb.Append(p?["text"]?.GetValue<string>());
        var text = sb.ToString();
        int a = text.IndexOf('['), b = text.LastIndexOf(']');
        if (a < 0 || b <= a) return result!;
        try
        {
            if (JsonNode.Parse(text[a..(b + 1)]) is not JsonArray arr) return result!;
            foreach (var item in arr)
            {
                if (item is not JsonObject o) continue;
                int i = (int)(o["i"]?.GetValue<double>() ?? 0) - 1;
                if (i < 0 || i >= count || o["box_2d"] is not JsonArray box || box.Count != 4) continue;
                var v = new double[4];
                for (int k = 0; k < 4; k++) v[k] = box[k]!.GetValue<double>();
                if (v[2] > v[0] && v[3] > v[1] && v.All(x => x >= 0 && x <= 1000)) result[i] = v;
            }
        }
        catch { }
        return result!;
    }

    /// <summary>Tek seferlik, akışsız metin üretimi (hafıza özetleme gibi arka plan işleri için).</summary>
    public async Task<string> GenerateTextAsync(string model, string system, string user, CancellationToken ct)
    {
        var s = _settings();
        var key = s.ResolvedApiKey;
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("API anahtarı yok");
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = user }),
            }),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 2048 },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var resp = await Http.SendAsync(req, timeout.Token).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new GeminiException((int)resp.StatusCode, ExtractError(json));
        var parts = JsonNode.Parse(json)?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        var sb = new StringBuilder();
        if (parts != null)
            foreach (var p in parts)
            {
                if (p?["thought"]?.GetValue<bool>() == true) continue;
                sb.Append(p?["text"]?.GetValue<string>());
            }
        return sb.ToString().Trim();
    }

    private static JsonObject BuildBody(AskRequest r, Settings s, bool search)
    {
        var contents = new JsonArray();

        foreach (var h in r.History)
        {
            contents.Add(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = $"(Önceki soru) {h.Question}" }),
            });
            contents.Add(new JsonObject
            {
                ["role"] = "model",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = $"S: {h.Question}\n{h.Answer}" }),
            });
        }

        var parts = new JsonArray
        {
            new JsonObject { ["text"] = r.TurnContext },
            InlineData("image/jpeg", r.FullJpeg),
        };
        if (r.FocusJpeg != null) parts.Add(InlineData("image/jpeg", r.FocusJpeg));
        // Son kullanıcı turu boş olmayan bir metinle bitmeli
        if (r.Wav != null)
        {
            parts.Add(InlineData("audio/wav", r.Wav));
            parts.Add(new JsonObject { ["text"] = "Yukarıdaki ses kaydı oyuncunun sorusu. Kurallara uyarak cevapla." });
        }
        else
        {
            parts.Add(new JsonObject { ["text"] = "Oyuncunun yazılı sorusu: " + (r.TypedQuestion ?? "") + "\nKurallara uyarak cevapla." });
        }

        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });

        var gen = new JsonObject { ["maxOutputTokens"] = s.MaxOutputTokens };
        if (!string.IsNullOrWhiteSpace(s.ThinkingLevel))
            gen["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = s.ThinkingLevel.Trim().ToLowerInvariant() };

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = r.SystemInstruction }),
            },
            ["contents"] = contents,
            ["generationConfig"] = gen,
        };
        if (search)
            body["tools"] = new JsonArray(new JsonObject { ["googleSearch"] = new JsonObject() });
        return body;
    }

    private static JsonObject InlineData(string mime, byte[] data) => new()
    {
        ["inlineData"] = new JsonObject
        {
            ["mimeType"] = mime,
            ["data"] = Convert.ToBase64String(data),
        },
    };

    private static string ExtractError(string body)
    {
        try
        {
            var n = JsonNode.Parse(body.TrimStart('[').TrimEnd(']'));
            var m = n?["error"]?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(m))
            {
                // Kota ihlalinin kimliği (ör. ...PerDay...) günlük/dakikalık ayrımı için mesaja eklenir
                if (n?["error"]?["details"] is JsonArray details)
                    foreach (var d in details)
                        if (d?["@type"]?.GetValue<string>()?.EndsWith("QuotaFailure", StringComparison.Ordinal) == true
                            && d["violations"]?[0]?["quotaId"]?.GetValue<string>() is { Length: > 0 } qid)
                            m += " [" + qid + "]";
                return m;
            }
        }
        catch { }
        return body.Length > 400 ? body[..400] : body;
    }
}

internal sealed class GeminiException : Exception
{
    public int Status { get; }
    public GeminiException(int status, string message) : base(message) => Status = status;

    public string UserMessage => Status switch
    {
        400 when Message.Contains("API key", StringComparison.OrdinalIgnoreCase) => "API anahtarı geçersiz.",
        400 => "İstek reddedildi: " + Short(Message),
        401 or 403 => "API anahtarı geçersiz ya da yetkisiz.",
        404 => "Model bulunamadı — settings.json'daki Model adını kontrol et.",
        429 when Message.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
            => "Bu modelin günlük ücretsiz kotası doldu. Ayarlar'dan başka model seç ya da yarın tekrar dene.",
        429 when Message.Contains("limit: 0", StringComparison.OrdinalIgnoreCase)
            => "Bu model ücretsiz katmanda yok (limit 0). AI Studio'da faturalandırmayı aç ya da Model'i değiştir.",
        429 => "Kota/hız sınırı doldu. Biraz bekleyip tekrar dene (limitler: aistudio.google.com/rate-limit).",
        >= 500 => "Gemini sunucusu şu an yanıt vermiyor, tekrar dene.",
        _ => Short(Message),
    };

    private static string Short(string s) => s.Length > 160 ? s[..160] + "…" : s;
}
