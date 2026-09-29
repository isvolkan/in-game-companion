using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using InGameCompanion.Ai;
using InGameCompanion.Core;
using InGameCompanion.Native;

namespace InGameCompanion.Ui;

/// <summary>
/// "Şuna bas, şuraya git" işaretleri: modelin gösterdiği noktalara nabız gibi atan halka + etiket çizer.
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

    private sealed record Mark(int X, int Y, string Label, int Step);
    private List<Mark> _marks = new();
    private bool _numbered;
    private DateTime _start, _until, _lastTopmost;
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
    /// Noktaları (0-1000 ölçeği, yakalanan karenin sol üstüne göre) ekran pikseline çevirip gösterir.
    /// <paramref name="left"/>/<paramref name="top"/>/<paramref name="width"/>/<paramref name="height"/>: yakalanan alanın ekrandaki konumu.
    /// </summary>
    public void Show(IReadOnlyList<PointMark> points, int left, int top, int width, int height, double seconds)
    {
        if (points.Count == 0 || width < 16 || height < 16) { Hide(); return; }

        var marks = new List<Mark>();
        foreach (var p in points)
            marks.Add(new Mark(left + (int)Math.Round(p.X / 1000.0 * width), top + (int)Math.Round(p.Y / 1000.0 * height), p.Label, p.Step));

        var mon = Win32.MonitorFromPoint(new Win32.POINT(marks[0].X, marks[0].Y), Win32.MONITOR_DEFAULTTONEAREST);
        _mon = Win32.GetMonitorRect(mon);
        double scale = Win32.GetMonitorScale(mon);
        if (_fonts == null || Math.Abs(scale - _scale) > 0.001)
        {
            _fonts?.Dispose();
            _fonts = _text.CreateFonts(_settings().Overlay.FontFamily, _settings().Overlay.FontSize, scale);
            _scale = scale;
        }

        _marks = marks;
        _numbered = marks.Count > 1;
        _start = DateTime.UtcNow;
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
        Log.Info("İşaret: " + string.Join(" → ", marks.ConvertAll(m => $"{m.Step}:{m.Label}@{m.X},{m.Y}")));
    }

    public void Hide()
    {
        if (_timerOn) { Win32.KillTimer(_hwnd, (UIntPtr)TimerId); _timerOn = false; }
        if (_visible) { Win32.ShowWindow(_hwnd, Win32.SW_HIDE); _visible = false; }
        _marks = new();
    }

    // ------------------------------------------------------------------ Çizim

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
        float R = 36 * s;                       // halka yarıçapı (modelin konum hatasını tolere eder)
        float badge = 22 * s;
        float padX = 10 * s, padY = 4 * s;

        // Etiket genişlikleri → pencere sınırları
        var labelW = new float[_marks.Count];
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int i = 0; i < _marks.Count; i++)
        {
            var m = _marks[i];
            labelW[i] = string.IsNullOrEmpty(m.Label) ? 0 : _text.Measure(f.SmallBold, m.Label) + padX * 2;
            float ext = R * 2;
            minX = Math.Min(minX, (int)(m.X - ext - labelW[i]));
            maxX = Math.Max(maxX, (int)(m.X + ext + labelW[i]));
            minY = Math.Min(minY, (int)(m.Y - ext));
            maxY = Math.Max(maxY, (int)(m.Y + ext));
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
            float cx = m.X - minX, cy = m.Y - minY;

            // Genişleyip sönen dış halka
            float r2 = R * (1f + (float)phase * 0.8f);
            DrawRing(cx, cy, r2, 3 * s, Argb((int)((1 - phase) * 210), accent), Argb((int)((1 - phase) * 90), 0x000000), 5 * s);
            // Ana halka (nabız)
            float r1 = R * (0.86f + 0.14f * (float)pulse);
            DrawRing(cx, cy, r1, 4 * s, Argb(255, accent), Argb(170, 0x000000), 7.5f * s);

            // Adım rozeti (halkanın sol üstünde)
            if (_numbered)
            {
                float bx = cx - R * 0.72f - badge / 2, by = cy - R * 0.72f - badge / 2;
                FillCircle(bx, by, badge, Argb(255, accent));
                string n = m.Step.ToString(CultureInfo.InvariantCulture);
                float nw = _text.Measure(f.SmallBold, n);
                _ops.Add(new TextRenderer.Op { Text = n, X = bx + (badge - nw) / 2, Y = by + (badge - f.SmallLine) / 2, Font = f.SmallBold, Color = 0xFFFFFFFF, LineHeight = f.SmallLine });
            }

            // Etiket hapı: sağda, yer yoksa solda
            if (labelW[i] > 0)
            {
                float lh = f.SmallLine + padY * 2;
                float lx = cx + R * 1.2f;
                if (lx + labelW[i] > W) lx = cx - R * 1.2f - labelW[i];
                float ly = cy - lh / 2;
                FillRoundRect(lx, ly, labelW[i], lh, lh / 2, Argb(225, 0x0D1015));
                _ops.Add(new TextRenderer.Op { Text = m.Label, X = lx + padX, Y = ly + padY, Font = f.SmallBold, Color = 0xFFFFFFFF, LineHeight = f.SmallLine });
            }
        }
        _text.Draw(_g, _ops, float.MaxValue);

        // Belirme / sönme
        double alpha = 1;
        double fin = t / 0.15, fout = (_until - now).TotalSeconds / 0.5;
        alpha = Math.Clamp(Math.Min(fin, fout), 0, 1);

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
