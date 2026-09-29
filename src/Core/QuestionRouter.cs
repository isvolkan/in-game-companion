using System;
using System.Globalization;

namespace InGameCompanion.Core;

/// <summary>
/// Ücretsiz katmanda güçlü modellerin günlük kotası çok az (model başına ~20 istek). Konum/işaret gerektiren sorular
/// güçlü modele, düz bilgi soruları hafif modele gider; böylece aynı kotayla çok daha fazla işaretli soru sorulabilir.
/// Karar soru metnine göre verilir (yazılı soru ya da sesli sorunun canlı yazısı); metin yoksa güçlü model kullanılır.
/// </summary>
internal static class QuestionRouter
{
    private static readonly CultureInfo Tr = new("tr-TR");

    /// <summary>Ekranda bir yer/öğe göstermeyi isteyen ifadeler (Türkçe kökler ve birkaç İngilizce).</summary>
    private static readonly string[] Keywords =
    {
        "nerede", "nerde", "nereye", "nereden", "neresi", "nerelerde",
        "göster", "goster", "gösterir", "işaret", "isaret", "işaretle",
        "hangi düğme", "hangi simge", "hangi tuş", "hangi sekme", "hangi yetenek", "hangi eşya", "hangi bölge",
        "bulabilir", " bul ", "bulur", "bulamıyorum", "bulamadım", "yerini", " yeri ", "konum", "şurası", "buraya", "şuraya",
        "tıkla", "basmam", "basayım", "basmalı", " bas ", " seç ", "seçmem",
        "where", "show me", "point", "which button", "which icon",
    };

    private static readonly string[] ScreenWords =
    {
        "ekran", "düğme", "simge", "ikon", "tuş", "sekme", "menü", "harita", "envanter", "buradaki", "şuradaki", "bu ", "şu ",
    };

    /// <summary>true: soru ekranda bir yeri göstermeyi gerektiriyor olabilir (güçlü model gerekir).</summary>
    public static bool NeedsPointer(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return true;   // bilinmiyorsa kaliteyi koru
        var q = " " + question.ToLower(Tr).Trim() + " ";
        foreach (var k in Keywords)
            if (q.Contains(k, StringComparison.Ordinal)) return true;
        // "hangisi" tek başına genel bir bilgi sorusu olabilir ("en iyi sınıf hangisi"); ekranla ilgili bir sözcükle birlikteyse işaret say
        if (q.Contains("hangisi", StringComparison.Ordinal))
            foreach (var w in ScreenWords)
                if (q.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }
}
