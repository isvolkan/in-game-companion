using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using InGameCompanion.Ai;
using InGameCompanion.Core;
using InGameCompanion.Native;

namespace InGameCompanion.Ui;

/// <summary>
/// "Şuna bas, şuraya git" işaretleri: modelin gösterdiği hedeflerin çevresine nabız gibi atan halka + etiket çizer.
/// Halkanın boyu hedefin kutusuna uyar. Konum sonradan hassaslaştırılırsa (<see cref="Update"/>) halka yumuşakça kayar.
/// HUD gibi tıklamayı arkaya geçirir, odağı çalmaz ve ekran yakalamalarına girmez.
/// Yalnızca işaretlerin çevresini kaplayan küçük bir pencere kullanır. Tüm üyeler UI iş parçacığında çalışır.
/// </summary>
internal sealed class MarkerOverlay : IDisposable
{
    private const string ClassName = "InGameCompanionMarkers";
    private const int TimerId = 1;

    private static Win32.WndProc? _wndProc; // GC koruması
    private static MarkerOverlay? _instance;

    private readonly Func<Settings> _settings;
    private readonly TextRenderer _text = new();
    private readonly List<TextRenderer.Op> _ops = new();
    private IntPtr _hwnd;

    private sealed class Mark
    {
        public double X, Y, R;          // şu anki (ekran pikseli, halka yarıçapı)
        public double TX, TY, TR;       // hedef
        public string Label = "";
        public int Step;
        public bool Arrow;              // true: halka yerine ok
        public double DX, DY;           // ok yönü: hedeften dışarı doğru birim vektör (okun kuyruğu bu tarafta)
    }

    private List<Mark> _marks = new();
    private bool _numbered;
    private int _left, _top, _fw, _fh;  // yakalanan karenin ekrandaki konumu/boyu
    private DateTime _start, _until, _lastTopmost, _lastTick;
    private double _scale = 1;
    private Win32.RECT _mon;
    private TextRenderer.Fonts? _fonts;
    private bool _visible, _timerOn;

    private IntPtr _screenDc, _memDc, _dib, _oldBmp, _bits, _gBitmap, _g;
    private int _surfW, _surfH;

    public MarkerOverlay(Func<Settings> settings)
    {
        _settings = settings;
        _instance = this;
        CreateWindow();
    }

    public bool IsVisible => _visible;

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
        _hwnd = Win32.CreateWindowExW(ex, ClassName, "In-Game Companion Markers", Win32.WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("İşaret penceresi oluşturulamadı: " + Marshal.GetLastWin32Error());
        if (!Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
            Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_MONITOR);
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
                case Win32.WM_TIMER:
                    try { self.Tick(); } catch (Exception ex) { Log.Error("İşaret çizimi", ex); self.Hide(); }
                    return IntPtr.Zero;
                case Win32.WM_MOUSEACTIVATE:
                    return new IntPtr(Win32.MA_NOACTIVATE);
                case Win32.WM_NCHITTEST:
                    return new IntPtr(Win32.HTTRANSPARENT);
            }
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------ API

    /// <summary>
    /// Hedefleri (0-1000 ölçeği, yakalanan karenin sol üstüne göre) ekran pikseline çevirip gösterir.
    /// <paramref name="left"/>/<paramref name="top"/>/<paramref name="width"/>/<paramref name="height"/>: yakalanan alanın ekrandaki konumu.
    /// </summary>
    public void Show(IReadOnlyList<PointMark> points, int left, int top, int width, int height, double seconds)
    {
        if (points.Count == 0 || width < 16 || height < 16) { Hide(); return; }
        _left = left; _top = top; _fw = width; _fh = height;

        var first = ToScreen(points[0]);
        var mon = Win32.MonitorFromPoint(new Win32.POINT((int)first.x, (int)first.y), Win32.MONITOR_DEFAULTTONEAREST);
        _mon = Win32.GetMonitorRect(mon);
        double scale = Win32.GetMonitorScale(mon);
        if (_fonts == null || Math.Abs(scale - _scale) > 0.001)
        {
            _fonts?.Dispose();
            _fonts = _text.CreateFonts(_settings().Overlay.FontFamily, _settings().Overlay.FontSize, scale);
            _scale = scale;
        }

        var marks = new List<Mark>();
        foreach (var p in points)
        {
            var (x, y, r) = ToScreen(p);
            marks.Add(new Mark { X = x, Y = y, R = r, TX = x, TY = y, TR = r, Label = p.Label, Step = p.Step,
                                 Arrow = p.Style == "arrow" });
        }
        _marks = marks;
        AssignDirections();
        _numbered = marks.Count > 1;
        _start = _lastTick = DateTime.UtcNow;
        _until = _start.AddSeconds(Math.Clamp(seconds, 2, 120));
        if (!_timerOn)
        {
            Win32.SetTimer(_hwnd, (UIntPtr)TimerId, 33, IntPtr.Zero);
            _timerOn = true;
        }
        Tick();
        if (!_visible)
        {
            Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
            _visible = true;
        }
        Keep();
        Log.Info("İşaret: " + string.Join(" → ", marks.ConvertAll(m => $"{m.Step}:{m.Label}@{m.TX:F0},{m.TY:F0}")));
    }

