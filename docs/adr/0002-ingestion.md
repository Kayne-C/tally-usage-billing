# ADR 0002 — İdempotent alım ve tam bir kez toplulaştırma

**Durum:** Kabul edildi

## Karar

* Bir olay `(customer_id, event_id)` ile tanımlanır; bu olay tablosunun birincil anahtarıdır. Üreticiler serbestçe yeniden dener; tekilleştirmeyi tablo yapar.
* Grup başına tek SQL ifadesi (diziler `unnest` ile açılır): bilinen müşterilere ve abonelik başlangıcında ya da sonrasındaki zamanlara filtrele, olayları ekle-ya-da-yoksay, **yalnızca gerçekten eklenen satırları** topla ve rollup'ları upsert et. Rollup etkisi böylece her ayrık olay için tam bir kez, olay eklemeyle aynı atomik ifadede gerçekleşir. Çökme, rollup etkisiz bir olay ya da tersini bırakamaz.
* İfade `received / accepted / duplicates / rejected` döndürür; istemci yeniden teslimi reddedilmeden ayırt edebilir.
* Geçersiz alanlar (pozitif olmayan ya da devasa miktar, kötü ölçer adı, UTC olmayan zaman, boş kimlik, 5.000'den büyük grup) tüm grubu veritabanı işine girmeden reddeder; API bunları HTTP 400'e çevirir. Bilinmeyen müşteri ya da abonelik öncesi olaylar `rejected` sayılır, saklanmaz.
* **Kilit sırası tasarımın parçasıdır.** Satırlar ve rollup upsert'leri `(customer, meter, period)` sırasıyla, fatura koşusu rollup'ları `(meter, period)` sırasıyla kilitler. Tutarlılık testi, faturalama `(period, meter)` kullandığında bir deadlock buldu.
* Ay, ekleme anında bir kez UTC olarak türetilir ve saklanır.

## Sonuçlar

* Alım maliyeti, olay başına bir indeks eklemesi ve gruptaki ayrık rollup başına bir upsert ile belirlenir (kıyaslama makinesinde ~255.000 olay/sn).
* Yoğun paralel alımda tek müşteri-ölçer-ay rollup satırı sıcak noktadır; ona dokunan gruplar satır kilidinde sıraya girer.
* Rollup'lar her zaman olaylardan yeniden kurulabilir ve bir test bunların olayların `SUM()`'ına eşit olduğunu doğrular.
