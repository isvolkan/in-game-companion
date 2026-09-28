using System;
using System.Text;
using System.Text.Json.Nodes;

namespace InGameCompanion.Ai;

/// <summary>
/// Akan model çıktısını üç parçaya ayırır:
///   "S: ..."            → ilk satır, oyuncunun sorusunun dökümü (HUD'da gri gösterilir)
///   cevap               → HUD'a akıtılır
///   "@@MEM {json}"      → son satır, hafıza güncellemesi (HUD'da asla gösterilmez)
/// İşaretçinin parçalar hâlinde gelebileceği durumlar için kuyruk tutulur.
/// </summary>
internal sealed class ResponseParser
{
    public const string MetaMarker = "@@MEM";

    private readonly StringBuilder _head = new();
    private bool _headDone;
    private string _tail = "";          // henüz yayınlanmamış, işaretçi başlangıcı olabilecek kuyruk
    private bool _inMeta;
    private readonly StringBuilder _meta = new();

    public string Question { get; private set; } = "";
    public StringBuilder Answer { get; } = new();
    public string MetaRaw => _meta.ToString();

    public event Action<string>? QuestionParsed;
    public event Action<string>? AnswerDelta;

    public void Feed(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        if (_headDone) { ProcessAnswer(chunk); return; }

        _head.Append(chunk);
        var text = _head.ToString();
        var trimmed = text.TrimStart();

        bool looksLikeQuestion = trimmed.Length < 2
            || trimmed.StartsWith("S:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("S :", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeQuestion) { _headDone = true; ProcessAnswer(text); return; }

        int nl = text.IndexOf('\n');
        if (nl < 0)
        {
            if (text.Length > 300) { _headDone = true; ProcessAnswer(text); }
            return;
        }

        _headDone = true;
        var q = text[..nl].Trim();
        int colon = q.IndexOf(':');
        Question = colon >= 0 ? q[(colon + 1)..].Trim() : q;
        QuestionParsed?.Invoke(Question);
        var rest = text[(nl + 1)..].TrimStart('\r', '\n');
        if (rest.Length > 0) ProcessAnswer(rest);
    }

    /// <summary>Akış bitti; bekleyen tamponları boşalt.</summary>
    public void Flush()
    {
        if (!_headDone)
        {
            _headDone = true;
            var text = _head.ToString().Trim();
            if (text.StartsWith("S:", StringComparison.OrdinalIgnoreCase))
            {
                var nl = text.IndexOf('\n');
                Question = (nl < 0 ? text[2..] : text[2..nl]).Trim();
                QuestionParsed?.Invoke(Question);
                if (nl >= 0) ProcessAnswer(text[(nl + 1)..]);
            }
            else if (text.Length > 0) ProcessAnswer(text);
        }
        if (!_inMeta && _tail.Length > 0)
        {
            Emit(_tail.TrimEnd());
            _tail = "";
        }
    }

    /// <summary>@@MEM satırındaki JSON nesnesi (yoksa ya da bozuksa null).</summary>
    public JsonObject? ParseMeta()
    {
        var raw = _meta.ToString();
        int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try { return JsonNode.Parse(raw[a..(b + 1)]) as JsonObject; }
        catch { return null; }
    }

    private void ProcessAnswer(string s)
    {
        if (_inMeta) { _meta.Append(s); return; }

        _tail += s;
        int idx = _tail.IndexOf(MetaMarker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            Emit(_tail[..idx].TrimEnd());
            _meta.Append(_tail[(idx + MetaMarker.Length)..]);
            _tail = "";
            _inMeta = true;
            return;
        }

        int keep = HoldBack(_tail);
        if (keep < _tail.Length)
        {
            Emit(_tail[..^keep]);
            _tail = keep == 0 ? "" : _tail[^keep..];
        }
    }

    /// <summary>
    /// Kuyrukta tutulacak karakter sayısı: sondaki boşluk/satır sonları + işaretçinin olası başlangıcı ("@", "@@", "@@M"...).
    /// Böylece "@@MEM" parçalı gelse bile ekrana bir an bile basılmaz.
    /// </summary>
    private static int HoldBack(string t)
    {
        int prefix = 0;
        for (int k = Math.Min(MetaMarker.Length - 1, t.Length); k >= 1; k--)
        {
            if (t.EndsWith(MetaMarker[..k], StringComparison.Ordinal)) { prefix = k; break; }
        }
        int i = t.Length - prefix;
        while (i > 0 && char.IsWhiteSpace(t[i - 1])) i--;
        return t.Length - i;
    }

    private void Emit(string s)
    {
        if (s.Length == 0) return;
        Answer.Append(s);
        AnswerDelta?.Invoke(s);
    }
}
