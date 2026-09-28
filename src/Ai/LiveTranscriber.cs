using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using InGameCompanion.Core;

namespace InGameCompanion.Ai;

/// <summary>
/// Gemini Live API (WebSocket) ile konuşurken canlı yazıya dökme. Sadece ekranda göstermek içindir;
/// asıl soru yine WAV olarak ana isteğe gider. Hata olursa sessizce devre dışı kalır, akışı bozmaz.
/// Sunucu mesajları: serverContent.interimInputTranscription.text (birikimli ara metin),
/// serverContent.inputTranscription.text (son metin), serverContent.generationComplete.
/// </summary>
internal sealed class LiveTranscriber
{
    private const string Url = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    private const int MaxChunkBytes = 16000 * 2 / 10; // en fazla 100 ms'lik paket

    private readonly string _apiKey;
    private readonly string _model;
    private readonly Action<string> _onText;
    private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string?> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _lastText;
    private readonly int _startDelayMs;
    private Task? _run;

    /// <param name="startDelayMs">Bağlantıyı bu kadar geciktir: kısa dokunuşlarda (iptal) hiç bağlanılmaz. Ses kanalda bekler.</param>
    public LiveTranscriber(string apiKey, string model, Action<string> onText, int startDelayMs = 0)
    {
        _startDelayMs = startDelayMs;
        _apiKey = apiKey;
        _model = model;
        _onText = onText;
    }

    public void Start() => _run = Task.Run(RunAsync);

    /// <summary>Mikrofon iş parçacığından çağrılır; bloklamaz.</summary>
    public void Push(byte[] pcm) => _audio.Writer.TryWrite(pcm);

    /// <summary>Ses bitti: son metni bekler (en fazla <paramref name="timeoutMs"/>), sonra bağlantıyı kapatır.</summary>
    public async Task<string?> FinishAsync(int timeoutMs = 1500)
    {
        _audio.Writer.TryComplete();
        var done = await Task.WhenAny(_final.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        var text = done == _final.Task ? _final.Task.Result : _lastText;
        _cts.Cancel();
        return text;
    }

    public void Cancel()
    {
        _audio.Writer.TryComplete();
        _cts.Cancel();
        _final.TrySetResult(null);
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        using var ws = new ClientWebSocket();
        try
        {
            if (_startDelayMs > 0) await Task.Delay(_startDelayMs, ct).ConfigureAwait(false);
            ws.Options.SetRequestHeader("x-goog-api-key", _apiKey);
            await ws.ConnectAsync(new Uri(Url), ct).ConfigureAwait(false);

            var setup = new JsonObject { ["setup"] = new JsonObject { ["model"] = "models/" + _model } };
            await SendAsync(ws, setup, ct).ConfigureAwait(false);
            var first = await ReceiveAsync(ws, ct).ConfigureAwait(false);
            if (first?["setupComplete"] == null)
                throw new InvalidOperationException("Kurulum yanıtı alınamadı: " + Short(first?.ToJsonString() ?? ws.CloseStatusDescription ?? "?"));

            var receiver = ReceiveLoopAsync(ws, ct);

            // Ses gönderimi: kanalda biriken parçaları birleştirip ~100 ms'lik paketlerle yolla
            var reader = _audio.Reader;
            var buf = new MemoryStream();
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                buf.SetLength(0);
                while (buf.Length < MaxChunkBytes && reader.TryRead(out var chunk)) buf.Write(chunk, 0, chunk.Length);
                if (buf.Length == 0) continue;
                var msg = new JsonObject
                {
                    ["realtimeInput"] = new JsonObject
                    {
                        ["audio"] = new JsonObject
                        {
                            ["mimeType"] = "audio/pcm;rate=16000",
                            ["data"] = Convert.ToBase64String(buf.GetBuffer(), 0, (int)buf.Length),
                        },
                    },
                };
                await SendAsync(ws, msg, ct).ConfigureAwait(false);
            }

            await SendAsync(ws, new JsonObject { ["realtimeInput"] = new JsonObject { ["audioStreamEnd"] = true } }, ct).ConfigureAwait(false);
            await receiver.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warn($"Canlı yazı kullanılamadı ({_model}): {ex.Message}"
                     + (ws.CloseStatusDescription is { Length: > 0 } d ? " — " + d : ""));
        }
        finally
        {
            _final.TrySetResult(_lastText);
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(500);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token).ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var node = await ReceiveAsync(ws, ct).ConfigureAwait(false);
            if (node == null) break; // bağlantı kapandı
            var sc = node["serverContent"];
            if (sc == null) continue;

            if (sc["interimInputTranscription"]?["text"]?.GetValue<string>() is { Length: > 0 } interim)
            {
                _lastText = interim;
                _onText(interim);
            }
            if (sc["inputTranscription"]?["text"]?.GetValue<string>() is { Length: > 0 } fin)
            {
                _lastText = fin;
                _onText(fin);
                _final.TrySetResult(fin);
            }
            if (sc["generationComplete"]?.GetValue<bool>() == true)
            {
                _final.TrySetResult(_lastText);
                break;
            }
        }
    }

    private static Task SendAsync(ClientWebSocket ws, JsonNode msg, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);

    /// <summary>Bir mesajın tamamını okur (sunucu metni ikili çerçeveyle de yollayabiliyor). Kapanışta null.</summary>
    private static async Task<JsonNode?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        try { return JsonNode.Parse(ms.ToArray()); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
