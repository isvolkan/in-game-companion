# Sürüm Geçmişi

Her sürüm GitHub'da `vX.Y.Z` etiketiyle (tag) işaretlenir.

## v0.6.1 — 29.09.2026
- **Uydurma yasağı:** Yetenek ağacı gibi adsız simgelerde model artık düğümlere kendi bilgisinden isim yapıştırmıyor
  ("Işığı Yansıt sol alttaki yeşil düğümdür" gibi). Adı ekranda olmayan bir yetenek sorulunca işaret koymuyor ve "imleci düğümün üstüne getir, adı sağ panelde çıkar" diyor.
  Panelde açık olan yeteneğin ağaçtaki vurgulu karşılığı tarif edildi.
- **`basis` sıkılaştırıldı:** `ad` / `seçili` / `soru`; `özellik` ve `tahmin` gerekçeli işaretler gösterilmiyor.
- **Doğrulanamayan işaret:** Cevap hafif modelden geldi ve güçlü model doğrulaması yapılamadıysa, adı yazan hedefler "≈ tahmini" (kesik çizgili) gösterilir;
  yalnızca görünüşe dayananlar gizlenir. (Ölçüm: hafif modeller yoğun ekranlarda 200–650 px şaşıyor; koordinat cetveli/ızgara da yardımcı olmadı.)
- Test: tüm ağaç düğümlerini saydırma — güçlü model 45, hafif model 46 düğümü doğru yerlerde buldu (soluk/kilitli 4–5 düğüm kaçtı); adları ekranda olmadığı için boş döndü.

## v0.6.0 — 29.09.2026
- **Ok işareti:** Halkanın yanında, modelin duruma göre seçtiği kalın ok stili (`style: "arrow"`). Yön ekrana sığacak ve diğer işaretlerle çakışmayacak şekilde otomatik seçilir; sallanır, etiketi ve adım rozeti var.
- **Yoğun ekranlarda doğru düğüm:** Yetenek ağacı gibi adsız simgelerden oluşan ekranlarda hafif model başka bir düğümü gösterebiliyordu (100–370 px).
  Artık (1) güçlü modeller zincirde öncelikli, (2) cevabı hafif model verdiyse işaretler güçlü bir modelle **tam ekranda yeniden konumlandırılıyor**,
  (3) model hedefin görünüşünü (`desc`) ve dayanağını (`basis`) yazıyor; dayanağı "tahmin" olan işaret gösterilmiyor,
  (4) kırpma ince ayarı yalnızca kutusu büyük (belirsiz) hedeflerde çalışıyor ve büyük atlamaları reddediyor (eskiden doğru düğümü bozabiliyordu).
  Gerçek yetenek ağacı ekranında (uygulama uçtan uca, 2. monitörde): sapma 3–9 px.
- **Model zinciri (`ModelChain`):** Ücretsiz katmanda kota model başına **günde 20 istek**. Kotası biten model sıfırlanmaya kadar atlanır, sıradaki güçlü modele geçilir.
  503'te aynı modeli beklemek yerine hemen sıradaki modele geçilir; 14 sn içinde yanıt başlığı gelmeyen model pes edilir.
- Ayar ekranında/hata mesajında günlük kota ayrımı: "Bu modelin günlük ücretsiz kotası doldu".

## v0.5.0 — 29.09.2026
- **Daha isabetli işaret:** Model artık hedefin sıkı sınırlayıcı kutusunu verir (halkanın boyu buna uyar). Ardından hedefin çevresi
  ekrandan kırpılıp büyütülür ve model bir kez daha, yakından bakar (`PointerRefine`, tek toplu istek, `PointerRefineModel`);
  halka yeni konuma yumuşakça kayar. Gerçek harita ekranında ilk tahmin bir simgede 107 px şaşarken ince ayardan sonra 2 px'e indi.
- **Uygulama içi ayarlar:** Sohbet panelinde **Ayarlar** düğmesi (ve tepsi → **Ayarlar…**). Model, yedek model, cevap uzunlukları,
  işaret, hassaslaştırma, işaret süresi, canlı yazı, web araması, spoiler, kısayol tuşu, API anahtarı (panodan yapıştır). Anında kaydedilir.
- **Sohbet yukarıdan aşağı akar:** Eski mesajlar üstte, en yeni altta; yeni cevap gelince otomatik alta kayar.
- **`settings.json` artık gerçekten kendiliğinden yeniden yüklenir** (FileSystemWatcher, 600 ms). v0.2.1 notlarında yazıyordu
  ama depodaki kodda yoktu.
