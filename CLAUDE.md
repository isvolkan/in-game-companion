# CLAUDE.md — Oyun Asistanı (In-Game AI Companion)

> Bu dosya Claude Code içindir. Proje ilk olarak Claude Cowork'te geliştirildi (v0.1 → v0.2.1).
> Buradaki bilgiler o oturumdaki kararları, bağlamı ve açık işleri aktarır. Yeni oturuma başlamadan önce baştan sona oku.

## 1. Proje ne yapıyor

Windows için masaüstü yardımcısı. Oyuncu hikâyeli, RPG ve açık dünya oyunlarında (ilk hedef **Crimson Desert**) oyunu durdurmadan soru sorar.

1. **Mouse 5** (XButton2) basılı tutulur. O anda oyun penceresinin ekran görüntüsü alınır ve mikrofon kaydı başlar.
2. Oyuncu sorusunu sesli söyler, sonra tuşu bırakır.
3. Tam kare, odak kırpması ve WAV sesi tek istekte **Gemini**'ye gider. Google Search grounding açıktır.
4. Cevap sağ üst köşedeki yarı saydam kutuda kelime kelime akar. Bittikten sonra okuma süresine göre (10–28 sn) kaybolur.
5. Mouse 5'e kısa dokunmak (<350 ms) kutuyu kapatır ve süren isteği iptal eder.

HUD'ın uyması gereken kurallar:

- Tıklamalar kutudan geçip oyuna gider.
- Kutu odağı oyundan almaz.
- Kutu kendi ekran görüntülerine girmez.
- Oyun **Borderless Windowed** modunda çalışmalı; Exclusive Fullscreen desteklenmez.

Yapay zekanın uyması gereken kurallar:

- Sıkı spoiler kalkanı.
- Kısa cevap: en fazla 3 madde veya yaklaşık 70 kelime.
- Cevap Türkçe, ama eşya, görev ve yer adları ekranda nasıl yazıyorsa öyle kalır.
- Diyalog seçenekleri istenirse spoiler vermeden analiz edilir.
- Bulmacalarda "ipucu" modu vardır.

## 2. Kullanıcı bağlamı

- Kullanıcı: **Serkan**. Türkçe konuşur, cevaplar ve UI metinleri **Türkçe** olmalı.
- Windows 11 kullanıyor, iki monitörü var: oyun solda, masaüstü sağda.
- Crimson Desert'ı **Türkçe arayüzle** oynuyor. Örnek HUD metinleri: görev "İsyan ya da Devrim", bölge "Serkis Toprakları / Altın Yaprak Ticaret Merkezi".
- Oyun işlemi `CrimsonDesert.exe`. Oyun tek oyunculu, yani global kanca için anti-cheat riski yok.
- Proje klasörü: `C:\Users\Serkan\Desktop\in-game`. Git deposu; sürümler `vX.Y.Z` etiketiyle, değişiklikler `CHANGELOG.md`'de.
- Kullanıcı mimari kararların tamamını bize bıraktı ("tüm kararları sen ver"). Yine de büyük yön değişikliklerinden önce ona sor.

## 3. Mevcut durum (29.09.2026)

| Konu | Durum |
|---|---|
| Derleme (`build.bat`) | ✅ Kullanıcının makinesinde başarılı |
| Fare kancası | ✅ Kuruluyor (`Fare kancası kuruldu (Mouse 5, swallow=True)`) |
| Mikrofon | ✅ Çalışıyor. Bazı kayıtlar sessiz (peak 6–8); büyük ihtimalle konuşulmayan denemeler |
| API anahtarı | ✅ Geçerli (ısınma isteği 200). Anahtar yeni `AQ.` formatında |
| **Uçtan uca cevap** | ⚠️ 429 nedeni bulundu ve v0.2.2'de düzeltildi (aşağıya bak). Gerçek oyunda henüz denenmedi |
| HUD çizimi, odak, click-through | ⚠️ Gerçek oyunda doğrulanmadı. Başlangıçta "hazır" bilgi kutusu ve hata kutusu görüldü |
| Hafıza (v0.2) | ✅ Linux'ta birim testleri geçti. Gerçek kullanımda doğrulanmadı |
| Ayar dosyasını otomatik yeniden yükleme (v0.2.1) | ✅ Kodda var. Kullanıcı henüz yeniden derlemedi |

### ✅ Çözülen sorun: Gemini 429 (v0.2.2)