    /// <summary>Hassaslaştırılmış konumlar: aynı sırayla, halkalar yeni yere yumuşakça kayar. Gösterim bitmişse yok sayılır.</summary>
    public void Update(IReadOnlyList<PointMark> points)
    {
        if (!_visible || points.Count != _marks.Count) return;
        for (int i = 0; i < points.Count; i++)
        {
            var (x, y, r) = ToScreen(points[i]);
            _marks[i].TX = x; _marks[i].TY = y; _marks[i].TR = r;
        }
        AssignDirections();
        Log.Info("İşaret güncellendi: " + string.Join(" → ", _marks.ConvertAll(m => $"{m.Step}:{m.Label}@{m.TX:F0},{m.TY:F0}")));
    }

    public void Hide()
    {
        if (_timerOn) { Win32.KillTimer(_hwnd, (UIntPtr)TimerId); _timerOn = false; }
        if (_visible) { Win32.ShowWindow(_hwnd, Win32.SW_HIDE); _visible = false; }
        _marks = new();
    }

    /// <summary>Kare içi 0-1000 → ekran pikseli merkez + halka yarıçapı (kutu biliniyorsa kutuya uyar).</summary>
    private (double x, double y, double r) ToScreen(PointMark p)
    {
        double s = Win32.GetMonitorScale(Win32.MonitorFromPoint(
            new Win32.POINT(_left + (int)(p.X / 1000.0 * _fw), _top + (int)(p.Y / 1000.0 * _fh)), Win32.MONITOR_DEFAULTTONEAREST));
        double x = _left + p.X / 1000.0 * _fw, y = _top + p.Y / 1000.0 * _fh;
        bool arrow = p.Style == "arrow";
        double r = arrow ? 16 * s : 34 * s;
        if (p.W > 0 && p.H > 0)
        {
            double boxPx = Math.Max(p.W / 1000.0 * _fw, p.H / 1000.0 * _fh);
            r = arrow ? Math.Clamp(boxPx / 2 + 4 * s, 12 * s, 90 * s)      // ok: hedefin kenarına yakın dursun
                      : Math.Clamp(boxPx / 2 * 1.15 + 9 * s, 24 * s, 110 * s);
        }
        return (x, y, r);
    }

    // ------------------------------------------------------------------ Çizim

    private static readonly (double x, double y)[] ArrowDirs =
    {
        (0, -1), (-0.707, -0.707), (0.707, -0.707), (-1, 0), (1, 0), (0, 1), (-0.707, 0.707), (0.707, 0.707),
    };

    /// <summary>
    /// Her ok için kuyruğun (ve etiketin) ekrana sığdığı, diğer işaretlerin üstüne düşmediği ilk yönü seçer.
    /// Tercih sırası: üstten, sol/sağ üst çapraz, yanlardan, alttan.
    /// </summary>
    private void AssignDirections()
    {
        float s = (float)_scale;
        foreach (var m in _marks)
        {
            if (!m.Arrow) continue;
            double reach = m.TR + (6 + 54 + 40) * s;              // hedeften kuyruk ucu + etiket payı
            double labelReach = string.IsNullOrEmpty(m.Label) ? 0 : 150 * s;
            (double x, double y) chosen = ArrowDirs[0];
            bool found = false;
            foreach (var d in ArrowDirs)
            {
                double ex = m.TX + d.x * reach, ey = m.TY + d.y * reach;
                double margin = 24 * s;
                bool inside = ex > _mon.Left + margin && ex < _mon.Right - margin && ey > _mon.Top + margin && ey < _mon.Bottom - margin;
                if (inside && Math.Abs(d.x) > 0.5)   // yatay yönde etiket de yana taşar
                    inside = ex + d.x * labelReach > _mon.Left + margin && ex + d.x * labelReach < _mon.Right - margin;
                if (!inside) continue;
                bool clear = true;
                foreach (var o in _marks)
                {
                    if (ReferenceEquals(o, m)) continue;
                    double dist = Math.Sqrt(Math.Pow(ex - o.TX, 2) + Math.Pow(ey - o.TY, 2));
                    if (dist < o.TR + 30 * s) { clear = false; break; }
                }
                if (!clear) continue;
                chosen = d; found = true; break;
            }
            if (!found) chosen = ArrowDirs[0];
            m.DX = chosen.x; m.DY = chosen.y;
        }
    }

