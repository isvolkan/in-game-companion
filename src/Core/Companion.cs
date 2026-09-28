using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InGameCompanion.Ai;
using InGameCompanion.Media;
using InGameCompanion.Native;
using InGameCompanion.Ui;

namespace InGameCompanion.Core;

/// <summary>
/// Ana akış:
///  bas  → ön plandaki oyunu tespit et, mikrofonu aç, ekranı arka planda yakala (+ JPEG'e kodla)
///  bırak→ kısa dokunuşsa kutuyu kapat; değilse sesi kapat, yakalamayı bekle, Gemini'ye gönder,
///         akan metni HUD'a daktilo efektiyle bas.
/// Yeni bir basış, süren isteği iptal eder.
/// </summary>
internal sealed class Companion
{
    private readonly Func<Settings> _settings;
    private readonly OverlayWindow _overlay;
    private readonly GameDetector _detector;
    private readonly GeminiClient _gemini;
    private readonly GameMemoryStore _memory;
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _summarizing = new();

    /// <summary>En son soru sorulan oyun (tepsi menüsündeki "hafızayı sil" için).</summary>
    public string? LastGameName { get; private set; }
    public GameMemoryStore Memory => _memory;

    // Geçerli basış
    private int _session;
    private long _pressTicks;
    private MicRecorder? _recorder;
    private Task<CaptureResult>? _capture;
    private GameContext? _game;
    private IntPtr _monitor;
    private CancellationTokenSource? _cts;

    public Companion(Func<Settings> settings, OverlayWindow overlay, GameDetector detector, GeminiClient gemini, GameMemoryStore memory)
    {
        _memory = memory;
        _settings = settings;
        _overlay = overlay;
        _detector = detector;
        _gemini = gemini;
    }


    /// <summary>Hook iş parçacığında, basış anında çağrılır — hızlı olmalı.</summary>
    public bool ShouldHandle()
    {
        var s = _settings();
        return !s.ActiveOnlyInKnownGames || _detector.IsKnownGameForeground();
    }

    public void OnPressed()
    {
        var s = _settings();
        int id;
        lock (_gate)
        {
            id = ++_session;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _recorder?.Cancel();
            _recorder = null;
            _pressTicks = Stopwatch.GetTimestamp();
        }

        var fg = Win32.GetForegroundWindow();
        var game = _detector.Detect(fg);
        _game = game;
        _monitor = Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST);

        // Ekranı hemen yakala (oyuncu konuşurken kodlama biter)
        var capCfg = s.Capture;
        _capture = Task.Run(() => ScreenCapture.Capture(fg, capCfg));

        // Mikrofonu aç
        try
        {
            var rec = new MicRecorder(s.MicDevice);
            rec.Start();
            lock (_gate) { if (id == _session) _recorder = rec; else rec.Cancel(); }
        }
        catch (Exception ex)
        {
            Log.Error("Mikrofon", ex);
            var mon = _monitor;
            _overlay.Invoke(o => o.ShowError(ex.Message, mon));
            return;
        }