Teşhis (curl ile doğrulandı): `gemini-3.8-flash` ve `gemini-3.5-flash-lite` aramasız 200 dönüyor,
`{"googleSearch":{}}` eklenince ikisi de 429 veriyor. Yani ücretsiz katmanda **Google Search grounding kotası yok**.
Çözüm `GeminiClient.AskStreamAsync` içinde: 429 + arama açık → aramasız tekrar, arama 30 dk kapalı;
sonra 429/404 → `FallbackModel`. HUD alt satırında uyarı gösterilir. Kalıcı çözüm: AI Studio'da faturalandırma (Tier 1).
Kullanıcının limitleri: https://aistudio.google.com/rate-limit

## 4. Mimari kararlar (değiştirmeden önce nedenini oku)

| Karar | Neden |
|---|---|
| C# / .NET 8, `net8.0` hedefi (`-windows` değil), **sıfır NuGet bağımlılığı** | Her şey P/Invoke ile yazıldı: user32, gdi32, gdiplus, winmm, dwmapi, shell32, shlwapi. Cowork sandbox'ında NuGet engelliydi. Ayrıca ortaya küçük ve bağımlılıksız bir exe çıkıyor. **WPF, WinForms ya da NAudio eklemeden önce kullanıcıya sor.** |
| Overlay: `WS_EX_LAYERED \| TRANSPARENT \| NOACTIVATE \| TOOLWINDOW \| TOPMOST` + `UpdateLayeredWindow` (piksel başına alfa) + GDI+ ile çizim | Click-through ve odak çalmama garantisi. `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` sayesinde kutu kendi yakalamalarımıza girmez. TOPMOST 1,5 sn'de bir yeniden uygulanır |
| Metin dizgisi elle yazıldı (`TextRenderer`) | GDI+ `DrawString` karışık stil desteklemiyor. `**kalın**`, madde, numaralı adım ve kelime kaydırma elle yapılıyor |
| Fare kancası `WH_MOUSE_LL`, **ayrı iş parçacığı + kendi mesaj döngüsü**. Olaylar tek tüketicili `BlockingCollection` ile sıralı dağıtılıyor | Windows yavaş kancaları sessizce söker; ayrıca basış/bırakış sırası korunmalı. `SwallowHotkey=true` ise tuş oyuna iletilmez |
| Ekran yakalama: ekran DC'sinden `BitBlt`, oyun penceresinin DWM çerçevesiyle monitör kesişimi | Borderless modda güvenilir ve 10–30 ms sürüyor. HDR'da görüntü soluk çıkar; ileride Windows.Graphics.Capture'a geçilebilir |
| İki görüntü: 1600 px tam kare + 900 px tam çözünürlüklü **odak kırpması** (imleç görünüyorsa imleç, yoksa ekran merkezi) | Küçük HUD ve eşya yazıları okunabilsin |
| Yakalama **tuşa basıldığı anda** arka planda başlar | Oyuncu konuşurken JPEG kodlama biter |
| Mikrofon: `waveIn` (CALLBACK_EVENT), 16 kHz / 16-bit / mono, RAM'de. Yalnızca basılıyken açık. Bırakınca 180 ms kuyruk kaydı alınır | Gizlilik ve düşük gecikme. Ayrı STT yok, ham ses Gemini'ye gider |
| Sessiz kayıt kontrolü: `peak < 250` ise istek gönderilmez | Boşa API çağrısı yapılmasın |
| Gemini REST `v1beta/models/{model}:streamGenerateContent?alt=sse`, alanlar camelCase | `thinkingConfig.thinkingLevel = "low"`. Gemini 3.x `temperature`, `top_p`, `top_k` ve `candidate_count` kabul etmez, **ekleme**. Son kullanıcı turu boş olmayan bir metinle bitmeli. Araç: `{"googleSearch":{}}` |
| Model çıktı protokolü: ilk satır `S: <soru dökümü>`, ardından cevap, en sonda `@@MEM {json}` | Ayrı STT gerekmeden soru metni elde edilir. `@@MEM` satırı ek API çağrısı olmadan hafızayı besler. `ResponseParser`, işaretçi parçalı gelse bile onu ekrana hiç basmaz (hold-back kuyruğu) |
| Hafıza: oyun başına `dist/memory/<Oyun>.json` (atomik yazım: tmp dosyası + move) | Otomatik ilerleme (bölüm, görev, bölge), oyuncu notları, son soru-cevaplar. 12 kayıt birikince eskiler arka planda `gemini-3.5-flash-lite` ile en fazla 120 kelimelik özete sıkıştırılır |
| Spoiler sınırı = elle not (`Games[x].ProgressNote` ya da genel `ProgressNote`) + otomatik takip | Sen ilerledikçe sınır da kayar |
| Oyun tanıma: ön plandaki işlem adı → `Games` sözlüğü (büyük/küçük harf duyarsız) → `games/*.md` profili | Aramalar hedefli olur; Crimson Desert, aynı stüdyonun Black Desert oyunuyla karıştırılmaz |

