using System;
using System.Collections.Generic;
using System.Text;
using InGameCompanion.Native;

namespace InGameCompanion.Ui;

/// <summary>
/// GDI+ üzerinde minik bir zengin metin dizgi motoru: kelime kaydırma, **kalın**, madde işaretleri,
/// numaralı adımlar. Önce ölçüp çizim komutları (Op) üretir, sonra hepsini tek geçişte çizer.
/// </summary>
internal sealed class TextRenderer : IDisposable
{
    public struct Op
    {
        public string Text;
        public float X, Y;
        public IntPtr Font;
        public uint Color;
        public float LineHeight;
    }

    public sealed class Fonts : IDisposable
    {
        public IntPtr Body, Bold, Small, SmallItalic, SmallBold;
        public float BodyLine, SmallLine;
        public double Scale;

        public void Dispose()
        {
            foreach (var f in new[] { Body, Bold, Small, SmallItalic, SmallBold })
                if (f != IntPtr.Zero) Gdip.GdipDeleteFont(f);
            Body = Bold = Small = SmallItalic = SmallBold = IntPtr.Zero;
        }
    }

    private readonly IntPtr _format;          // GenericTypographic + NoWrap + MeasureTrailingSpaces
    private readonly IntPtr _measureBitmap;
    private readonly IntPtr _measureGraphics;
    private readonly Dictionary<(IntPtr, string), float> _widthCache = new();

    public IntPtr Format => _format;

    public TextRenderer()
    {
        Gdip.Check(Gdip.GdipStringFormatGetGenericTypographic(out IntPtr generic), "GenericTypographic");
        Gdip.Check(Gdip.GdipCloneStringFormat(generic, out _format), "CloneFormat");
        Gdip.GdipSetStringFormatFlags(_format,
            Gdip.StringFormatFlagsNoWrap | Gdip.StringFormatFlagsMeasureTrailingSpaces | Gdip.StringFormatFlagsNoClip);
        Gdip.Check(Gdip.GdipCreateBitmapFromScan0(4, 4, 0, Gdip.PixelFormat32bppPARGB, IntPtr.Zero, out _measureBitmap), "MeasureBmp");
        Gdip.Check(Gdip.GdipGetImageGraphicsContext(_measureBitmap, out _measureGraphics), "MeasureG");
        Gdip.GdipSetTextRenderingHint(_measureGraphics, Gdip.TextRenderingHintAntiAlias);
    }

    public Fonts CreateFonts(string family, float sizeDip, double scale)
    {
        IntPtr fam = IntPtr.Zero;
        foreach (var name in new[] { family, "Segoe UI", "Arial" })
        {
            if (!string.IsNullOrWhiteSpace(name) && Gdip.GdipCreateFontFamilyFromName(name, IntPtr.Zero, out fam) == 0) break;
            fam = IntPtr.Zero;
        }
        if (fam == IntPtr.Zero) throw new InvalidOperationException("Hiçbir yazı tipi yüklenemedi");

        float body = (float)(sizeDip * scale);
        float small = (float)(sizeDip * 0.8 * scale);
        var f = new Fonts { Scale = scale };
        try
        {
            Gdip.Check(Gdip.GdipCreateFont(fam, body, Gdip.FontStyleRegular, Gdip.UnitPixel, out f.Body), "Font");
            Gdip.Check(Gdip.GdipCreateFont(fam, body, Gdip.FontStyleBold, Gdip.UnitPixel, out f.Bold), "FontB");
            Gdip.Check(Gdip.GdipCreateFont(fam, small, Gdip.FontStyleRegular, Gdip.UnitPixel, out f.Small), "FontS");
            Gdip.Check(Gdip.GdipCreateFont(fam, small, Gdip.FontStyleItalic, Gdip.UnitPixel, out f.SmallItalic), "FontSI");
            Gdip.Check(Gdip.GdipCreateFont(fam, small, Gdip.FontStyleBold, Gdip.UnitPixel, out f.SmallBold), "FontSB");
            Gdip.GdipGetFontHeight(f.Body, _measureGraphics, out float bh);
            Gdip.GdipGetFontHeight(f.Small, _measureGraphics, out float sh);
            f.BodyLine = bh * 1.12f;
            f.SmallLine = sh * 1.1f;
        }
        finally { Gdip.GdipDeleteFontFamily(fam); }
        _widthCache.Clear();
        return f;
    }

    public float Measure(IntPtr font, string text)
    {
        if (text.Length == 0) return 0;
        if (_widthCache.TryGetValue((font, text), out var w)) return w;
        var layout = new Gdip.RectF(0, 0, 100000, 10000);
        Gdip.GdipMeasureString(_measureGraphics, text, text.Length, font, ref layout, _format, out var bounds, out _, out _);
        w = bounds.Width;
        if (_widthCache.Count > 4000) _widthCache.Clear();
        _widthCache[(font, text)] = w;
        return w;
    }

