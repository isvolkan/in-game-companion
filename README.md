# Oyun Asistanı (In-Game AI Companion) — v0.3.0

Oyunu durdurmadan sesli soru sor, cevap sağ üst köşede şeffaf bir kutuda aksın.
İlk hedef oyun: **Crimson Desert**. The Witcher 3, BG3, Elden Ring ve Cyberpunk için de ayar hazır.

## Kurulum (bir kerelik)

1. **Gemini API anahtarı al:** https://aistudio.google.com/apikey adresinden ücretsiz bir anahtar oluştur.
2. **`build.bat`'a çift tıkla.** .NET 8 SDK yoksa winget ile kurar, projeyi derler ve `dist\InGameCompanion.exe` dosyasını başlatır.
3. Not Defteri'nde `settings.json` açılır. `"ApiKey": ""` alanına anahtarını yapıştır ve kaydet.
4. Sistem tepsisindeki ikona sağ tıkla ve **Ayarları yeniden yükle**'yi seç.
5. Crimson Desert'ta grafik ayarlarından **Borderless Windowed** (Çerçevesiz Pencere) modunu seç.

## Kullanım

| Eylem | Sonuç |
|---|---|
| **Mouse 5**'i basılı tut → konuş → bırak | Ekran + ses gönderilir, cevap akar |
| Mouse 5'e **kısa dokun** (<0,35 sn) | Kutuyu kapatır, süren isteği iptal eder |
| Mouse 5'e **çift dokun** | Önceki sorular paneli açılır: tıkla → cevabı aç/kapat, tekerlek → kaydır. Esc, Mouse 5 ya da dışarı tıklama kapatır |
| Konuşurken | Söylediklerin kutuda canlı yazı olarak görünür (`LiveTranscription`) |
| Cevap akarken yeniden basılı tut | Eski istek iptal olur, yeni soru başlar |
| Devam sorusu ("peki o nerede?") | Son 4 soru-cevap 20 dakika bağlam olarak gönderilir |
| "Bunu **hatırla**: demirci kuzey kapıda" | Not o oyunun hafızasına kaydedilir; kutuda "Hafızaya not eklendi" görünür |
| "Demirci **neredeydi**?" / "**Notlarım** neler?" | Hafızadaki notlardan cevaplar |
| "Notlarımı **sil**" | O oyunun notlarını temizler |
| "... sadece **ipucu** ver" | Bulmacalarda çözüm yerine yalnızca bir sonraki adım |
| "... **spoiler olsun**" | Sıkı spoiler kalkanını o soru için açar |

Kutu tıklamaları arkadaki oyuna geçirir, odağı oyundan almaz ve kendi ekran görüntülerine girmez.
Cevap bittikten sonra okuma süresine göre (10–28 sn) ekranda kalır, sonra kaybolur.

## Oyun başına hafıza (v0.2)

Her oyunun kendi dosyası var: `dist\memory\Crimson Desert.json`. Uygulama kapansa da silinmez. Oyunlar birbirine karışmaz.

| Ne tutulur | Nasıl dolar |
|---|---|
| **İlerleme**: bölüm, son görev, son bölge, görülen görev ve bölge listesi | Otomatik. Model ekranda **açıkça okuduğu** görev ve bölge adlarını not eder. Spoiler sınırı sen ilerledikçe kendiliğinden kayar |
| **Notlar** | Sen "hatırla / not al / kaydet" dediğinde |
| **Son soru-cevaplar** | Her sorudan sonra. Devam soruları için son 4 tanesi gönderilir |
| **Özet** | 12 kayıt birikince eskiler arka planda ucuz bir modelle (`gemini-3.5-flash-lite`) en fazla 120 kelimelik özete sıkıştırılır. İstek boyutu, maliyet ve gecikme büyümez |

Nasıl çalışıyor: Model cevabın sonuna gizli bir `@@MEM {...}` satırı ekler. Bu satır kutuda **asla görünmez**, sadece hafıza dosyasına işlenir. Log'da `M:` satırı olarak görebilirsin.

İlerlemeyi elle de yazabilirsin. Oyuna özel not `Games → CrimsonDesert → ProgressNote` alanına, bütün oyunlar için genel not `ProgressNote` alanına yazılır. İkisi otomatik takiple birlikte kullanılır.

Tepsi menüsünden **Hafıza klasörünü aç** ve **Son oyunun hafızasını sil…** seçeneklerine ulaşabilirsin.
Dosyalar düz JSON'dur; istersen elle düzenleyebilirsin.

## Mimari kararlar

