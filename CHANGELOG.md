# Sürüm Geçmişi

Her sürüm GitHub'da `vX.Y.Z` etiketiyle (tag) işaretlenir.

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
