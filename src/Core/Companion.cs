using System;
using System.Collections.Generic;
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
    private readonly HistoryPanel _panel;
    private readonly MarkerOverlay _markers;
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _summarizing = new();

    /// <summary>En son soru sorulan oyun (tepsi menüsündeki "hafızayı sil" için).</summary>
    public string? LastGameName { get; private set; }
    public GameMemoryStore Memory => _memory;

    // Geçerli basış
    private int _session;
    private long _pressTicks;
    private MicRecorder? _recorder;
    private LiveTranscriber? _transcriber;
    private long _lastTapTicks;   // çift dokunuş (geçmiş paneli) tespiti
    private bool _ignoreRelease;  // paneli kapatan basışın bırakışı
    private Task<CaptureResult>? _capture;
    private GameContext? _game;
    private IntPtr _monitor;
    private CancellationTokenSource? _cts;

    public Companion(Func<Settings> settings, OverlayWindow overlay, HistoryPanel panel, MarkerOverlay markers,
                     GameDetector detector, GeminiClient gemini, GameMemoryStore memory)
    {
        _panel = panel;
        _markers = markers;
        _panel.Submitted = AskTyped;
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
        return _panel.IsOpen || !s.ActiveOnlyInKnownGames || _detector.IsKnownGameForeground();
    }

    public void OnPressed()
    {
        // Panel açıksa bu basış yalnızca paneli kapatır
        if (_panel.IsOpen)
        {
            _ignoreRelease = true;
            _overlay.Invoke(_ => _panel.Close(restoreFocus: true));
            return;
        }

        var s = _settings();
        _overlay.Invoke(_ => _markers.Hide());
        int id;
        lock (_gate)
        {
            id = ++_session;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _recorder?.Cancel();
            _recorder = null;
            _transcriber?.Cancel();
            _transcriber = null;
            _pressTicks = Stopwatch.GetTimestamp();
        }

        var fg = Win32.GetForegroundWindow();
        var game = _detector.Detect(fg);
        _game = game;
        _monitor = Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST);

        // Ekranı hemen yakala (oyuncu konuşurken kodlama biter)
        var capCfg = s.Capture;
        _capture = Task.Run(() => ScreenCapture.Capture(fg, capCfg));

        // Canlı yazı: bağlantı mikrofonla paralel kurulur, o arada gelen ses kanalda bekler
        LiveTranscriber? tr = null;
        var key = s.ResolvedApiKey;
        if (s.LiveTranscription && !string.IsNullOrEmpty(key) && !string.IsNullOrWhiteSpace(s.LiveTranscriptionModel))
        {
            tr = new LiveTranscriber(key, s.LiveTranscriptionModel.Trim(), text =>
            {
                if (Volatile.Read(ref _session) == id) _overlay.Invoke(o => o.SetLiveText(text));
            }, startDelayMs: s.MinHoldMs);
            tr.Start();
        }

        // Mikrofonu aç
        try
        {
            var rec = new MicRecorder(s.MicDevice) { ChunkReady = tr == null ? null : tr.Push };
            rec.Start();
            lock (_gate)
            {
                if (id == _session) { _recorder = rec; _transcriber = tr; }
                else { rec.Cancel(); tr?.Cancel(); }
            }
        }
        catch (Exception ex)
        {
            tr?.Cancel();
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
        if (_ignoreRelease)
        {
            _ignoreRelease = false;
            return;
        }
        int id;
        MicRecorder? rec;
        LiveTranscriber? tr;
        double heldMs;
        CancellationToken ct;
        lock (_gate)
        {
            id = _session;
            rec = _recorder;
            _recorder = null;
            tr = _transcriber;
            _transcriber = null;
            heldMs = Stopwatch.GetElapsedTime(_pressTicks).TotalMilliseconds;
            ct = _cts?.Token ?? CancellationToken.None;
        }
        if (rec == null) { tr?.Cancel(); return; }

        if (heldMs < s.MinHoldMs)
        {
            // Kısa dokunuş = kutuyu kapat; hızlı ikinci dokunuş = geçmiş panelini aç
            rec.Cancel();
            tr?.Cancel();
            _cts?.Cancel();
            bool doubleTap = _lastTapTicks != 0 && Stopwatch.GetElapsedTime(_lastTapTicks).TotalMilliseconds < DoubleTapMs;
            _lastTapTicks = doubleTap ? 0 : Stopwatch.GetTimestamp();
            if (doubleTap) OpenHistory();
            else _overlay.Invoke(o => { o.Dismiss(); _markers.Hide(); });
            return;
        }
        _lastTapTicks = 0;

        _ = ProcessAsync(id, rec, tr, heldMs, ct);
    }

    private const double DoubleTapMs = 500;

    /// <summary>
    /// Önceki sorular panelini açar: ön plandaki oyunun geçmişi; boşsa bu oturumda son soru sorulan oyun,
    /// o da yoksa hafızası en son güncellenen oyun. Herhangi bir iş parçacığından çağrılabilir.
    /// </summary>
    public void OpenHistory(bool settingsView = false)
    {
        var game = _game?.GameName;
        var items = string.IsNullOrEmpty(game) ? Array.Empty<MemoryExchange>() : _memory.History(game);
        foreach (var alt in new[] { LastGameName, _memory.MostRecentGame() })
        {
            if (items.Count > 0) break;
            if (string.IsNullOrEmpty(alt) || alt == game) continue;
            var altItems = _memory.History(alt);
            if (altItems.Count == 0) continue;
            game = alt;
            items = altItems;
        }
        var name = game ?? "Bilinmeyen oyun";
        var fg = Win32.GetForegroundWindow();
        // Ön plan bu uygulamanın kendi penceresiyse (tepsi menüsü vb.) son bilinen oyun penceresini hedef al
        Win32.GetWindowThreadProcessId(fg, out var fgPid);
        if ((fgPid == (uint)Environment.ProcessId || fg == IntPtr.Zero) && _game != null && Win32.IsWindow(_game.Hwnd))
            fg = _game.Hwnd;
        var monitor = _monitor != IntPtr.Zero ? _monitor : Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST);
        _overlay.Invoke(o =>
        {
            o.HideNow();
            _panel.Open(name, items, monitor, fg, settingsView);
        });
    }

    private async Task ProcessAsync(int id, MicRecorder rec, LiveTranscriber? tr, double heldMs, CancellationToken ct)
    {
        bool trFinishing = false;
        var s = _settings();
        var game = _game ?? new GameContext(IntPtr.Zero, "", "", "Bilinmeyen oyun", false, null, null);
        var monitor = _monitor;
        var total = Stopwatch.StartNew();
        try
        {
            // Son hecenin kesilmemesi için kısa kuyruk kaydı
            if (s.TailRecordMs > 0) await Task.Delay(s.TailRecordMs, ct).ConfigureAwait(false);
            var audio = await rec.StopAsync().ConfigureAwait(false);
            // Son metin geri çağrıyla kutuya gelir; ana isteği bekletme
            if (tr != null) { trFinishing = true; _ = tr.FinishAsync(); }
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
            var result = await _gemini.AskStreamAsync(req, parser.Feed, ct, () =>
            {
                parser.Reset();
                if (id == Volatile.Read(ref _session)) _overlay.Invoke(o => o.ResetAnswer());
            }).ConfigureAwait(false);
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
                MemoryUpdate? upd = memOn ? SaveExchange(game.GameName, parser, answer, parser.Question, s) : null;
                var footerText = string.Join(" · ", new[] { s.ShowModelNotice ? result.Notice : null, upd?.Footer }.Where(x => !string.IsNullOrEmpty(x)));
                var footer = footerText.Length == 0 ? null : footerText;
                _overlay.Invoke(o => o.Complete(sources, footer));
                ShowPoints(id, ct, parser, cap, s, parser.Question, answer, result.Model);
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
        finally
        {
            if (!trFinishing) tr?.Cancel();
        }
    }

    /// <summary>Meta satırını uygular ve soru-cevabı hafızaya yazar (özetleme gerekiyorsa başlatır).</summary>
    private MemoryUpdate SaveExchange(string gameName, ResponseParser parser, string answer, string question, Settings s)
    {
        var upd = _memory.ApplyMeta(gameName, parser.ParseMeta());
        LastGameName = gameName;
        if (_memory.AddExchange(gameName, question, answer, s.Memory.SummarizeAfter))
            _ = SummarizeAsync(gameName);
        return upd;
    }

    /// <summary>Modelin @@POINT satırları varsa ekranda işaret gösterir (yakalama karesine göre ekran pikseline çevrilir).</summary>
    private void ShowPoints(int id, CancellationToken ct, ResponseParser parser, CaptureResult cap, Settings s, string question, string answer, string answeredBy)
    {
        if (!s.PointerMarkers) return;
        var pts = parser.ParsePoints();
        if (pts.Count == 0) return;
        _overlay.Invoke(_ => _markers.Show(pts, cap.ScreenLeft, cap.ScreenTop, cap.SourceWidth, cap.SourceHeight, s.MarkerSeconds));
        if (s.PointerRefine)
        {
            var shortAnswer = answer.Length > 350 ? answer[..350] + "…" : answer;
            _ = ImprovePointsAsync(id, ct, pts, cap, s, $"Oyuncunun sorusu: {question}. Asistanın cevabı: {shortAnswer}", answeredBy);
        }
    }

    /// <summary>
    /// İşaretleri iyileştirir (en fazla iki aşama, ikisi de başarısız olursa ilk tahmin kalır; halkalar yeni yere yumuşakça kayar):
    /// A) Cevabı HAFİF model verdiyse: hafif model yoğun ekranlarda (yetenek ağacı gibi) yanlış öğeyi seçebildiği için hedefler
    ///    güçlü bir modelle tam karede yeniden konumlandırılır.
    /// B) Kutusu büyük (yani belirsiz) hedefler için: çevresi ekrandan kırpılıp büyütülür ve yakından tekrar sorulur.
    ///    Kutusu zaten sıkı olan hedeflere dokunulmaz; kutunun izin verdiğinden büyük atlamalar (başka bir benzer öğeye
    ///    kayma) reddedilir.
    /// </summary>
    private async Task ImprovePointsAsync(int id, CancellationToken ct, IReadOnlyList<PointMark> pts, CaptureResult cap, Settings s,
                                          string context, string answeredBy)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            int W = cap.SourceWidth, H = cap.SourceHeight;
            var current = new List<PointMark>(pts);
            bool anyChange = false;

            // ---- A) hafif modelin cevabını güçlü modelle doğrula (tam kare)
            if (answeredBy.Contains("lite", StringComparison.OrdinalIgnoreCase))
            {
                var targets = current.Select(p => (string.IsNullOrWhiteSpace(p.Label) ? "işaretlenen nesne" : p.Label, p.Desc)).ToList();
                var full = await _gemini.LocateOnFrameAsync(cap.FullJpeg, targets, context, ct).ConfigureAwait(false);
                if (id != Volatile.Read(ref _session)) return;
                int moved = 0;
                double drift = 0;
                for (int i = 0; i < current.Count; i++)
                {
                    var b = full[i];
                    if (b == null) continue;
                    double cx = (b[1] + b[3]) / 2, cy = (b[0] + b[2]) / 2;      // 0-1000
                    double bw = b[3] - b[1], bh = b[2] - b[0];
                    drift += Math.Sqrt(Math.Pow((cx - current[i].X) / 1000 * W, 2) + Math.Pow((cy - current[i].Y) / 1000 * H, 2));
                    current[i] = current[i] with { X = cx, Y = cy, W = bw, H = bh };
                    moved++;
                }
                if (moved > 0)
                {
                    anyChange = true;
                    Log.Info($"  İşaret doğrulama (güçlü model, tam kare): {moved}/{current.Count} hedef, ort. kayma {drift / moved:F0}px, {sw.ElapsedMilliseconds}ms");
                    _overlay.Invoke(_ => _markers.Update(new List<PointMark>(current)));
                }
                else Log.Info($"  İşaret doğrulama: güçlü model kullanılamadı ya da hedefi ayırt edemedi; hafif modelin tahmini kaldı ({sw.ElapsedMilliseconds}ms)");
            }

            // ---- B) belirsiz (büyük kutulu) hedefleri kırpıp yakından sor
            int cs = (int)Math.Clamp(W * 0.22, 240, Math.Min(W, H));
            var idx = new List<int>();
            for (int i = 0; i < current.Count; i++)
            {
                double boxPx = Math.Max(current[i].W / 1000 * W, current[i].H / 1000 * H);
                if (current[i].W <= 0 || boxPx >= W * 0.045) idx.Add(i);       // kutusu bilinmiyor ya da ~86px+ (1080p): belirsiz
            }
            if (idx.Count > 0)
            {
                var origins = new (int x, int y)[idx.Count];
                var crops = new List<(string, string, byte[])>();
                for (int k = 0; k < idx.Count; k++)
                {
                    var p = current[idx[k]];
                    int cx = (int)(p.X / 1000 * W), cy = (int)(p.Y / 1000 * H);
                    int ox = Math.Clamp(cx - cs / 2, 0, W - cs), oy = Math.Clamp(cy - cs / 2, 0, H - cs);
                    origins[k] = (ox, oy);
                    var jpg = await Task.Run(() => ScreenCapture.CaptureRegionJpeg(cap.ScreenLeft + ox, cap.ScreenTop + oy, cs, 768, 88), ct).ConfigureAwait(false);
                    crops.Add((string.IsNullOrWhiteSpace(p.Label) ? "işaretlenen nesne" : p.Label, p.Desc, jpg));
                }
                var boxes = await _gemini.LocateAsync(crops, context, ct).ConfigureAwait(false);
                if (id != Volatile.Read(ref _session)) return;

                int changed = 0;
                double drift = 0;
                for (int k = 0; k < idx.Count; k++)
                {
                    var b = boxes[k];
                    if (b == null) continue;
                    var p = current[idx[k]];
                    double bw = (b[3] - b[1]) / 1000 * cs, bh = (b[2] - b[0]) / 1000 * cs;
                    double px = origins[k].x + (b[1] + b[3]) / 2 / 1000 * cs, py = origins[k].y + (b[0] + b[2]) / 2 / 1000 * cs;
                    double dist = Math.Sqrt(Math.Pow(px - p.X / 1000 * W, 2) + Math.Pow(py - p.Y / 1000 * H, 2));
                    double oldBox = Math.Max(p.W / 1000 * W, p.H / 1000 * H);
                    double maxDrift = Math.Min(cs * 0.5, Math.Max(36, oldBox * 1.3));
                    // Kutu kırpmanın neredeyse tamamıysa ya da izin verilen kaymadan büyükse (başka bir benzer öğeye atlama) güvenme
                    if (bw < cs * 0.85 && bh < cs * 0.85 && dist <= maxDrift)
                    {
                        current[idx[k]] = p with { X = px / W * 1000, Y = py / H * 1000, W = bw / W * 1000, H = bh / H * 1000 };
                        changed++;
                        drift += dist;
                    }
                }
                if (changed > 0)
                {
                    anyChange = true;
                    Log.Info($"  İşaret ince ayar: {changed}/{idx.Count} belirsiz hedef, ort. kayma {drift / changed:F0}px, {sw.ElapsedMilliseconds}ms");
                    if (id == Volatile.Read(ref _session)) _overlay.Invoke(_ => _markers.Update(new List<PointMark>(current)));
                }
            }
            if (!anyChange) Log.Info($"  İşaret iyileştirme: değişiklik yok ({sw.ElapsedMilliseconds}ms)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (OperationCanceledException) { Log.Warn("İşaret iyileştirme zaman aşımına uğradı; mevcut konum kaldı"); }
        catch (Exception ex) { Log.Warn("İşaret iyileştirilemedi: " + ex.Message); }
    }

    // ------------------------------------------------------------------ Yazılı soru (sohbet paneli)

    /// <summary>Panelde Enter'a basılınca (UI iş parçacığı) çağrılır. Yeni bir oturum açar, süren isteği iptal eder.</summary>
    private void AskTyped(string text)
    {
        int id;
        CancellationToken ct;
        lock (_gate)
        {
            id = ++_session;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            ct = _cts.Token;
            _recorder?.Cancel();
            _recorder = null;
            _transcriber?.Cancel();
            _transcriber = null;
        }
        _markers.Hide();

        var target = _panel.ReturnTarget;
        if (target == IntPtr.Zero || !Win32.IsWindow(target)) target = _game?.Hwnd ?? IntPtr.Zero;
        _ = ProcessTypedAsync(id, text, target, ct);
    }

    private async Task ProcessTypedAsync(int id, string text, IntPtr target, CancellationToken ct)
    {
        var s = _settings();
        var total = Stopwatch.StartNew();
        void Ui(Action<HistoryPanel> a)
        {
            if (id == Volatile.Read(ref _session)) _overlay.Invoke(_ => a(_panel));
        }
        try
        {
            var game = _detector.Detect(target) ?? _game ?? new GameContext(IntPtr.Zero, "", "", "Bilinmeyen oyun", false, null, null);

            // Panel WDA_EXCLUDEFROMCAPTURE ile işaretli: kareye girmez. Odak kırpması yok (imleç panelde).
            CaptureResult cap;
            try { cap = await Task.Run(() => ScreenCapture.Capture(target, s.Capture, includeFocus: false), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Ekran yakalama (yazılı)", ex);
                Ui(p => p.EndPending("Ekran görüntüsü alınamadı: " + ex.Message));
                return;
            }
            ct.ThrowIfCancellationRequested();

            bool memOn = s.Memory.Enabled;
            var history = memOn
                ? _memory.Recent(game.GameName, s.HistoryTurns, s.HistoryMinutes)
                : Array.Empty<ChatTurn>();
            string? manualProgress = !string.IsNullOrWhiteSpace(game.ProgressNote) ? game.ProgressNote : s.ProgressNote;
            var req = new AskRequest(
                Prompt.BuildSystem(s, game, manualProgress,
                    memOn ? _memory.AutoProgress(game.GameName) : null,
                    memOn ? _memory.BuildPromptBlock(game.GameName) : null,
                    typed: true),
                Prompt.BuildTurnContext(game, false, false, DateTime.Now, text),
                cap.FullJpeg, null, null, history, text);

            var parser = new ResponseParser();
            parser.AnswerDelta += d => Ui(p => p.AppendPending(d));

            var result = await _gemini.AskStreamAsync(req, parser.Feed, ct, () =>
            {
                parser.Reset();
                Ui(p => p.ResetPending());
            }).ConfigureAwait(false);
            parser.Flush();
            if (id != Volatile.Read(ref _session)) return;

            var answer = parser.Answer.ToString().Trim();
            if (answer.Length == 0)
            {
                Ui(p => p.EndPending("Boş yanıt geldi, tekrar dene."));
            }
            else
            {
                if (memOn) SaveExchange(game.GameName, parser, answer, string.IsNullOrWhiteSpace(parser.Question) ? text : parser.Question, s);
                if (s.ShowModelNotice && result.Notice != null) Ui(p => p.AppendPending("\n\n" + result.Notice));
                Ui(p => p.EndPending(null));
                ShowPoints(id, ct, parser, cap, s, parser.Question, answer, result.Model);
            }

            if (result.Notice != null) Log.Warn("  Uyarı: " + result.Notice);
            Log.Info($"[{game.GameName}] YAZILI model={result.Model} yakalama={cap.ElapsedMs}ms ilkToken={result.FirstTokenMs}ms " +
                     $"toplam={total.ElapsedMilliseconds}ms token(in={result.PromptTokens}, out={result.OutputTokens}, think={result.ThoughtTokens}) " +
                     $"nokta={parser.ParsePoints().Count}");
            Log.Info($"  S: {text}");
            Log.Info($"  C: {answer.Replace('\n', ' ')}");
            if (parser.MetaRaw.Length > 0) Log.Info($"  M: {parser.MetaRaw.Trim().Replace('\n', ' ')}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (OperationCanceledException) { Ui(p => p.EndPending("Yanıt zaman aşımına uğradı.")); }
        catch (GeminiException gex)
        {
            Log.Error($"Gemini {gex.Status}: {gex.Message}");
            Ui(p => p.EndPending(gex.UserMessage));
        }
        catch (Exception ex)
        {
            Log.Error("Yazılı istek başarısız", ex);
            Ui(p => p.EndPending("Hata: " + ex.Message));
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
