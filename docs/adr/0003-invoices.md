# ADR 0003 — Faturalar: değiştirilemezlik, numaralandırma ve eşzamanlılık

**Durum:** Kabul edildi

## Karar

* **Veritabanında değiştirilemez.** `invoices` ve `invoice_lines` üzerindeki `BEFORE UPDATE OR DELETE` tetikleyicileri istisna fırlatır; ne bir hata ne SQL yazan bir operatör geçmişi yeniden yazabilir. Düzeltmeler, düzeltme satırlı yeni faturalardır.
* **Müşteri başına boşluksuz numaralandırma.** Numara `customers.next_invoice_no`'dan satır kilidi altında okunur ve faturayı ekleyen aynı işlemde artırılır. Geri alma numara tüketmez.
* **Fatura koşusu tek işlemdir:** müşteri satırını kilitle, faturalanacak rollup'ları kilitle (`FOR UPDATE`), satırları hesapla, faturayı ve satırlarını ekle, rollup'ların faturalanmış çiftini güncelle, faturalanan temel ücret aylarını kaydet, numarayı artır. Bir müşteri için eşzamanlı koşular müşteri satırında sıralanır; ikincisi faturalanacak bir şey bulmaz.
* **Koşuyla yarışan olaylar** ya koşu rollup'ı kilitlemeden önce rollup güncellemesini commit etmiştir (dahil edilir) ya bekler ve bir sonraki sefer düzeltme olur. Hiçbir şey kaybolmaz, hiçbir şey iki kez sayılmaz.
* **Temel ücretler** (müşteri, dönem) başına `base_fees_billed`'da tutulur; yakalama koşusu kaçan her ayı tam bir kez faturalar, ilk ay kalan güne göre orantılanır.
* **Planlar değiştirilemez sürümlerdir** ve müşteriler `(plan, sürüm)` ifade eder; bir fatura her zaman onu üreten plan ile açıklanabilir.
* **Önizleme** aynı kodu kilitsiz ve yazmasız çalıştırır.
* Müşterinin planında olmayan ölçerler sessizce sıfır fiyatlanmak yerine sonuçta raporlanır ve faturalanmadan bırakılır.

## Sonuçlar

* Bir müşteri için fatura oluşturma serileşir; farklı müşteriler paralel faturalanır (kıyaslama makinesinde ~1.470 fatura/sn).
* Henüz fatura iptal etmenin yolu yok (bilinen sınırlar).