| Katman | Karar | Neden |
|---|---|---|
| Dil / çalışma zamanı | C# · .NET 8, **sıfır NuGet bağımlılığı** | Tüm Windows API'leri doğrudan P/Invoke ile çağrılıyor. Hafif (~birkaç MB), WPF/Electron yükü yok |
| Tuş | `WH_MOUSE_LL` kancası, **ayrı iş parçacığı + kendi mesaj döngüsü** | Arayüz ya da ağ işi kancayı asla yavaşlatmaz (Windows yavaş kancaları söker). Tuş oyuna iletilmez (`SwallowHotkey`) |
| Ekran | DWM üzerinden BitBlt, oyun penceresiyle sınırlı | Borderless modda güvenilir ve ~10–30 ms. Yalnızca oyun penceresi alınır, diğer monitörler ve bildirimler gönderilmez |
| Görüntü | 1600 px tam kare + **900 px odak kırpması** (imleç ya da ekran merkezi) | Küçük eşya ve görev yazılarını model net okur |
| Ses | winmm `waveIn`, 16 kHz mono WAV, RAM'de | Mikrofon yalnızca tuş basılıyken açık. Bırakınca 180 ms kuyruk kaydı ile son hece kesilmez |
| Paralellik | Bastığın an yakalama + JPEG kodlama başlar | Sen konuşurken görüntü hazırlanır; bıraktığında yalnızca gönderim kalır |
| Yapay zeka | **Gemini 3.8 Flash**, `thinkingLevel: low`, **Google Search grounding**, SSE akışı | Görüntü + ham ses + web araması tek çağrıda, ayrı STT servisi gerekmez |
| Soru dökümü | Model ilk satıra `S: ...` yazar | Kutuda "ne anladığı" görünür; ayrı STT'ye gerek kalmadan geçmiş için metin elde edilir |
| HUD | Katmanlı Win32 pencere + GDI+ çizim | `WS_EX_LAYERED \| TRANSPARENT \| NOACTIVATE \| TOOLWINDOW`, `WDA_EXCLUDEFROMCAPTURE`, 1,5 sn'de bir TOPMOST yenileme |
| Daktilo | 95 karakter/sn, birikme olursa otomatik hızlanır | Akış hissi korunur, gecikme birikmez |
| Spoiler | Sistem talimatı + otomatik ilerleme takibi + elle ilerleme notu + oyun profili | `SpoilerLevel: Strict/Mild`, `ProgressNote` (genel ya da oyuna özel) |
| Hafıza | Oyun başına JSON (atomik yazım) + gizli `@@MEM` satırı + arka planda özetleme | Ek API çağrısı olmadan hafıza dolar. Özetleme ara sıra ve ucuz modelle yapılır |
| Oyun tanıma | Ön plandaki işlem adı → `Games` tablosu → `games/*.md` profili | Aramalar "Crimson Desert" diye hedeflenir, Black Desert ile karıştırılmaz |

## Ayarlar (`dist\settings.json`)

Önemli alanlar:

- `Model`: Varsayılan `gemini-3.8-flash`. Daha ucuz ve hızlı seçenek: `gemini-3.5-flash-lite`.
- `ThinkingLevel`: `low` (en hızlı), `medium` ya da `high`.
- `UseWebSearch`: Kapatırsan cevaplar daha hızlı gelir ama güncel bilgi eksik kalabilir.
- `Hotkey`: `XButton2` (Mouse 5), `XButton1` (Mouse 4) ya da `Middle`.
- `SwallowHotkey`: `true` olduğunda tuş oyuna gitmez.
- `SpoilerLevel`: `Strict` ya da `Mild`.
- `ProgressNote`: Genel ilerleme notu. Oyuna özel not için `Games → <oyun> → ProgressNote` alanını kullan.
- `Memory.Enabled`, `Memory.SummarizeAfter`, `Memory.KeepRecent`, `Memory.SummaryModel`: Hafıza ayarları.
- `Capture.SaveDebugCaptures`: `true` olduğunda gönderilen görüntüler `logs\captures` altına kaydedilir. Modelin ne gördüğünü kontrol etmek için kullanılır.
- `Overlay.*`: Genişlik, kenar boşlukları, yazı boyutu, saydamlık, vurgu rengi ve ekranda kalma süreleri.

Her sorgu `logs\companion-YYYYMMDD.log` dosyasına yazılır: yakalama süresi, ilk token gecikmesi, toplam süre, token sayıları, yapılan web aramaları, soru ve cevap.

## Bilinen sınırlar (v0.1)

- **Exclusive Fullscreen desteklenmez.** Oyun Borderless Windowed modunda olmalı.
- **HDR açıksa** ekran görüntüsü soluk çıkabilir. Okuma genelde yine çalışır.
- **Anti-cheat:** Global fare kancası kullanılıyor. Crimson Desert tek oyunculu olduğu için sorun yok, ama online oyunlarda kullanma.
- **Mikrofon izni:** Ayarlar → Gizlilik → Mikrofon → "Masaüstü uygulamalarının erişmesine izin ver" açık olmalı.

## Yol haritası

- ~~v0.2: Oyun başına kalıcı hafıza~~ ✔
- v0.3: İsteğe bağlı sesli cevap (Gemini TTS) ve oyun sesini kısma (ducking).
- v0.3: HDR tone-mapping ile yakalama (Windows.Graphics.Capture).
- v0.4: Gamepad tuş kombinasyonu ve kademeli ipucu (aynı soruya tekrar basınca bir sonraki seviye).
- v0.4: Çıkış spoiler filtresi (ikinci, ucuz model ile denetim).