## 5. Dosya haritası

```
InGameCompanion.csproj    net8.0 WinExe, AllowUnsafeBlocks, games/** çıktıya kopyalanır
app.manifest              PerMonitorV2 DPI, asInvoker
build.bat                 .NET 8 SDK yoksa winget ile kurar, dist\'e publish eder ve başlatır (CRLF!)
games/CrimsonDesert.md    Oyun profili (spoiler içermeyen arka plan + tercih edilen kaynaklar)
src/Program.cs            Giriş noktası: tek örnek mutex, bileşenleri kurar, tepsi menüsü,
                          settings.json için FileSystemWatcher (600 ms debounce), ana mesaj döngüsü
src/Core/Settings.cs      Tüm ayarlar + varsayılanlar; yoksa oluşturur; yeni alanları dosyaya ekler
src/Core/Companion.cs     Ana akış: OnPressed / OnReleased / ProcessAsync / SummarizeAsync
src/Core/GameDetector.cs  Ön plandaki işlem → GameContext (ad, profil, ProgressNote)
src/Core/GameMemoryStore.cs  Kalıcı oyun hafızası (ApplyMeta, AddExchange, TakeForSummary, ApplySummary)
src/Core/Log.cs           dist\logs\companion-YYYYMMDD.log
src/Input/MouseHook.cs    LL fare kancası + sıralı olay dağıtıcısı
src/Media/ScreenCapture.cs  BitBlt + GDI+ ölçekleme, kırpma ve JPEG
src/Media/MicRecorder.cs  waveIn kaydı → WAV
src/Ai/GeminiClient.cs    SSE akışı, grounding kaynakları, token sayıları, GenerateTextAsync (özet için)
src/Ai/Prompt.cs          Sistem talimatı (Türkçe) + tur bağlamı
src/Ai/ResponseParser.cs  S: / cevap / @@MEM ayrıştırıcı
src/Ai/ConversationMemory.cs  Sadece `ChatTurn` kaydı (dosya adı tarihsel)
src/Native/Win32.cs       P/Invoke bildirimleri
src/Native/Gdip.cs        GDI+ flat API + bellek içi JPEG kodlayıcı (SHCreateMemStream)
src/Ui/OverlayWindow.cs   HUD durum makinesi: Hidden/Listening/Thinking/Streaming/Done/Error/Info, daktilo, solma
src/Ui/TextRenderer.cs    Kelime kaydırma + mini markdown
src/Ui/TrayIcon.cs        Shell_NotifyIcon + sağ tık menüsü
src/Ui/HistoryPanel.cs    (v0.3) Önceki sorular paneli: tıklanabilir, odak alan ayrı katmanlı pencere
src/Ai/LiveTranscriber.cs (v0.3) Gemini Live WebSocket ile konuşurken canlı yazı
src/Ui/MarkerOverlay.cs   (v0.4) "Şuna bas" işaretleri: tıklamayı geçiren küçük katmanlı pencere, nabız halkası + etiket
```

