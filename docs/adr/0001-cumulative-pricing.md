# ADR 0001 — Kümülatif fiyatların farkını faturala

**Durum:** Kabul edildi

## Bağlam

Kademeli fiyatlamada bir olayın fiyatı, ondan önce ne kadar kullanım geldiğine bağlıdır. Geç olaylar, yeniden denemeler ve akış ortasında faturalama "her olayı / her grubu fiyatla" yaklaşımını bozar: kademeli fiyat gruplara hatasız bölünemez ve her parçayı kuruşa yuvarlamak sapmaya yol açar.

## Karar

* `Cost(toplam)`, bir rollup'ın toplam miktarının deterministik bir fonksiyonudur (kesin ondalık aritmetik, plan para biriminin ondalığına bir kez, sıfırdan uzağa yarım yuvarlama).
* Her rollup nelerin faturalandığını saklar: `billed_quantity`, `billed_amount`. Fatura koşusu `Cost(quantity) − billed_amount` faturalar ve `(quantity, Cost(quantity))` çiftini saklar.
* Bir (müşteri, ölçer, ay) için ilk fatura `usage` satırıdır; sonraki her değişiklik miktar farkıyla bir `adjustment` satırıdır. İkisi de aynı kod yolundan çıkar.
* Dolayısıyla tüm faturalar boyunca `amount` toplamı, gruplama, sıra, koşu sayısı ya da geç olayın ne zaman geldiğinden bağımsız olarak tam `Cost(nihai miktar)` eder. Tutarlılık testi bunu doğrular.
* Süren ay asla faturalanmaz; yalnızca `asOf` anında ya da öncesinde biten aylar vadesi gelmiş sayılır.
* Hacim kademelerinde kullanım artınca fatura düşebilir (daha ucuz kademeye ulaşılır). Düzeltme o zaman negatiftir. Bu motorun değil fiyat modelinin özelliğidir.

## Sonuçlar

* Fiyatlama kodu saf bir fonksiyondur ve bağımsız birim-birim oracle'a karşı test edilir.
* Bir planı geriye dönük değiştirmek farklı bir mekanizma gerektirir (planlar bu yüzden değiştirilemez sürümlerdir).