    /// <summary>Düz metni (tek stil) kelime kaydırarak yerleştirir.</summary>
    public void LayoutPlain(string text, IntPtr font, float lineH, uint color, float x0, float maxW, ref float y, List<Op> ops)
    {
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            float x = x0;
            float space = Measure(font, " ");
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                float ww = Measure(font, word);
                if (x > x0 && x + ww > x0 + maxW) { y += lineH; x = x0; }
                ops.Add(new Op { Text = word, X = x, Y = y, Font = font, Color = color, LineHeight = lineH });
                x += ww + space;
            }
            y += lineH;
        }
    }

    private readonly struct Piece
    {
        public readonly string Text;
        public readonly bool Bold;
        public Piece(string t, bool b) { Text = t; Bold = b; }
    }

    /// <summary>Hafif markdown: satır başı "- ", "* ", "• " madde; "1." numaralı; **kalın**.</summary>
    public void LayoutRich(string text, Fonts f, uint color, uint bulletColor, uint boldColor,
                           float x0, float maxW, ref float y, List<Op> ops)
    {
        float lineH = f.BodyLine;
        float paraGap = (float)(4 * f.Scale);
        bool any = false;
        float space = Measure(f.Body, " ");

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (any) y += lineH * 0.35f;
                continue;
            }
            // Model yine de başlık yazarsa işaretleri at
            while (line.StartsWith('#')) line = line[1..].TrimStart();

            string? prefix = null;
            string content = line;
            if ((line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("• ")) && line.Length > 2)
            {
                prefix = "•";
                content = line[2..].TrimStart();
            }
            else
            {
                int i = 0;
                while (i < line.Length && i < 2 && char.IsDigit(line[i])) i++;
                if (i > 0 && i < line.Length - 1 && (line[i] == '.' || line[i] == ')') && line[i + 1] == ' ')
                {
                    prefix = line[..(i + 1)];
                    content = line[(i + 2)..].TrimStart();
                }
            }

            float indent = 0;
            if (prefix != null)
            {
                bool isBullet = prefix == "•";
                IntPtr pf = isBullet ? f.Body : f.Bold;
                ops.Add(new Op { Text = prefix, X = x0, Y = y, Font = pf, Color = bulletColor, LineHeight = lineH });
                indent = isBullet ? (float)(14 * f.Scale) : Measure(f.Bold, prefix) + (float)(6 * f.Scale);
            }

            float lineStart = x0 + indent;
            float x = lineStart;
            foreach (var word in SplitWords(content))
            {
                float ww = 0;
                foreach (var p in word) ww += Measure(p.Bold ? f.Bold : f.Body, p.Text);
                if (x > lineStart && x + ww > x0 + maxW) { y += lineH; x = lineStart; }
                foreach (var p in word)
                {
                    var font = p.Bold ? f.Bold : f.Body;
                    ops.Add(new Op { Text = p.Text, X = x, Y = y, Font = font, Color = p.Bold ? boldColor : color, LineHeight = lineH });
                    x += Measure(font, p.Text);
                }
                x += space;
            }
            y += lineH + paraGap;
            any = true;
        }
        if (any) y -= paraGap;
    }

    private static List<List<Piece>> SplitWords(string s)
    {
        var words = new List<List<Piece>>();
        var cur = new List<Piece>();
        var sb = new StringBuilder();
        bool bold = false;

        void FlushPiece()
        {
            if (sb.Length > 0) { cur.Add(new Piece(sb.ToString(), bold)); sb.Clear(); }
        }
        void FlushWord()
        {
            FlushPiece();
            if (cur.Count > 0) { words.Add(cur); cur = new List<Piece>(); }
        }

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
            {
                FlushPiece();
                bold = !bold;
                i++;
                continue;
            }
            if (c == ' ' || c == '\t') { FlushWord(); continue; }
            sb.Append(c);
        }
        FlushWord();
        return words;
    }

    public void Draw(IntPtr g, List<Op> ops, float maxBottom)
    {
        var brushes = new Dictionary<uint, IntPtr>();
        try
        {
            foreach (var op in ops)
            {
                if (op.Y + op.LineHeight > maxBottom) continue;
                if (!brushes.TryGetValue(op.Color, out var b))
                {
                    Gdip.GdipCreateSolidFill(op.Color, out b);
                    brushes[op.Color] = b;
                }
                var r = new Gdip.RectF(op.X, op.Y, 0, 0);
                Gdip.GdipDrawString(g, op.Text, op.Text.Length, op.Font, ref r, _format, b);
            }
        }
        finally
        {
            foreach (var b in brushes.Values) Gdip.GdipDeleteBrush(b);
        }
    }

    public void Dispose()
    {
        Gdip.GdipDeleteGraphics(_measureGraphics);
        Gdip.GdipDisposeImage(_measureBitmap);
        Gdip.GdipDeleteStringFormat(_format);
    }
}