- **Kota dostu:** 429 alan model, yanıttaki bekleme süresi kadar atlanır (her soruda boşuna denenmez). 503'te önce aynı model bir kez daha denenir.
- Yedek model uyarısı artık varsayılan olarak kutuda gösterilmez (`ShowModelNotice`); log'a yazılmaya devam eder.

## v0.4.0 — 29.09.2026
- **Ekranda işaret:** Model hedefi ekranda görürse üstüne nabız gibi atan halka + etiket çizilir; çok adımlı yönlendirmede numaralı.
  Gizli `@@POINT` satırı, tıklamayı geçiren ayrı katmanlı pencere (`MarkerOverlay`). Ayarlar: `PointerMarkers`, `MarkerSeconds`.
- **Yazılı sohbet:** Mouse 5 çift dokunuşla açılan panelin altında yazı kutusu. Enter ile sor; cevap panelde akar ve
  geçmişe kaydedilir. Ekran görüntüsü yine gönderilir (odak kırpması olmadan). Ctrl+V, ok tuşları, Home/End/Delete desteklenir.
- **Cevap uzunluğu ayarı:** `AnswerLength` (sesli) ve `TypedAnswerLength` (yazılı): `Short` / `Normal` / `Detailed`; `AnswerMaxWords` ile elle kelime sınırı.
- HUD'a sığmayan uzun cevapta "Tamamı için Mouse 5'e çift dokun" ipucu; uzun modlarda kutu daha uzun süre açık kalır.
- **Düzeltme:** Gemini 5xx (ör. 503 "yoğun talep") artık 429/404 gibi yedek modele geçirir.
- **Düzeltme:** Cevabın ortasında bağlantı koparsa yedek model metni baştan yazıyordu ve ekranda metin çift çıkıyordu; kısmi metin artık atılıyor.

## v0.3.0 — 29.09.2026
- **Canlı yazı:** Konuşurken söylenenler kutuda kelime kelime görünür. Gemini Live API
  (`gemini-3.5-transcribe-live`, ücretsiz katmanda çalışıyor) üzerinden, WebSocket ile, ek bağımlılık yok.
  Ayarlar: `LiveTranscription`, `LiveTranscriptionModel`. Hata olursa sessizce kapanır, asıl soru etkilenmez.
- **Önceki sorular paneli:** Mouse 5'e çift dokununca açılır (ya da tepsi → "Önceki sorular…").
  Tıklanabilir, tekerlekle kaydırılır; açıkken odağı alır, kapanınca odağı oyuna geri verir.
- Hafızaya özetlemeden etkilenmeyen `History` listesi eklendi (oyun başına en fazla 300 kayıt, tam cevap).

## v0.2.2 — 29.09.2026
- **429 düzeltmesi:** Ücretsiz katmanda Google Search grounding kotası yok. 429 gelince istek
  otomatik olarak web aramasız tekrarlanır ve arama 30 dk boyunca kapalı tutulur.
- Yeni ayar `FallbackModel` (varsayılan `gemini-3.5-flash-lite`): ana model 429/404 verirse
  istek bu modelle tekrarlanır.
- Yedeğe düşüldüğünde HUD'da küçük bir uyarı gösterilir, log satırına kullanılan model yazılır.
- 429 mesajında "limit: 0" geçiyorsa "bu model ücretsiz katmanda yok" uyarısı gösterilir.

## v0.2.1
- `settings.json` değişince ayarlar otomatik yeniden yüklenir (FileSystemWatcher, 600 ms debounce).
  *(Not: Bu madde Cowork notlarında yazıyordu ama depodaki kodda yoktu; gerçekten v0.5.0'da eklendi.)*

## v0.2
- Oyun başına kalıcı hafıza (`dist/memory/<Oyun>.json`): bölüm/görev/bölge takibi, notlar,
  son soru-cevaplar; 12 kayıtta arka planda özetleme.
- `@@MEM` çıktı protokolü ve spoiler sınırının ilerlemeyle kayması.

## v0.1
- İlk sürüm: Mouse 5 bas-konuş, ekran görüntüsü + ses tek istekte Gemini'ye,
  click-through yarı saydam HUD, spoiler kalkanı, Türkçe cevap.

> Not: v0.1–v0.2.1 Claude Cowork'te git olmadan geliştirildi; o sürümlerin ayrı anlık
> görüntüleri yok. Git geçmişi v0.2.2 ile başlıyor.