        // "Dinliyorum" göstergesini kısa bir gecikmeyle aç: kapatma dokunuşlarında göz kırpmasın
        var gameName = game?.GameName ?? "";
        var monitor = _monitor;
        _ = Task.Delay(Math.Min(s.MinHoldMs, 200)).ContinueWith(_ =>
        {
            if (Volatile.Read(ref _session) == id && _recorder != null)
                _overlay.Invoke(o => o.ShowListening(monitor, gameName));
        }, TaskScheduler.Default);
    }

    public void OnReleased()
    {
        var s = _settings();
        int id;
        MicRecorder? rec;
        double heldMs;
        CancellationToken ct;
        lock (_gate)
        {
            id = _session;
            rec = _recorder;
            _recorder = null;
            heldMs = Stopwatch.GetElapsedTime(_pressTicks).TotalMilliseconds;
            ct = _cts?.Token ?? CancellationToken.None;
        }
        if (rec == null) return;

        if (heldMs < s.MinHoldMs)
        {
            // Kısa dokunuş = kutuyu kapat
            rec.Cancel();
            _cts?.Cancel();
            _overlay.Invoke(o => o.Dismiss());
            return;
        }

        _ = ProcessAsync(id, rec, heldMs, ct);
    }

    private async Task ProcessAsync(int id, MicRecorder rec, double heldMs, CancellationToken ct)
    {
        var s = _settings();
        var game = _game ?? new GameContext(IntPtr.Zero, "", "", "Bilinmeyen oyun", false, null, null);
        var monitor = _monitor;
        var total = Stopwatch.StartNew();
        try
        {
            // Son hecenin kesilmemesi için kısa kuyruk kaydı
            if (s.TailRecordMs > 0) await Task.Delay(s.TailRecordMs, ct).ConfigureAwait(false);
            var audio = await rec.StopAsync().ConfigureAwait(false);
            if (ct.IsCancellationRequested || id != Volatile.Read(ref _session)) return;

            if (audio == null || audio.Seconds < 0.25)
            {
                _overlay.Invoke(o => o.ShowError("Ses kaydedilemedi. Mikrofonu kontrol et.", monitor));
                return;
            }
            if (audio.Peak < 250)
            {
                _overlay.Invoke(o => o.ShowError("Ses algılanmadı — mikrofon sessiz ya da kapalı olabilir.", monitor));
                Log.Warn($"Sessiz kayıt: peak={audio.Peak}, {audio.Seconds:F1}s");
                return;
            }

            _overlay.Invoke(o => o.ShowThinking());

            CaptureResult cap;
            try { cap = await (_capture ?? throw new InvalidOperationException("yakalama yok")).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Error("Ekran yakalama", ex);
                _overlay.Invoke(o => o.ShowError("Ekran görüntüsü alınamadı: " + ex.Message, monitor));
                return;
            }
            if (ct.IsCancellationRequested) return;

            bool memOn = s.Memory.Enabled;
            var history = memOn
                ? _memory.Recent(game.GameName, s.HistoryTurns, s.HistoryMinutes)
                : Array.Empty<ChatTurn>();
            string? manualProgress = !string.IsNullOrWhiteSpace(game.ProgressNote) ? game.ProgressNote : s.ProgressNote;
            var req = new AskRequest(
                Prompt.BuildSystem(s, game, manualProgress,
                    memOn ? _memory.AutoProgress(game.GameName) : null,
                    memOn ? _memory.BuildPromptBlock(game.GameName) : null),
                Prompt.BuildTurnContext(game, cap.FocusJpeg != null, cap.FocusFromCursor, DateTime.Now),
                cap.FullJpeg, cap.FocusJpeg, audio.Wav, history);

            var parser = new ResponseParser();
            parser.QuestionParsed += q => { if (id == Volatile.Read(ref _session)) _overlay.Invoke(o => o.SetQuestion(q)); };
            parser.AnswerDelta += d => { if (id == Volatile.Read(ref _session)) _overlay.Invoke(o => o.AppendAnswer(d)); };

            long preMs = total.ElapsedMilliseconds;
            var result = await _gemini.AskStreamAsync(req, parser.Feed, ct).ConfigureAwait(false);
            parser.Flush();
            if (id != Volatile.Read(ref _session)) return;

            var answer = parser.Answer.ToString().Trim();
            if (answer.Length == 0)
            {
                _overlay.Invoke(o => o.ShowError("Boş yanıt geldi, tekrar dene.", monitor));
            }
            else
            {
                var sources = result.Sources.ToList();
                MemoryUpdate? upd = null;
                if (memOn)
                {
                    upd = _memory.ApplyMeta(game.GameName, parser.ParseMeta());
                    LastGameName = game.GameName;
                    if (_memory.AddExchange(game.GameName, parser.Question, answer, s.Memory.SummarizeAfter))
                        _ = SummarizeAsync(game.GameName);
                }
                var footerText = string.Join(" · ", new[] { result.Notice, upd?.Footer }.Where(x => !string.IsNullOrEmpty(x)));
                var footer = footerText.Length == 0 ? null : footerText;
                _overlay.Invoke(o => o.Complete(sources, footer));
                if (upd != null && (upd.NewQuest != null || upd.NewRegion != null))
                    Log.Info($"  Hafıza: yeni görev={upd.NewQuest ?? "-"}, yeni bölge={upd.NewRegion ?? "-"}");
            }

            if (result.Notice != null) Log.Warn("  Uyarı: " + result.Notice);
            Log.Info($"[{game.GameName}] model={result.Model} basılı={heldMs:F0}ms ses={audio.Seconds:F1}s yakalama={cap.ElapsedMs}ms " +
                     $"({cap.SourceWidth}x{cap.SourceHeight}, {cap.FullJpeg.Length / 1024}KB+{(cap.FocusJpeg?.Length ?? 0) / 1024}KB) " +
                     $"hazırlık={preMs}ms ilkToken={result.FirstTokenMs}ms toplam={total.ElapsedMilliseconds}ms " +
                     $"token(in={result.PromptTokens}, out={result.OutputTokens}, think={result.ThoughtTokens}) " +
                     $"arama=[{string.Join(" | ", result.SearchQueries)}]");
            Log.Info($"  S: {parser.Question}");
            Log.Info($"  C: {answer.Replace('\n', ' ')}");
            if (parser.MetaRaw.Length > 0) Log.Info($"  M: {parser.MetaRaw.Trim()}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // yeni basış ya da kapatma
        }
        catch (OperationCanceledException)
        {
            if (id == Volatile.Read(ref _session))
                _overlay.Invoke(o => o.ShowError("Yanıt zaman aşımına uğradı.", monitor));
        }
        catch (GeminiException gex)
        {
            Log.Error($"Gemini {gex.Status}: {gex.Message}");
            if (id == Volatile.Read(ref _session))
                _overlay.Invoke(o => o.ShowError(gex.UserMessage, monitor));
        }
        catch (Exception ex)
        {
            Log.Error("İstek başarısız", ex);
            if (id == Volatile.Read(ref _session))
                _overlay.Invoke(o => o.ShowError("Hata: " + ex.Message, monitor));
        }
    }

    /// <summary>Eski soru-cevapları arka planda ucuz bir modelle özetleyip hafızayı sıkıştırır.</summary>
    private async Task SummarizeAsync(string game)
    {
        if (!_summarizing.TryAdd(game, 0)) return;
        try
        {
            var s = _settings();
            var take = _memory.TakeForSummary(game, s.Memory.KeepRecent);
            if (take == null) return;
            var (oldSummary, fold) = take.Value;

            var user = new System.Text.StringBuilder();
            user.AppendLine($"Oyun: {game}");
            user.AppendLine("Önceki özet: " + (string.IsNullOrWhiteSpace(oldSummary) ? "(yok)" : oldSummary));
            user.AppendLine("Yeni soru-cevaplar (eskiden yeniye):");
            foreach (var e in fold) user.AppendLine($"- [{e.At:dd.MM HH:mm}] S: {e.Q} | C: {e.A.Replace('\n', ' ')}");

            const string system =
                "Bir oyun asistanının uzun süreli hafızasını güncelliyorsun. Önceki özeti ve yeni soru-cevapları birleştirip " +
                "en fazla 120 kelimelik tek bir Türkçe özet yaz. İçerik: oyuncunun oyundaki ilerleme durumu, ilgilendiği ve " +
                "takıldığı sistemler (zanaat, binek, dövüş, bulmaca vb.), bulduğu/aradığı önemli eşya ve konumlar, cevap tercihleri. " +
                "Eşya, görev ve yer adlarını orijinal yazımıyla koru. Hikâye spoiler'ı ekleme. Sadece özet metnini yaz, başlık ya da madde kullanma.";

            var sw = Stopwatch.StartNew();
            var summary = await _gemini.GenerateTextAsync(s.Memory.SummaryModel, system, user.ToString(), CancellationToken.None)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(summary)) return;
            _memory.ApplySummary(game, summary, fold);
            Log.Info($"Hafıza özetlendi [{game}] {fold.Count} kayıt → {summary.Length} karakter ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex) { Log.Warn($"Hafıza özetlenemedi [{game}]: {ex.Message}"); }
        finally { _summarizing.TryRemove(game, out _); }
    }
}
