# Tally

[English](README.md) · [Türkçe](README.tr.md)

.NET için PostgreSQL üzerinde çalışan kullanım bazlı (usage-based) bir faturalama motoru: ölçülen kullanım olaylarını alır, sabit/kademeli/hacim/paket modelleriyle fiyatlar ve değiştirilemez faturalar keser; **geç gelen kullanım için doğru düzeltmeleri** de içerir.

Projenin odağı, söylemesi kolay tutması zor tek bir özellik:

> Gruplama, sıralama, tekrar teslim, yeniden deneme ve fatura koşusu iç içe geçmesi ne olursa olsun, bir (müşteri, ölçer, ay) için faturalanan her şeyin toplamı, nihai toplam kullanımın fiyatına kuruşu kuruşuna eşittir.

```
POST /events  [{ "customerId":"acme", "eventId":"7f3a", "meter":"api.calls", "quantity":1500, "eventTime":"2026-01-15T10:00:00Z" }]
POST /customers/acme/invoices?asOf=2026-02-01T00:00:00Z
```

## İçerik

| Yetenek | Nasıl |
|---|---|
| İdempotent alım | `(customerId, eventId)` benzersizdir; yeniden teslim edilen olay tanınır ve sıfır kez sayılır |
| Tam bir kez toplulaştırma | Rollup'lar, yalnızca *yeni* olayları ekleyen aynı ifadede güncellenir; tekrarlar için asla |
| Fiyat modelleri | Sabit, Kademeli (kademe başına sabit ücretli), Hacim, Paket; dahil ücretsiz birimler; ayarlanabilir para birimi ondalığı |
| Sürümlü planlar | Planlar değiştirilemez sürümlerdir; müşteri abone olduğu sürümde kalır |
| Geç kullanım | `fiyat(şimdiki toplam) − fiyat(zaten faturalanan toplam)` kadar bir `adjustment` satırı |
| Orantılama | İlk ayın temel ücreti kalan güne göre orantılanır |
| Değiştirilemez faturalar | Veritabanı tetikleyicileri UPDATE/DELETE'i reddeder; müşteri başına boşluksuz numaralandırma |
| Önizleme | Faturalamayla aynı kod yolu, yazmadan |
| Operasyon | REST API, bearer anahtarı (anahtarsız sunucu başlamaz), Docker, CI |

## Tasarım

* **Fiyat, bir olayın değil kümülatif toplamın fonksiyonudur.** Kademeli fiyat, *toplamın* kademelerde nerede durduğuna bağlıdır; bu yüzden bir ayı parçalar halinde faturalamak, her parça kümülatif fiyatların farkı olarak fiyatlanmazsa çift ya da eksik sayar. Her rollup `billed_quantity` ve `billed_amount` hatırlar; bir fatura koşusu `Cost(quantity) − billed_amount` üretir ve yeni çifti saklar. Düzeltmeler aynı mekanizmanın yeniden çalışmasıdır ([ADR 1](docs/adr/0001-cumulative-pricing.md)).
* **Yuvarlama bir kez, kümülatif fiyat üzerinde yapılır;** olay başına asla. Böylece yuvarlama hatası parçalar arasında birikmez.
* **Alım tek bir SQL ifadesidir:** olayları ekle-ya-da-yoksay, yalnızca gerçekten eklenen satırları topla, rollup'ları sabit anahtar sırasıyla upsert et ([ADR 2](docs/adr/0002-ingestion.md)).
* **Fatura koşusu müşteriyi ve faturaladığı rollup'ları kilitler** (`FOR UPDATE`); koşuyla yarışan bir olay ya bu faturadadır ya sonraki düzeltmede, asla kaybolmaz, asla ikisinde birden olmaz ([ADR 3](docs/adr/0003-invoices.md)).

## Kanıtlar

Tek Windows makinesi, aynı makinede Docker'da PostgreSQL 17, yük üreteci aynı süreçte. Mutlak değil, göreli okuyun.

| Çalışma | Sonuç |
|---|---|
| 50 test | CI'da geçer |
| 1.000.000 olay, 2.000 müşteri, 8 yazıcı, 1.000'lik gruplar | ~255.000 olay/sn, grup p50 28 ms, p99 96 ms |
| Bunların 100.000'inin yeniden teslimi | %100 tekrar olarak tanındı, 0,1 sn |
| Her biri 3 ay ve 3 ölçerli (12 satır) 2.000 fatura | ~1.470 fatura/sn |

Yeniden üretmek için: `dotnet run -c Release --project bench/Tally.Bench -- --events 1000000 --customers 2000`.

### Nasıl test edildi

