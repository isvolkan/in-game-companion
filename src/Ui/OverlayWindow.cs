using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using InGameCompanion.Core;
using InGameCompanion.Native;

namespace InGameCompanion.Ui;

/// <summary>
/// Oyunun üzerinde duran şeffaf HUD.
/// - WS_EX_LAYERED + UpdateLayeredWindow: piksel başına alfa (yarı saydam, yumuşak köşeler)
/// - WS_EX_TRANSPARENT: tüm tıklamalar arkadaki oyuna geçer
/// - WS_EX_NOACTIVATE + SW_SHOWNOACTIVATE: odak asla oyundan çalınmaz
/// - WDA_EXCLUDEFROMCAPTURE: kendi ekran görüntülerimize girmez
/// Tüm üyeler UI (ana) iş parçacığında çalışır; diğer iş parçacıkları <see cref="Invoke"/> kullanır.
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    private enum Mode { Hidden, Listening, Thinking, Streaming, Done, Error, Info }

    private const uint WM_RUN = Win32.WM_APP + 1;
    private const int TimerId = 1;
    private const string ClassName = "InGameCompanionOverlay";

    private static Win32.WndProc? _wndProc; // GC koruması
    private static OverlayWindow? _instance;

    private readonly Func<Settings> _settings;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly TextRenderer _text = new();
    private IntPtr _hwnd;

    // Durum
    private Mode _mode = Mode.Hidden;
    private string _header = "";
    private string _question = "";
    private bool _questionFromModel;
    private readonly StringBuilder _answer = new();
    private double _revealed;               // gösterilen karakter sayısı (kesirli, daktilo için)
    private bool _streamDone;
    private List<string> _sources = new();
    private string? _footer;
    private DateTime _modeStart = DateTime.UtcNow;
    private DateTime? _holdUntil;
    private DateTime? _fadeStart;
    private double _fadeMs = 700;
    private DateTime _lastTick = DateTime.UtcNow;
    private DateTime _lastTopmost = DateTime.MinValue;
    private bool _dirty;
    private bool _visible;
    private bool _timerOn;

    // Yüzey
    private IntPtr _monitor;
    private double _scale = 1;
    private TextRenderer.Fonts? _fonts;
    private IntPtr _screenDc, _memDc, _dib, _oldBmp, _bits, _gBitmap, _g;
    private int _surfW, _surfH;
    private readonly List<TextRenderer.Op> _ops = new();

    public OverlayWindow(Func<Settings> settings)
    {
        _settings = settings;
        _instance = this;
        CreateWindow();
    }

    // ------------------------------------------------------------------ Pencere

    private void CreateWindow()
    {
        _wndProc = StaticWndProc;
        var hInst = Win32.GetModuleHandleW(null);
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInst,
            lpszClassName = ClassName,
        };
        Win32.RegisterClassExW(ref wc);

        int ex = Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE
               | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST;
        _hwnd = Win32.CreateWindowExW(ex, ClassName, "In-Game Companion", Win32.WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Overlay penceresi oluşturulamadı: " + Marshal.GetLastWin32Error());

        // Kendi ekran yakalamalarımıza girmesin (Win10 2004+). Eski sürümde WDA_MONITOR'a düş.
        if (!Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
        {
            Log.Warn("WDA_EXCLUDEFROMCAPTURE desteklenmiyor; WDA_MONITOR kullanılıyor");
            Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_MONITOR);
        }
        _screenDc = Win32.GetDC(IntPtr.Zero);
        _memDc = Win32.CreateCompatibleDC(_screenDc);
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _instance;
        if (self != null && hwnd == self._hwnd)
        {
            switch (msg)
            {
                case WM_RUN:
                    self.DrainQueue();
                    return IntPtr.Zero;
                case Win32.WM_TIMER:
                    self.Tick();
                    return IntPtr.Zero;
                case Win32.WM_MOUSEACTIVATE:
                    return new IntPtr(Win32.MA_NOACTIVATE);
                case Win32.WM_NCHITTEST:
                    return new IntPtr(Win32.HTTRANSPARENT);
            }
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>Herhangi bir iş parçacığından UI iş parçacığına iş gönderir.</summary>
    public void Invoke(Action<OverlayWindow> action)
    {
        _queue.Enqueue(() => action(this));
        Win32.PostMessageW(_hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
    }

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex) { Log.Error("Overlay işlemi başarısız", ex); }
        }
    }

    // ------------------------------------------------------------------ Genel API (UI iş parçacığı)

    public void ShowListening(IntPtr monitor, string gameName)
    {
        Reset(Mode.Listening, monitor);
        _header = string.IsNullOrWhiteSpace(gameName) ? "Dinliyorum" : $"Dinliyorum · {gameName}";
    }

    public void ShowThinking()
    {
        if (_mode == Mode.Hidden) return;
        SetMode(Mode.Thinking);
        _header = "Düşünüyor";
    }

    public void SetQuestion(string q)
    {
        _question = q;
        _questionFromModel = true;
        _dirty = true;
    }

    /// <summary>Konuşurken canlı yazı. Modelin "S:" satırı geldiyse onu ezmez.</summary>
    public void SetLiveText(string text)
    {
        if (_mode is not (Mode.Listening or Mode.Thinking) || _questionFromModel) return;
        _question = text;
        _dirty = true;
    }

    public void AppendAnswer(string delta)
    {
        if (_mode is Mode.Hidden or Mode.Error) return;
        if (_mode != Mode.Streaming) SetMode(Mode.Streaming);
        _answer.Append(delta);
        _dirty = true;
    }

    public void Complete(IReadOnlyList<string> sources, string? footer = null)
    {
        if (_mode is Mode.Hidden or Mode.Error) return;
        _sources = new List<string>(sources);
        _footer = footer;
        _streamDone = true;
        if (_mode != Mode.Streaming) SetMode(Mode.Streaming);
        _dirty = true;
    }

    public void ShowError(string message, IntPtr monitor = default)
    {
        if (monitor == IntPtr.Zero) monitor = _monitor != IntPtr.Zero ? _monitor : PrimaryMonitor();
        Reset(Mode.Error, monitor);
        _header = message;
        _holdUntil = DateTime.UtcNow.AddSeconds(7);
    }

    public void ShowInfo(string title, string body, double seconds, IntPtr monitor = default)
    {
        if (monitor == IntPtr.Zero) monitor = PrimaryMonitor();
        Reset(Mode.Info, monitor);
        _header = title;
        _answer.Append(body);
        _revealed = body.Length;
        _holdUntil = DateTime.UtcNow.AddSeconds(seconds);
    }

    /// <summary>Hızlı kapanış (kısa dokunuş).</summary>
    public void Dismiss()
    {
        if (_mode == Mode.Hidden) return;
        _fadeMs = 220;
        _fadeStart ??= DateTime.UtcNow;
        _holdUntil = DateTime.UtcNow;
    }

    /// <summary>Anında gizle (ör. kayıt iptal edildi ve daha önce bir şey gösterilmiyordu).</summary>
    public void HideNow() => Hide();

    public bool IsBusyOrVisible => _mode != Mode.Hidden;

    // ------------------------------------------------------------------ Durum makinesi

    private void Reset(Mode mode, IntPtr monitor)
    {
        _question = "";
        _questionFromModel = false;
        _answer.Clear();
        _revealed = 0;
        _streamDone = false;
        _sources.Clear();
        _footer = null;
        _holdUntil = null;
        _fadeStart = null;
        _fadeMs = 700;
        _header = "";
        SetMonitor(monitor);
        SetMode(mode);
        EnsureVisible();
    }

    private void SetMode(Mode m)
    {
        _mode = m;
        _modeStart = DateTime.UtcNow;
        _dirty = true;
    }

    private void EnsureVisible()
    {
        if (!_timerOn)
        {
            Win32.SetTimer(_hwnd, (UIntPtr)TimerId, 16, IntPtr.Zero);
            _timerOn = true;
            _lastTick = DateTime.UtcNow;
        }
        Render();
        if (!_visible)
        {
            Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
            _visible = true;
        }
        KeepTopmost(force: true);
    }

    private void Hide()
    {
        _mode = Mode.Hidden;
        if (_timerOn) { Win32.KillTimer(_hwnd, (UIntPtr)TimerId); _timerOn = false; }
        if (_visible) { Win32.ShowWindow(_hwnd, Win32.SW_HIDE); _visible = false; }
        _answer.Clear();
        _question = "";
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min(0.1, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        var o = _settings().Overlay;

        // Daktilo efekti: birikme varsa hızlan ki gecikme büyümesin
        if (_mode == Mode.Streaming && _revealed < _answer.Length)
        {
            double backlog = _answer.Length - _revealed;
            double cps = Math.Max(o.CharsPerSecond, backlog * 2.5);
            _revealed = Math.Min(_answer.Length, _revealed + cps * dt);
            _dirty = true;
        }

        // Tamamlandı → okuma süresi kadar ekranda kal
        if (_mode == Mode.Streaming && _streamDone && _revealed >= _answer.Length && _holdUntil == null)
        {
            _mode = Mode.Done;
            int words = CountWords(_answer.ToString()) + CountWords(_question) / 2;
            double secs = Math.Clamp(3 + words / Math.Max(0.5, o.ReadingWordsPerSecond), o.MinVisibleSeconds, o.MaxVisibleSeconds);
            _holdUntil = now.AddSeconds(secs);
            _dirty = true;
        }

        // Animasyonlu başlıklar (nabız, üç nokta)
        if (_mode is Mode.Listening or Mode.Thinking) _dirty = true;

        if (_holdUntil != null && now >= _holdUntil && _fadeStart == null) _fadeStart = now;

        double alpha = 1;
        if (_fadeStart != null)
        {
            double t = (now - _fadeStart.Value).TotalMilliseconds / _fadeMs;
            if (t >= 1) { Hide(); return; }
            alpha = 1 - EaseIn(t);
            _dirty = true;
        }

        if (_dirty) Render(alpha);
        if ((now - _lastTopmost).TotalMilliseconds > 1500) KeepTopmost(force: false);
    }

    private static double EaseIn(double t) => t * t;

    private void KeepTopmost(bool force)
    {
        _lastTopmost = DateTime.UtcNow;
        // Bazı oyunlar çerçevesiz pencere modunda kendini periyodik olarak öne alır
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    private static int CountWords(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ------------------------------------------------------------------ Çizim

    private void SetMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) monitor = PrimaryMonitor();
        double scale = Win32.GetMonitorScale(monitor);
        if (_fonts == null || Math.Abs(scale - _scale) > 0.001)
        {
            _fonts?.Dispose();
            var o = _settings().Overlay;
            _fonts = _text.CreateFonts(o.FontFamily, o.FontSize, scale);
            _scale = scale;
        }
        _monitor = monitor;
    }

    private static IntPtr PrimaryMonitor() =>
        Win32.MonitorFromPoint(new Win32.POINT(0, 0), Win32.MONITOR_DEFAULTTONEAREST);

    private void EnsureSurface(int w, int h)
    {
        if (w <= _surfW && h <= _surfH && _g != IntPtr.Zero) return;
        FreeSurface();
        _surfW = Math.Max(w, _surfW);
        _surfH = Math.Max(h, _surfH);
        _dib = Win32.CreateDib32(_screenDc, _surfW, _surfH, out _bits);
        _oldBmp = Win32.SelectObject(_memDc, _dib);
        Gdip.Check(Gdip.GdipCreateBitmapFromScan0(_surfW, _surfH, _surfW * 4, Gdip.PixelFormat32bppPARGB, _bits, out _gBitmap), "Surface");
        Gdip.Check(Gdip.GdipGetImageGraphicsContext(_gBitmap, out _g), "SurfaceG");
        Gdip.GdipSetSmoothingMode(_g, Gdip.SmoothingModeAntiAlias);
        Gdip.GdipSetTextRenderingHint(_g, Gdip.TextRenderingHintAntiAlias);
        Gdip.GdipSetPixelOffsetMode(_g, Gdip.PixelOffsetModeHighQuality);
    }

    private void FreeSurface()
    {
        if (_g != IntPtr.Zero) { Gdip.GdipDeleteGraphics(_g); _g = IntPtr.Zero; }
        if (_gBitmap != IntPtr.Zero) { Gdip.GdipDisposeImage(_gBitmap); _gBitmap = IntPtr.Zero; }
        if (_dib != IntPtr.Zero)
        {
            Win32.SelectObject(_memDc, _oldBmp);
            Win32.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
    }

    private static uint Argb(byte a, uint rgb) => ((uint)a << 24) | (rgb & 0xFFFFFF);

    private static uint ParseHex(string? hex, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var h = hex.Trim().TrimStart('#');
        return uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? (v & 0xFFFFFF) : fallback;
    }

    private void Render(double alpha = 1)
    {
        _dirty = false;
        if (_mode == Mode.Hidden || _fonts == null) return;

        var o = _settings().Overlay;
        var f = _fonts;
        float s = (float)_scale;
        var mon = Win32.GetMonitorRect(_monitor);

        int W = (int)Math.Round(o.Width * s);
        int maxH = (int)(mon.Height * Math.Clamp(o.MaxHeightRatio, 0.2, 0.95));
        float pad = 14 * s;
        float barX = pad * 0.65f;
        float x0 = pad + 8 * s;
        float contentW = W - x0 - pad;

        uint accent = ParseHex(o.AccentColor, 0xD2463C);
        const uint White = 0xFFECEFF3;
        const uint Bold = 0xFFFFFFFF;
        const uint Muted = 0xFF9AA4AF;
        const uint Faint = 0xFF6E7884;
        const uint ErrorRed = 0xFFFF7A6E;

        _ops.Clear();
        float y = pad;
        var now = DateTime.UtcNow;
        double since = (now - _modeStart).TotalSeconds;
        float dotSize = 8 * s;
        (float x, float y, uint color)? dot = null;

        switch (_mode)
        {
            case Mode.Listening:
            {
                double pulse = 0.55 + 0.45 * Math.Sin(since * Math.PI * 2 / 1.1);
                dot = (x0, y + (f.SmallLine - dotSize) / 2, Argb((byte)(120 + 135 * pulse), 0xFF4D4D));
                _ops.Add(new TextRenderer.Op { Text = _header, X = x0 + dotSize + 7 * s, Y = y, Font = f.SmallBold, Color = Muted, LineHeight = f.SmallLine });
                y += f.SmallLine;
                if (!string.IsNullOrEmpty(_question))
                {
                    y += 4 * s;
                    _text.LayoutPlain(_question, f.Body, f.BodyLine, White, x0, contentW, ref y, _ops);
                }
                break;
            }
            case Mode.Thinking:
            {
                int n = (int)(since * 3) % 4;
                dot = (x0, y + (f.SmallLine - dotSize) / 2, Argb(255, 0xF2B33D));
                _ops.Add(new TextRenderer.Op { Text = _header + new string('.', n), X = x0 + dotSize + 7 * s, Y = y, Font = f.SmallBold, Color = Muted, LineHeight = f.SmallLine });
                y += f.SmallLine;
                if (!string.IsNullOrEmpty(_question))
                {
                    y += 3 * s;
                    _text.LayoutPlain($"“{_question}”", f.SmallItalic, f.SmallLine, Faint, x0, contentW, ref y, _ops);
                }
                break;
            }
            case Mode.Error:
                _text.LayoutPlain(_header, f.Body, f.BodyLine, ErrorRed, x0, contentW, ref y, _ops);
                break;
            case Mode.Info:
                _text.LayoutPlain(_header, f.SmallBold, f.SmallLine, Muted, x0, contentW, ref y, _ops);
                if (_answer.Length > 0)
                {
                    y += 4 * s;
                    _text.LayoutRich(_answer.ToString(), f, White, accent | 0xFF000000, Bold, x0, contentW, ref y, _ops);
                }
                break;
            case Mode.Streaming:
            case Mode.Done:
            {
                if (!string.IsNullOrEmpty(_question))
                {
                    _text.LayoutPlain($"“{_question}”", f.SmallItalic, f.SmallLine, Faint, x0, contentW, ref y, _ops);
                    y += 5 * s;
                }
                int n = (int)Math.Min(_answer.Length, Math.Floor(_revealed));
                var visible = _answer.ToString(0, n);
                _text.LayoutRich(visible, f, White, accent | 0xFF000000, Bold, x0, contentW, ref y, _ops);
                if (_mode == Mode.Done && _sources.Count > 0)
                {
                    y += 7 * s;
                    var src = "Kaynak: " + string.Join(" · ", _sources.GetRange(0, Math.Min(3, _sources.Count)));
                    _text.LayoutPlain(src, f.Small, f.SmallLine, Faint, x0, contentW, ref y, _ops);
                }
                if (_mode == Mode.Done && !string.IsNullOrEmpty(_footer))
                {
                    y += (_sources.Count > 0 ? 2 : 7) * s;
                    _text.LayoutPlain(_footer, f.SmallBold, f.SmallLine, accent | 0xFF000000, x0, contentW, ref y, _ops);
                }
                break;
            }
        }

        int H = (int)Math.Ceiling(y + pad);
        H = Math.Clamp(H, (int)(pad * 2 + f.SmallLine), maxH);

        EnsureSurface(W, maxH);
        Gdip.GdipGraphicsClear(_g, 0);

        // Arka plan: yarı saydam koyu, yuvarlak köşe
        byte bgA = (byte)Math.Clamp(o.BackgroundOpacity * 255, 60, 250);
        FillRoundRect(_g, Argb(bgA, 0x0D1015), 0.5f, 0.5f, W - 1, H - 1, 10 * s);
        // Sol vurgu çizgisi
        uint barColor = _mode switch
        {
            Mode.Listening => 0xFFFF4D4D,
            Mode.Thinking => 0xFFF2B33D,
            Mode.Error => ErrorRed,
            _ => accent | 0xFF000000,
        };
        FillRect(_g, barColor, barX, pad, 3 * s, H - pad * 2);
        if (dot is { } d) FillEllipse(_g, d.color, d.x, d.y, dotSize, dotSize);

        _text.Draw(_g, _ops, H - pad * 0.5f);

        // Pencereye bas
        var dst = new Win32.POINT(mon.Right - (int)Math.Round(o.MarginRight * s) - W, mon.Top + (int)Math.Round(o.MarginTop * s));
        var size = new Win32.SIZE(W, H);
        var srcPt = new Win32.POINT(0, 0);
        var blend = new Win32.BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            SourceConstantAlpha = (byte)Math.Clamp(alpha * 255, 0, 255),
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };
        if (!Win32.UpdateLayeredWindow(_hwnd, _screenDc, ref dst, ref size, _memDc, ref srcPt, 0, ref blend, Win32.ULW_ALPHA))
            Log.Warn("UpdateLayeredWindow başarısız: " + Marshal.GetLastWin32Error());
    }

    private static void FillRect(IntPtr g, uint color, float x, float y, float w, float h)
    {
        Gdip.GdipCreateSolidFill(color, out var b);
        Gdip.GdipFillRectangle(g, b, x, y, w, h);
        Gdip.GdipDeleteBrush(b);
    }

    private static void FillEllipse(IntPtr g, uint color, float x, float y, float w, float h)
    {
        Gdip.GdipCreateSolidFill(color, out var b);
        Gdip.GdipFillEllipse(g, b, x, y, w, h);
        Gdip.GdipDeleteBrush(b);
    }

    private static void FillRoundRect(IntPtr g, uint color, float x, float y, float w, float h, float r)
    {
        float d = r * 2;
        Gdip.GdipCreatePath(0, out var path);
        Gdip.GdipAddPathArc(path, x, y, d, d, 180, 90);
        Gdip.GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
        Gdip.GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
        Gdip.GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
        Gdip.GdipClosePathFigure(path);
        Gdip.GdipCreateSolidFill(color, out var b);
        Gdip.GdipFillPath(g, b, path);
        Gdip.GdipDeleteBrush(b);
        Gdip.GdipDeletePath(path);
    }

    public void Dispose()
    {
        Hide();
        FreeSurface();
        _fonts?.Dispose();
        _text.Dispose();
        if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero) Win32.ReleaseDC(IntPtr.Zero, _screenDc);
        if (_hwnd != IntPtr.Zero) Win32.DestroyWindow(_hwnd);
    }
}
