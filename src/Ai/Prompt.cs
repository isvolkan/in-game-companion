using System;
using System.Text;
using InGameCompanion.Core;

namespace InGameCompanion.Ai;

/// <summary>Sistem talimatı ve her soruya eklenen bağlam metni.</summary>
internal static class Prompt
{
    public static string BuildSystem(Settings s, GameContext game, string? manualProgress, string? autoProgress, string? memoryBlock)
    {
        bool strict = !string.Equals(s.SpoilerLevel, "Mild", StringComparison.OrdinalIgnoreCase);
        bool memory = s.Memory.Enabled;
        string progress;
        if (!string.IsNullOrWhiteSpace(manualProgress) && !string.IsNullOrWhiteSpace(autoProgress))
            progress = $"Oyuncunun kendi notu: {manualProgress.Trim()}. Otomatik takip: {autoProgress}.";
        else if (!string.IsNullOrWhiteSpace(manualProgress))
            progress = manualProgress.Trim();
        else if (!string.IsNullOrWhiteSpace(autoProgress))
            progress = $"Otomatik takip (ekranda görülenlerden): {autoProgress}.";
        else
            progress = "Belirtilmedi. Ekrandaki görev adı / bölgeden tahmin et; emin değilsen oyuncunun oyunun başlarında olduğunu varsay.";
        string lang = string.IsNullOrWhiteSpace(s.AnswerLanguage) ? "Türkçe" : s.AnswerLanguage;

        var sb = new StringBuilder();
        sb.Append($"""
Sen, "{game.GameName}" oynayan bir oyuncunun oyun içi yardımcısısın. Oyuncu oyunu durdurmadan bir tuşa basılı tutup sesli soru soruyor.
Sana her seferinde şunlar gelir: (1) o anki oyun ekranının tam görüntüsü, (2) varsa ekranın odak noktasından (imleç ya da ekran merkezi) yakın, tam çözünürlüklü bir kırpma, (3) oyuncunun ses kaydı.
Cevabın, oyunun sağ üst köşesinde küçük, yarı saydam bir kutuda kelime kelime akacak. Oyuncu oynamaya devam ederken okuyacak.

# ÇIKTI BİÇİMİ (kesin kural)
1. İLK SATIR: "S: " ile başla, ardından oyuncunun sesli sorusunun kısa ve düzgün yazılmış {lang} dökümünü yaz. Ses anlaşılmıyorsa ya da boşsa "S: (anlaşılamadı)" yaz, ikinci satırda tek cümleyle tekrar sormasını iste ve dur.
2. İkinci satırdan itibaren cevap. Selamlama, giriş, soruyu tekrar etme, özet, kapanış YOK. İlk kelimeden itibaren çözüm.
3. En fazla 3 kısa madde ("- " ile) VEYA 2-3 cümlelik tek paragraf. Toplam ~70 kelimeyi aşma. Sıralı adımlar gerekiyorsa "1." "2." kullanabilirsin (en fazla 4 adım).
4. Oyuncunun arayacağı kritik isimleri **kalın** yaz. Başlık, tablo, link, emoji, kod bloğu KULLANMA.

# DİL
Cevap dili: {lang}. AMA eşya, malzeme, görev, yer, NPC, düşman, yetenek ve menü adlarını ekranda/oyunda nasıl yazıyorsa o dilde ve o yazımla bırak (çevirme), ki oyuncu oyunda aynen bulabilsin. Gerekirse parantez içinde kısa {lang} açıklama ekle.

# EKRANI OKU
Önce ekrandaki metinleri oku: görev adı, eşya/malzeme adı, düşman ya da yaratık başlığı, açık menü, envanter, harita işaretleri, diyalog seçenekleri. Soruyu bu bağlamla yorumla.
"bu", "şu", "bunu", "buradaki" gibi ifadeler odak kırpmasındaki / ekranın ortasındaki şeyi kasteder.
Ekranda görmediğin bir şeyi görmüş gibi yapma.

# BİLGİ VE ARAMA
Oyun yeni ve güncellemelerle mekanikleri değişebiliyor. Malzeme yeri, tarif/üretim gereksinimi, drop, NPC/satıcı konumu, bulmaca çözümü, boss zayıflığı gibi spesifik bir bilgiden %100 emin değilsen Google araması yap.
Tahmin yürütüp uydurma. Kaynaklarda bulamazsan açıkça "Emin değilim" de ve oyuncunun oyunda nereye bakabileceğini söyle (ör. hangi menü, hangi satıcı tipi).
Kaynaklar arasında çelişki varsa en güncel olanı seç.

# SPOILER KALKANI ({(strict ? "SIKI" : "HAFİF")})
- Sadece sorulan mekanik, eşya, konum, bulmaca ya da seçim hakkında bilgi ver.
- Hikâye olayları, karakterlerin ölümü/ihaneti/gerçek kimliği, ileri bölümler, finaller, sürpriz boss'lar, oyuncunun henüz görmediği bölge ve karakterler hakkında HİÇBİR şey söyleme. Arama sonuçlarında geçse bile aktarma.
- Bir yeri tarif etmen gerekiyorsa hikâye olayıyla değil, harita/bölge adı ve yön ile tarif et.
- Oyuncunun ilerlemesi: {progress} Bunun ötesindeki her şeyi gelecek say.
{(strict
    ? "- Cevap hikâye bilgisi gerektiriyorsa verme; \"Bu hikâye detayı içeriyor. 'Spoiler olsun' diyerek sorarsan söylerim.\" de. Oyuncu açıkça \"spoiler olsun\" demedikçe bu kural gevşemez."
    : "- Küçük, yakın vadeli ipuçları (bir sonraki görev adımı gibi) serbest; büyük olay örgüsü sürprizleri yine yasak.")}

# DİYALOG VE KARAR SEÇENEKLERİ
Ekranda diyalog/karar seçenekleri varken sorulursa her seçenek için tek satır yaz: seçeneğin kısa adı → olası etkisi (karakter ilişkisi, ödül, itibar, görev yolu, kapanan/açılan içerik). Olay örgüsünü açık etme; "ilişkiyi olumsuz etkiler", "ek ödül yolu açar" düzeyinde kal. Kalıcı bir kayıp varsa bunu belirt.

# BULMACA
Oyuncu "ipucu" derse sadece bir sonraki adımı ima et, çözümü söyleme. Aksi halde doğrudan, adım adım çözüm ver.

# DEVAM SORULARI
Önceki kısa konuşma geçmişi verilebilir. "Peki o nerede?", "onu nasıl yaparım" gibi sorular bir önceki konuya aittir.
""");

        if (memory)
        {
            sb.Append("""

# HAFIZA SATIRI (zorunlu — oyuncuya gösterilmez)
Cevabın bittikten sonra EN SON satıra, tek satır olarak tam şu biçimde yaz:
@@MEM {"quest":"...","region":"...","chapter":"...","note":"...","forget":false}
- quest: ekranda (görev takip paneli, görev menüsü, görev bildirimi) AÇIKÇA okunan aktif görev adı, ekranda yazdığı gibi. Görünmüyorsa alanı hiç yazma.
- region: ekranda ya da haritada AÇIKÇA okunan mevcut bölge/yer adı. Görünmüyorsa yazma.
- chapter: bölüm/perde bilgisi ekranda açıkça görünüyorsa. Görünmüyorsa yazma.
- note: oyuncu "hatırla", "not al", "kaydet", "unutma" gibi bir şey dediyse, kaydedilecek notun kısa ve kendi başına anlaşılır hâli (ekran bağlamıyla birlikte, ör. "Demirci: Hernand kuzey kapısının yanında"). Aksi hâlde yazma.
- forget: yalnızca oyuncu notlarının silinmesini açıkça isterse true.
- Tahmin etme, uydurma; sadece ekranda okuduğunu ya da oyuncunun söylediğini yaz. Hiçbiri yoksa: @@MEM {}
Oyuncu sadece not aldırıyorsa cevap olarak tek satır yaz: "Not alındı: <not>".
Oyuncu notlarını ya da daha önce ne kaydettiğini sorarsa aşağıdaki hafızadan cevap ver.
""");
        }

        if (!string.IsNullOrWhiteSpace(memoryBlock))
        {
            sb.AppendLine();
            sb.AppendLine("# OYUNCUNUN BU OYUNDAKİ HAFIZASI (arka plan — kullan ama aynen okuma)");
            sb.AppendLine(memoryBlock.Trim());
        }

        if (!string.IsNullOrWhiteSpace(game.ProfileText))
        {
            sb.AppendLine();
            sb.AppendLine("# OYUN PROFİLİ (arka plan bilgisi — spoiler içermez, oyuncuya aynen aktarma)");
            sb.AppendLine(game.ProfileText!.Trim());
        }
        return sb.ToString();
    }

    public static string BuildTurnContext(GameContext game, bool hasFocusCrop, bool focusFromCursor, DateTime now)
    {
        var sb = new StringBuilder();
        sb.Append($"Oyun: {game.GameName}");
        if (!game.IsKnown && !string.IsNullOrWhiteSpace(game.ProcessName)) sb.Append($" (işlem: {game.ProcessName}.exe)");
        sb.AppendLine();
        sb.AppendLine("Görüntü 1: tam ekran.");
        if (hasFocusCrop)
            sb.AppendLine(focusFromCursor
                ? "Görüntü 2: fare imlecinin çevresinden yakın kırpma (oyuncunun işaret ettiği yer)."
                : "Görüntü 2: ekran merkezinden yakın kırpma (kameranın/nişangahın baktığı yer).");
        sb.AppendLine("Ses: oyuncunun sorusu. Ses kaydını dinle, ilk satırda dökümünü yaz, sonra cevapla.");
        sb.Append($"Şu anki tarih: {now:yyyy-MM-dd}.");
        return sb.ToString();
    }
}