* **Fiyatlama (24):** her model ve sınır, yuvarlama yönü, para birimi ondalığı, orantılama, plan doğrulama; kademeli fiyat, 300 rastgele planda bağımsız bir **birim-birim oracle** ile karşılaştırıldı, 200 planda monotonluk denetlendi.
* **Alım (9):** yeniden teslim, grup içi tekrarlar, bilinmeyen müşteri ve abonelik öncesi olaylar, UTC ay sınırları, geçersiz grubun hiçbir iz bırakmaması, aynı grubun 50 eşzamanlı yeniden denemesi ve 6.000 olaylık (her biri en fazla 3 kez teslim, rastgele grup boyutları) 8 yazıcılı koşu; rollup'lar bellek içi oracle'a ve ham olaylar üzerindeki `SUM()`'a eşit olmalı.
* **Faturalama (12):** kademeli fatura, çift faturalamama, açık ayın dokunulmaması, geç kullanım düzeltmesinin kümülatif fiyat farkına eşitliği, boşluksuz numara, orantılama, önizlemenin hiçbir şey yazmaması, fiyatsız ölçerlerin raporlanması, değiştirilemezliğin veritabanınca zorlanması, plan sürümünün sabitlenmesi, on eşzamanlı koşunun tek fatura üretmesi.
* **Tutarlılık testi:** 3 aya yayılmış 6 müşteri ve 9.000 ayrık olay, rastgele sırada ve grup boyutunda en fazla iki kez teslim edilir, 8 yazıcı **ve sürekli çalışan 4 faturalama döngüsü** eşzamanlıdır. Sonunda her (müşteri, ölçer, ay) için tüm fatura satırlarının toplamı oracle toplamının fiyatına eşit olmalı, miktarlar tutmalı, tam bir `usage` satırı olmalı, temel ücret ayda bir kez faturalanmalı, numaralar boşluksuz olmalı ve faturalanmamış rollup kalmamalı.
* **API (4):** yetki, doğrulama hataları, uçtan uca müşteri akışı.

### Tutarlılık testinin bulduğu hata

Alım rollup satırlarını `(meter, period)` sırasıyla kilitlerken, fatura koşusu aynı müşterinin rollup'larını `(period, meter)` sırasıyla kilitliyordu: eşzamanlı yükte PostgreSQL `deadlock detected` bildirdi. İkisi de artık `(meter, period)` sırasını kullanır (fatura sonradan döneme göre sıralı sunulur). Düzeltmeden sonra test art arda üç kez temiz geçti.

## Çalıştırma

```bash
docker compose up --build
KEY="Authorization: Bearer change-me-0123456789"
curl -X PUT localhost:8080/plans -H "$KEY" -H "Content-Type: application/json" -d '{"id":"pro","baseFee":50,"meters":[{"meter":"api.calls","model":"Graduated","tiers":[{"upTo":1000,"unitPrice":0.01},{"upTo":null,"unitPrice":0.005}]}]}'
curl -X PUT localhost:8080/customers/acme -H "$KEY" -H "Content-Type: application/json" -d '{"planId":"pro","startDate":"2026-01-01"}'
curl -X POST localhost:8080/events -H "$KEY" -H "Content-Type: application/json" -d '[{"customerId":"acme","eventId":"1","meter":"api.calls","quantity":1500,"eventTime":"2026-01-15T10:00:00Z"}]'
curl -X POST "localhost:8080/customers/acme/invoices?asOf=2026-02-01T00:00:00Z" -H "$KEY"
```

Uç noktalar: `PUT /plans`, `PUT /customers/{id}`, `POST /events` (istek başına en fazla 5.000; olay zamanı en fazla 24 saat ilerde), `GET /customers/{id}/usage?period=`, `GET /customers/{id}/invoice-preview?asOf=`, `POST /customers/{id}/invoices?asOf=`, `GET /customers/{id}/invoices`, `GET /health`.

```bash
dotnet test --solution Tally.slnx
```

## Bilinen sınırlar

* **Düzeltme ya da iade yok.** Miktarlar pozitif olmalı, olaylar silinemez, alacak dekontu (credit note) API'si yok. Fazla faturalama bugün sistem dışında düzeltilir. Hacim fiyatlaması geç gelen bir olayın faturayı *düşürmesine* yol açabilir (düzeltme negatif olur); bu doğrudur ama şaşırtıcıdır.
* **Vergi, indirim, kupon, kredi, ön ödemeli taahhüt ve asgari ücret yok.**
* **Yalnızca aylık UTC dönemleri.** Çapalı fatura döngüsü yok, dönem ortası plan değişikliği yok (müşteri tek plan sürümüne sabit), çok para birimli dönüşüm yok.
* **Fiyatlar en fazla 9 ondalıklı `decimal`; miktarlar en fazla 10^12.** Çok küçük birim fiyatla devasa miktarın bileşimi, bunun ötesinde `decimal` taşmasına karşı doğrulanmaz.
* **Kullanım olayları sonsuza dek saklanır** ve olay tablosu bölümlenmemiştir; açık farkla en büyük tablodur.
* **Planda olmayan bir ölçer raporlanır ama kullanımı geriye dönük faturalanmaz;** plan değişemediği için.
* **Tek paylaşılan API anahtarı,** müşteri başına yetkilendirme yok, hız sınırlama yok.
* **Ödeme tahsilatı, borç takibi (dunning), PDF üretimi yok.**

Tasarım kararları: [docs/adr](docs/adr).
