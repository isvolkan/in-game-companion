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
    /// Kotası dolan (429) modeller bir süre atlanır: yanıttaki "retry in Ns" süresi kadar (yoksa 5 dk).
    /// Böylece her soruda önce dolu modele gidip vakit kaybedilmez.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _modelBlockedUntil = new(StringComparer.OrdinalIgnoreCase);

    private bool IsBlocked(string model) => _modelBlockedUntil.TryGetValue(model, out var t) && DateTime.UtcNow.Ticks < t;

    private void BlockModel(string model, string message)
    {
        double secs = 300;
        var m = Regex.Match(message ?? "", @"retry in ([0-9]+(?:\.[0-9]+)?)s", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            secs = Math.Clamp(d + 1, 5, 900);
        _modelBlockedUntil[model] = (DateTime.UtcNow + TimeSpan.FromSeconds(secs)).Ticks;
        Log.Warn($"{model} kotası dolu; {secs:F0} sn boyunca atlanacak");
    }
    private static readonly TimeSpan SearchBlockDuration = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Soruyu sorar. 429 gelirse önce web aramasız, sonra yedek modelle tekrar dener.
    /// 429/404 yanıtı başlıkta döndüğü için o ana kadar ekrana hiçbir metin akmamış olur.
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
        var model = s.Model;
        var fallback = string.IsNullOrWhiteSpace(s.FallbackModel) ? null : s.FallbackModel.Trim();
        bool search = s.UseWebSearch && DateTime.UtcNow.Ticks >= Interlocked.Read(ref _searchBlockedUntilTicks);
        var notices = new List<string>();
        if (s.UseWebSearch && !search) notices.Add("Web araması kotası dolu, aramasız cevaplandı.");
        bool sameModelRetried = false;
        if (fallback != null && !string.Equals(model, fallback, StringComparison.OrdinalIgnoreCase) && IsBlocked(model))
        {
            notices.Add($"{model} kotası dolu, {fallback} ile cevaplandı.");
            model = fallback;
        }

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
            catch (GeminiException ex) when (ex.Status >= 500 && !sameModelRetried)
            {
                // 503 "yoğun talep" genelde birkaç saniyelik: önce aynı modeli bir kez daha dene (yedek model daha zayıf)
                sameModelRetried = true;
                Log.Warn($"Gemini {ex.Status} ({model}): {ex.Message} → aynı model bir kez daha deneniyor");
                await Task.Delay(700, ct).ConfigureAwait(false);
                Retrying();
            }
            catch (GeminiException ex) when ((ex.Status == 429 || ex.Status == 404 || ex.Status >= 500)
                                             && fallback != null
                                             && !string.Equals(model, fallback, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Gemini {ex.Status} ({model}): {ex.Message} → yedek model {fallback}");
                if (ex.Status == 429) BlockModel(model, ex.Message);
                notices.Add($"{model} kullanılamadı ({ex.Status}), yedek model {fallback} ile cevaplandı.");
                model = fallback;
                Retrying();
            }
        }
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

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
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

    /// <summary>
    /// Kırpmalarda aranan nesnelerin sınırlayıcı kutusunu ister (tek istekte toplu). Her kırpma için
    /// [ymin,xmin,ymax,xmax] (0-1000, o kırpmaya göre) ya da bulunamadıysa null döner. 5xx/429'da bir kez tekrar dener,
    /// sonra yedek modele geçer.
    /// </summary>
    public async Task<double[]?[]> LocateAsync(IReadOnlyList<(string Label, byte[] Jpeg)> crops, CancellationToken ct)
    {
        var s = _settings();
        var key = s.ResolvedApiKey;
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("API anahtarı yok");

        var parts = new JsonArray
        {
            new JsonObject { ["text"] = "Aşağıdaki her görüntü, bir oyun ekranından alınmış küçük bir kırpma. Her kırpma için aranan nesneyi bul; nesne büyük olasılıkla kırpmanın merkezine yakındır." },
        };
        for (int i = 0; i < crops.Count; i++)
        {
            parts.Add(new JsonObject { ["text"] = $"Kırpma {i + 1} — aranan nesne: \"{crops[i].Label}\"" });
            parts.Add(InlineData("image/jpeg", crops[i].Jpeg));
        }
        parts.Add(new JsonObject
        {
            ["text"] = "Her kırpma için aranan nesnenin SIKI sınırlayıcı kutusunu ver (yalnızca nesnenin kendisi, çevresi değil). " +
                       "Sadece JSON dizisi döndür: [{\"i\":1,\"box_2d\":[ymin,xmin,ymax,xmax]},{\"i\":2,\"none\":true}]. " +
                       "Koordinatlar 0-1000 ölçeğinde ve İLGİLİ KIRPMAYA göre (x soldan sağa, y yukarıdan aşağı). " +
                       "Nesne o kırpmada yoksa ya da emin değilsen none:true yaz; tahmin etme.",
        });
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

        var models = new List<string>();
        foreach (var m in new[] { s.PointerRefineModel, s.Model, s.FallbackModel })
            if (!string.IsNullOrWhiteSpace(m) && !models.Exists(x => string.Equals(x, m.Trim(), StringComparison.OrdinalIgnoreCase))
                && !IsBlocked(m.Trim()))
                models.Add(m.Trim());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(14));
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
                if (resp.IsSuccessStatusCode) return ParseBoxes(json, crops.Count);
                last = new GeminiException((int)resp.StatusCode, ExtractError(json));
                if ((int)resp.StatusCode == 429) { BlockModel(model, last.Message); break; }   // kota: aynı modeli tekrar deneme
                if ((int)resp.StatusCode >= 500) { await Task.Delay(500, timeout.Token).ConfigureAwait(false); continue; }
                break; // 4xx: bu modelle olmaz, sıradakine geç
            }
        }
        throw last ?? new GeminiException(0, "Konum sorgusu başarısız");
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
            if (!string.IsNullOrWhiteSpace(m)) return m;
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
        429 when Message.Contains("limit: 0", StringComparison.OrdinalIgnoreCase)
            => "Bu model ücretsiz katmanda yok (limit 0). AI Studio'da faturalandırmayı aç ya da Model'i değiştir.",
        429 => "Kota/hız sınırı doldu. Biraz bekleyip tekrar dene (limitler: aistudio.google.com/rate-limit).",
        >= 500 => "Gemini sunucusu şu an yanıt vermiyor, tekrar dene.",
        _ => Short(Message),
    };

    private static string Short(string s) => s.Length > 160 ? s[..160] + "…" : s;
}