v0.4 notları: `HistoryPanel` artık sohbet paneli (yazı kutusu, `Submitted` olayı, `BeginPending/AppendPending/EndPending`).
`Companion.AskTyped` yazılı soruyu işler (ekran görüntüsü `panel.ReturnTarget` penceresinden, odak kırpması yok). Cevap uzunluğu `Prompt.LengthRule` içinde.
`ResponseParser` artık genel `@@` önekini gizler: `@@MEM` (hafıza) ve `@@POINT` (işaret, 0–1000 ölçeği, HER ZAMAN Görüntü 1'e göre).
`GeminiClient.AskStreamAsync(..., onReset)`: akış ortasında kopup yeniden denenirse tüketici kısmi metni atmalı (`ResponseParser.Reset`).

## 6. İş parçacığı kuralları (kırma!)

- **OverlayWindow** üyeleri yalnızca ana (UI) iş parçacığında çalışır. Diğer iş parçacıklarından her zaman `overlay.Invoke(o => ...)` ile çağır.
- **Kanca geri çağrısı** mikro saniyeler içinde dönmeli. İçinde ağ, disk ya da kilit bekleme olmamalı. `ShouldHandle` yalnızca önbellekli işlem adı kontrolü yapar.
- **Companion**: `_session` sayacı her basışta artar. Eski oturumun geri çağrıları `id != _session` kontrolüyle düşürülür. Yeni basış `_cts`'yi iptal eder.
- **GameMemoryStore** kendi kilidiyle iş parçacığı güvenlidir.
- **HistoryPanel** da UI iş parçacığında yaşar; dışarıdan `overlay.Invoke(_ => panel.X())` ile çağır. `IsOpen` her yerden okunabilir.
  Panel HUD kuralının bilinçli istisnasıdır: kullanıcı açınca odağı alır, kapanınca `SetForegroundWindow(oyun)`.
- **LiveTranscriber**: `Push` mikrofon iş parçacığından gelir (bloklamaz, kanal). Bağlantı `MinHoldMs` sonra kurulur (dokunuşlarda bağlanmaz).
  Protokol (doğrulandı): `setup{model}` → `setupComplete`; `realtimeInput.audio{mimeType:"audio/pcm;rate=16000"}`;
  sunucu `serverContent.interimInputTranscription.text` (birikimli) ve bitişte `inputTranscription.text` + `generationComplete`.

## 7. Derleme ve çalıştırma

```bat
build.bat                                   :: tam derleme + dist\ + başlat
dotnet build -c Release                     :: hızlı derleme kontrolü
dotnet publish InGameCompanion.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

- Çalışan bir `InGameCompanion.exe` varsa publish dosyayı üzerine yazamaz. Önce tepsiden **Çıkış** yapılmalı (tek örnek mutex var).
- Çalışma zamanında oluşan dosyalar `dist\` altında: `settings.json` (**ApiKey içerir, asla commit etme ve loglama**), `logs\`, `memory\`.
- Hata ayıklama:
  - `Capture.SaveDebugCaptures: true` ile modele giden görüntüler `logs\captures` altına kaydedilir.
  - Log'daki her sorgu satırı şunları içerir: yakalama süresi, ilk token süresi, toplam süre, token sayıları, arama sorguları, `S:`, `C:` ve `M:` satırları.
- Test projesi henüz yok. Cowork'te `ResponseParser`, `GameMemoryStore` ve `Prompt` geçici bir konsol projesiyle test edildi. **Öneri:** `tests/` altına xUnit projesi ekle, bu üç sınıfı kapsasın. Hepsi Win32'ye dokunmadan test edilebilir.

## 8. Kod kuralları

- Kullanıcıya görünen metinler ve yorumlar **Türkçe**.
- Tüm tipler `internal`. `Nullable` açık. Derleme **sıfır uyarı** ile geçmeli.
- Yeni P/Invoke'ları `Win32.cs` / `Gdip.cs` içine ekle. `DllImport` kullan (LibraryImport değil, tutarlılık için).
- Ayar eklerken `Settings.cs`'ye varsayılan değerle ekle. Eski dosyalar kendiliğinden çalışır. Yeni bir üst düzey bölüm eklersen `Settings.Load` içindeki "dosyaya ekle" kontrolünü güncelle.
- `build.bat` **CRLF** satır sonlarıyla kalmalı.

## 9. Yol haritası (öncelik sırasıyla)

1. **429 ve yedek model** (bkz. §3). Uçtan uca ilk başarılı cevabı al, HUD'ı gerçek oyunda doğrula: odak, click-through, topmost, DPI, sol monitör konumu.
2. Gecikme ölçümü ve iyileştirme. Hedef: bırakıştan ilk kelimeye < 2 sn. Log'daki `ilkToken` değerine bak. Denenebilecekler: `thinkingLevel`, aramayı kapatma, JPEG boyutu, flash-lite.
3. Sesli cevap (Gemini TTS, `gemini-3.8-flash-lite-tts`) ve oyun sesini kısma. İsteğe bağlı olsun.
4. HDR-uyumlu yakalama: Windows.Graphics.Capture ya da tone-mapping.
5. Kademeli ipucu (aynı soruya tekrar basınca bir sonraki seviye) ve gamepad tuş kombinasyonu (XInput).
6. Çıkış spoiler filtresi: ikinci, ucuz model ile denetim; isteğe bağlı.
7. Uzun cevaplar için sayfalama. Kutu click-through olduğu için kaydırılamaz.
8. `git init` ve `.gitignore` zaten hazır (`dist/`, `settings.json`, `logs/` hariç tutuluyor).

## 10. Kaynaklar

- Gemini modelleri: https://ai.google.dev/gemini-api/docs/models
- Gemini 3.8 Flash notları (thinkingLevel, kaldırılan parametreler): https://ai.google.dev/gemini-api/docs/generate-content/latest-model
- Ses girişi: https://ai.google.dev/gemini-api/docs/audio (saniye başına 32 token, istek başına en fazla 20 MB inline)
- Hız limitleri: https://ai.google.dev/gemini-api/docs/rate-limits
- Not: Google `generateContent`'i "legacy" olarak işaretliyor; yeni API **Interactions API**. İleride geçiş değerlendirilebilir.
- Crimson Desert kaynakları: Fextralife wiki, Game8, PowerPyx (bkz. `games/CrimsonDesert.md`)