    private void Keep()
    {
        _lastTopmost = DateTime.UtcNow;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        if (now >= _until || _marks.Count == 0) { Hide(); return; }
        double dt = Math.Min(0.1, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        double k = 1 - Math.Exp(-dt * 12);   // ~150 ms'de oturur
        foreach (var m in _marks)
        {
            m.X += (m.TX - m.X) * k; m.Y += (m.TY - m.Y) * k; m.R += (m.TR - m.R) * k;
        }
        if ((now - _lastTopmost).TotalMilliseconds > 1500) Keep();
        Render(now);
    }

    private void EnsureSurface(int w, int h)
    {
        if (w <= _surfW && h <= _surfH && _g != IntPtr.Zero) return;
        FreeSurface();
        _surfW = Math.Max(w, _surfW);
        _surfH = Math.Max(h, _surfH);
        _dib = Win32.CreateDib32(_screenDc, _surfW, _surfH, out _bits);
        _oldBmp = Win32.SelectObject(_memDc, _dib);
        Gdip.Check(Gdip.GdipCreateBitmapFromScan0(_surfW, _surfH, _surfW * 4, Gdip.PixelFormat32bppPARGB, _bits, out _gBitmap), "MarkerSurface");
        Gdip.Check(Gdip.GdipGetImageGraphicsContext(_gBitmap, out _g), "MarkerSurfaceG");
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

    private static uint ParseHex(string? hex, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var h = hex.Trim().TrimStart('#');
        return uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? (v & 0xFFFFFF) : fallback;
    }

    private static uint Argb(int a, uint rgb) => ((uint)Math.Clamp(a, 0, 255) << 24) | (rgb & 0xFFFFFF);

    private void Render(DateTime now)
    {
        var f = _fonts;
        if (f == null) return;
        float s = (float)_scale;
        var o = _settings().Overlay;
        uint accent = ParseHex(o.AccentColor, 0xD2463C);
        float badge = 22 * s;
        float padX = 10 * s, padY = 4 * s;

        // Etiket genişlikleri → pencere sınırları (hedef konumları da kapsa ki kayarken kırpılmasın)
        var labelW = new float[_marks.Count];
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int i = 0; i < _marks.Count; i++)
        {
            var m = _marks[i];
            labelW[i] = string.IsNullOrEmpty(m.Label) ? 0 : _text.Measure(f.SmallBold, m.Label) + padX * 2;
            float ext = m.Arrow ? (float)Math.Max(m.R, m.TR) + 190 * s : (float)Math.Max(m.R, m.TR) * 2;
            foreach (var (px, py) in new[] { (m.X, m.Y), (m.TX, m.TY) })
            {
                minX = Math.Min(minX, (int)(px - ext - labelW[i]));
                maxX = Math.Max(maxX, (int)(px + ext + labelW[i]));
                minY = Math.Min(minY, (int)(py - ext));
                maxY = Math.Max(maxY, (int)(py + ext));
            }
        }
        minX = Math.Max(minX, _mon.Left); minY = Math.Max(minY, _mon.Top);
        maxX = Math.Min(maxX, _mon.Right); maxY = Math.Min(maxY, _mon.Bottom);
        int W = Math.Max(8, maxX - minX), H = Math.Max(8, maxY - minY);

        EnsureSurface(W, H);
        Gdip.GdipGraphicsClear(_g, 0);
        _ops.Clear();

        double t = (now - _start).TotalSeconds;
        double pulse = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / 0.9);
        double phase = (t % 1.3) / 1.3;

        for (int i = 0; i < _marks.Count; i++)
        {
            var m = _marks[i];
            float R = (float)m.R;
            float cx = (float)(m.X - minX), cy = (float)(m.Y - minY);

            if (m.Arrow)
            {
                DrawArrowMark(m, cx, cy, R, labelW[i], s, accent, t, f, padX, padY);
                continue;
            }

            // Genişleyip sönen dış halka
            float r2 = R * (1f + (float)phase * 0.6f);
            DrawRing(cx, cy, r2, 3 * s, Argb((int)((1 - phase) * 210), accent), Argb((int)((1 - phase) * 90), 0x000000), 5 * s);
            // Ana halka (nabız)
            float r1 = R * (0.9f + 0.1f * (float)pulse);
            DrawRing(cx, cy, r1, 4 * s, Argb(255, accent), Argb(170, 0x000000), 7.5f * s);

            // Adım rozeti (halkanın sol üstünde)
            if (_numbered)
            {
                float bx = cx - r1 * 0.72f - badge / 2, by = cy - r1 * 0.72f - badge / 2;
                FillCircle(bx, by, badge, Argb(255, accent));
                string n = m.Step.ToString(CultureInfo.InvariantCulture);
                float nw = _text.Measure(f.SmallBold, n);
                _ops.Add(new TextRenderer.Op { Text = n, X = bx + (badge - nw) / 2, Y = by + (badge - f.SmallLine) / 2, Font = f.SmallBold, Color = 0xFFFFFFFF, LineHeight = f.SmallLine });
            }

            // Etiket hapı: sağda, yer yoksa solda
            if (labelW[i] > 0)
            {
                float lh = f.SmallLine + padY * 2;
                float lx = cx + r1 + 6 * s;
                if (lx + labelW[i] > W) lx = cx - r1 - 6 * s - labelW[i];
                float ly = cy - lh / 2;
                FillRoundRect(lx, ly, labelW[i], lh, lh / 2, Argb(225, 0x0D1015));
                _ops.Add(new TextRenderer.Op { Text = m.Label, X = lx + padX, Y = ly + padY, Font = f.SmallBold, Color = 0xFFFFFFFF, LineHeight = f.SmallLine });
            }
        }
        _text.Draw(_g, _ops, float.MaxValue);

        // Belirme / sönme
        double fin = t / 0.15, fout = (_until - now).TotalSeconds / 0.5;
        double alpha = Math.Clamp(Math.Min(fin, fout), 0, 1);

        var dst = new Win32.POINT(minX, minY);
        var size = new Win32.SIZE(W, H);
        var srcPt = new Win32.POINT(0, 0);
        var blend = new Win32.BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            SourceConstantAlpha = (byte)Math.Clamp(alpha * 255, 0, 255),
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };
        if (!Win32.UpdateLayeredWindow(_hwnd, _screenDc, ref dst, ref size, _memDc, ref srcPt, 0, ref blend, Win32.ULW_ALPHA))
            Log.Warn("İşaret UpdateLayeredWindow başarısız: " + Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Hedefi gösteren kalın ok: ucu hedefin kenarında, kuyruğu <see cref="Mark.DX"/>/<see cref="Mark.DY"/> yönünde; hafifçe ileri-geri sallanır.
    /// Etiket hapı kuyruğun ucunda durur. Koyu dış çizgi her arka planda okunmasını sağlar.
    /// </summary>
    private void DrawArrowMark(Mark m, float cx, float cy, float R, float labelW, float s, uint accent, double t,
                               TextRenderer.Fonts f, float padX, float padY)
    {
        float dx = (float)m.DX, dy = (float)m.DY;
        float nx = -dy, ny = dx;                                   // dik vektör
        float bob = (float)(Math.Sin(t * Math.PI * 2 / 0.8) * 6 * s);
        float gap = 6 * s + Math.Max(0, bob);
        float len = 54 * s, hl = 26 * s, hw = 32 * s, sw = 11 * s;
        float tx = cx + dx * (R + gap), ty = cy + dy * (R + gap);   // ok ucu
        float bx = tx + dx * hl, by = ty + dy * hl;                 // gövde-baş birleşimi
        float ex = tx + dx * len, ey = ty + dy * len;               // kuyruk
        var pts = new[]
        {
            new Gdip.PointF(tx, ty),
            new Gdip.PointF(bx + nx * hw / 2, by + ny * hw / 2),
            new Gdip.PointF(bx + nx * sw / 2, by + ny * sw / 2),
            new Gdip.PointF(ex + nx * sw / 2, ey + ny * sw / 2),
            new Gdip.PointF(ex - nx * sw / 2, ey - ny * sw / 2),
            new Gdip.PointF(bx - nx * sw / 2, by - ny * sw / 2),
            new Gdip.PointF(bx - nx * hw / 2, by - ny * hw / 2),
        };
        Gdip.GdipCreatePen1(Argb(200, 0x000000), 6 * s, 2, out var dark);
        Gdip.GdipSetPenLineJoin(dark, 2);
        Gdip.GdipDrawPolygon(_g, dark, pts, pts.Length);
        Gdip.GdipDeletePen(dark);
        Gdip.GdipCreateSolidFill(Argb(255, accent), out var fill);
        Gdip.GdipFillPolygon(_g, fill, pts, pts.Length, 0);
        Gdip.GdipDeleteBrush(fill);
        Gdip.GdipCreatePen1(Argb(230, 0xFFFFFF), Math.Max(1.2f, 1.6f * s), 2, out var light);
        Gdip.GdipSetPenLineJoin(light, 2);
        Gdip.GdipDrawPolygon(_g, light, pts, pts.Length);
        Gdip.GdipDeletePen(light);

        // Adım rozeti: okun ucunun yanında
        if (_numbered)
        {
            float badge = 22 * s;
            float bcx = tx + nx * (hw / 2 + badge / 2 + 2 * s), bcy = ty + ny * (hw / 2 + badge / 2 + 2 * s);
            FillCircle(bcx - badge / 2, bcy - badge / 2, badge, Argb(255, accent));
            string n = m.Step.ToString(CultureInfo.InvariantCulture);
            float nw = _text.Measure(f.SmallBold, n);
            _ops.Add(new TextRenderer.Op { Text = n, X = bcx - nw / 2, Y = bcy - f.SmallLine / 2, Font = f.SmallBold, Color = 0xFFFFFFFF, LineHeight = f.SmallLine });
        }

        if (labelW > 0)
        {
            float lh = f.SmallLine + padY * 2;
            float lcx = ex + dx * 8 * s + dx * labelW / 2, lcy = ey + dy * 8 * s + dy * lh / 2;
            FillRoundRect(lcx - labelW / 2, lcy - lh / 2, labelW, lh, lh / 2, Argb(230, 0x0D1015));
            _ops.Add(new TextRenderer.Op { Text = m.Label, X = lcx - labelW / 2 + padX, Y = lcy - lh / 2 + padY, Font = f.SmallBold, Color = 0xFFFFFFFF, LineHeight = f.SmallLine });
        }
    }

    /// <summary>Koyu gölgeli halka (her arka planda okunsun diye önce geniş koyu, sonra renkli çizgi).</summary>
    private void DrawRing(float cx, float cy, float r, float width, uint color, uint shadow, float shadowWidth)
    {
        Gdip.GdipCreatePen1(shadow, shadowWidth, 2, out var p0);
        Gdip.GdipDrawEllipse(_g, p0, cx - r, cy - r, r * 2, r * 2);
        Gdip.GdipDeletePen(p0);
        Gdip.GdipCreatePen1(color, width, 2, out var p1);
        Gdip.GdipDrawEllipse(_g, p1, cx - r, cy - r, r * 2, r * 2);
        Gdip.GdipDeletePen(p1);
    }

    private void FillCircle(float x, float y, float d, uint color)
    {
        Gdip.GdipCreateSolidFill(color, out var b);
        Gdip.GdipFillEllipse(_g, b, x, y, d, d);
        Gdip.GdipDeleteBrush(b);
    }

    private void FillRoundRect(float x, float y, float w, float h, float r, uint color)
    {
        float d = Math.Min(r, Math.Min(w, h) / 2) * 2;
        Gdip.GdipCreatePath(0, out var path);
        Gdip.GdipAddPathArc(path, x, y, d, d, 180, 90);
        Gdip.GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
        Gdip.GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
        Gdip.GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
        Gdip.GdipClosePathFigure(path);
        Gdip.GdipCreateSolidFill(color, out var b);
        Gdip.GdipFillPath(_g, b, path);
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
