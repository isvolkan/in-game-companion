# Sürüm Geçmişi

Her sürüm GitHub'da `vX.Y.Z` etiketiyle (tag) işaretlenir.

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

## v0.2
- Oyun başına kalıcı hafıza (`dist/memory/<Oyun>.json`): bölüm/görev/bölge takibi, notlar,
  son soru-cevaplar; 12 kayıtta arka planda özetleme.
- `@@MEM` çıktı protokolü ve spoiler sınırının ilerlemeyle kayması.

## v0.1
- İlk sürüm: Mouse 5 bas-konuş, ekran görüntüsü + ses tek istekte Gemini'ye,
  click-through yarı saydam HUD, spoiler kalkanı, Türkçe cevap.

> Not: v0.1–v0.2.1 Claude Cowork'te git olmadan geliştirildi; o sürümlerin ayrı anlık
> görüntüleri yok. Git geçmişi v0.2.2 ile başlıyor.
