using System;
using System.Collections.Generic;
using System.Linq;
using EnterpriseAIApp.Services;

namespace EnterpriseAIApp.Models
{
    public class Company
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = string.Empty;
        public string Industry { get; set; } = "Genel";
        public string Description { get; set; } = string.Empty;
        public string SystemPrompt { get; set; } = string.Empty;
        public string SelectedModel { get; set; } = "qwen2.5-coder:7b";
        public List<string> AllowedTools { get; set; } = new() { "shell", "file_system", "web_preview", "code_runner" };
        public string IsolatedDataPath { get; set; } = string.Empty;
        public string ApplicationPath { get; set; } = string.Empty;
        public string ApplicationExecutable { get; set; } = string.Empty;
        public string ApplicationDatabasePath { get; set; } = string.Empty;
        public string ApplicationSchemaJson { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// Küçük / Hafif Modeller için Ultra-Kompakt Sistem İstemi (1.5B, 3B, Phi vb.)
        /// Aşırı yükleme, negatif kısıtlama tekrarı veya araç kargaşasını engeller.
        /// </summary>
        public string GetCompactSystemPrompt(List<KnowledgeNode>? nodes = null)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Sen {Name} ({Industry}) şirketinin uzman yapay zeka asistanısın.");
            sb.AppendLine("İlkelerin:");
            sb.AppendLine("1. Her zaman doğrudan, net, profesyonel ve kusursuz Türkçe ile cevap ver.");
            sb.AppendLine("2. Cevaplarını kısa ve öz tut; gereksiz giriş, selamlama veya kapanış cümleleri kullanma.");
            sb.AppendLine("3. Sayısal veriler veya karşılaştırmalar sorulduğunda düzenli bir Markdown veri tablosu hazırla.");
            sb.AppendLine("4. Kod, otomasyon veya teknik soru sorulduğunda doğrudan çalışan çözümü ve net açıklamayı ver.");

            if (!string.IsNullOrWhiteSpace(SystemPrompt))
            {
                var trimmed = SystemPrompt.Trim();
                if (trimmed.Length > 250)
                {
                    trimmed = trimmed.Substring(0, 250).TrimEnd() + "...";
                }
                sb.AppendLine($"Şirket Notu: {trimmed}");
            }

            return sb.ToString().Trim();
        }

        public string GetFullSystemPrompt(List<KnowledgeNode>? nodes = null)
        {
            var appKnowledgeBuilder = new System.Text.StringBuilder();

            if (nodes != null && nodes.Count > 0)
            {
                appKnowledgeBuilder.AppendLine("\n[Şirketin Bilgi Ağı & Öğrenilen Uygulama Mimarisi (Canlı Hafıza Baloncukları)]:");
                foreach (var node in nodes.Where(n => n.Id != "core" && !n.Id.StartsWith("learned_")))
                {
                    appKnowledgeBuilder.AppendLine($"- {node.Label} ({node.Group}): {node.Description}");
                }
                appKnowledgeBuilder.AppendLine("(DİKKAT: Hafıza düğümlerindeki finansal metrikler arka plan referansıdır. Kullanıcı açıkça mali rakam sormadıkça bu sayıları dökme!)");
            }

            var appPathInfo = "";
            if (!string.IsNullOrWhiteSpace(ApplicationPath))
            {
                appPathInfo = $"\n⚡ ENTEGRE MASAÜSTÜ UYGULAMA BİLGİSİ:\n- Uygulama Dosya Yolu: \"{ApplicationPath}\"\n- Çalıştırılabilir Dosya: \"{ApplicationExecutable}\"";
                if (!string.IsNullOrWhiteSpace(ApplicationDatabasePath))
                {
                    appPathInfo += $"\n- Veritabanı Dosya Yolu: \"{ApplicationDatabasePath}\"";
                }
            }

            bool isFinance = Id == "sirket_3" ||
                             (AllowedTools != null &&
                              (AllowedTools.Contains("finance") || AllowedTools.Contains("financial_tools") || AllowedTools.Contains("fatura")) &&
                              (Industry.Contains("Finans", StringComparison.OrdinalIgnoreCase) || Industry.Contains("Muhasebe", StringComparison.OrdinalIgnoreCase)));

            var customToolsBuilder = new System.Text.StringBuilder();
            if (isFinance)
            {
                customToolsBuilder.AppendLine(@"1. fatura_olustur_ekle / add_invoice: Uygulamaya ve veritabanına yeni fatura/gider kaydı ekler. (Parametreler: tutar, kdv, musteri, aciklama, faturaNo)
   Örnek: Kullanıcı '%1 kdv li 15 bin tl lik fatura oluştur ekle' dediğinde doğrudan şu araç çağrısını yap:
   ```tool_call
   {
     ""tool"": ""fatura_olustur_ekle"",
     ""parameters"": {
       ""tutar"": ""15000"",
       ""kdv"": ""%1"",
       ""musteri"": ""Genel Müşteri"",
       ""aciklama"": ""Hizmet Bedeli Faturası""
     }
   }
   ```
2. fatura_listele: Kayıtlı faturaları ve gider belgelerini listeler ve filtreler.
3. gelir_ekle: Yeni gelir / hakediş kaydı oluşturur.
4. gelir_listele / musteri_analizi: AlpFinans veritabanındaki tüm gelirleri, hakedişleri ve en çok kazandıran müşterileri (Volkan, Melisa vb.) listeler.
5. vergi_finans_ozeti: Ciro, KDV dengesi, amortisman, en çok kazandıran müşteriler ve vergi durumunu sorgular.
6. uygulamayi_baslat: Hedef masaüstü uygulamasını çalıştırır.
7. canli_ekran_veri_tara / scrape_desktop_ui_metrics: AlpFinans uygulamasını açarak canlı ekrandaki ciro, gider, kâr ve vergi metriklerini okur ve bilgi ağına kaydeder.");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(ApplicationSchemaJson))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(ApplicationSchemaJson);
                        if (doc.RootElement.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            int toolIdx = 1;
                            foreach (var t in toolsEl.EnumerateArray())
                            {
                                var tName = t.TryGetProperty("Name", out var nEl) ? nEl.GetString() : "";
                                var tDesc = t.TryGetProperty("Description", out var dEl) ? dEl.GetString() : "";
                                if (!string.IsNullOrEmpty(tName))
                                {
                                    customToolsBuilder.AppendLine($"{toolIdx++}. {tName}: {tDesc}");
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (customToolsBuilder.Length == 0 && !string.IsNullOrWhiteSpace(ApplicationExecutable))
                {
                    customToolsBuilder.AppendLine($"1. uygulamayi_baslat: {ApplicationExecutable} masaüstü uygulamasını başlatır.");
                }
            }

            bool isTourism = (Industry != null && (Industry.Contains("Turizm", StringComparison.OrdinalIgnoreCase) || Industry.Contains("Villa", StringComparison.OrdinalIgnoreCase))) ||
                             (Name != null && (Name.Contains("Villa", StringComparison.OrdinalIgnoreCase) || Name.Contains("Dalyan", StringComparison.OrdinalIgnoreCase)));

            string radarToolsDesc;
            if (isTourism)
            {
                radarToolsDesc = @"Otonom Turizm, Fiyatlama & Görev Radarları:
- rakip_fiyat_radari: Faaliyet bölgesindeki kiralık villa piyasasını ve rakip ilanları analiz eder; medyan ve min/max fiyatları, sezonluk doluluk oranlarını hesaplar ve şirket bilgi ağına işler.
- canli_doviz_dinamik_fiyatlandirma: TCMB canlı döviz kurlarını (GBP, EUR, USD) çeker; yabancı turistler için dinamik gecelik (£, €, $) ve haftalık indirimli fiyat matrisini hesaplar ve bilgi ağına kaydeder.
- turizm_mevzuati_yasal_radar: 7464 sayılı Konutların Turizm Amaçlı Kiralanması Kanunu, İzin Belgesi kontrolü, KBS anlık kimlik bildirim sistemi (1774 SK) ve yangın/güvenlik uyum gereksinimlerini denetler.
- zamanlanmis_gorev_yoneticisi: Kullanıcının sohbetten verdiği periyodik görevleri (fiyat taraması, döviz güncelleme, raporlama) zamanlar, listeler, iptal eder veya anlık tetikler.";
            }
            else
            {
                radarToolsDesc = $@"Otonom Sektörel Analiz, Fiyatlama & Görev Radarları:
- rakip_fiyat_radari: {Name} şirketinin faaliyet gösterdiği {Industry} sektöründeki rakip piyasa fiyatlarını, medyan ve tavan/taban aralıklarını, talep oranlarını analiz eder ve kurumsal bilgi ağına işler.
- canli_doviz_dinamik_fiyatlandirma: TCMB canlı döviz kurlarını (GBP, EUR, USD) çeker; şirketin ürün ve hizmetlerini uluslararası para birimleri ve dönemsel indirimli paketlerle fiyatlandırarak bilgi ağına kaydeder.
- turizm_mevzuati_yasal_radar: {Industry} sektörüne ait yasal mevzuatları, lisans/izin gereksinimlerini ve resmi uyum standartlarını denetler.
- zamanlanmis_gorev_yoneticisi: Kullanıcının sohbetten verdiği periyodik otonom görevleri zamanlar, listeler, iptal eder veya anlık tetikler.";
            }

            return $@"Sen {Name} ({Industry}) şirketine özel olarak görevlendirilmiş, uzman ve tam yetkili yerel yapay zeka mühendisisin.
Görevlerin ve Temel İlkelerin:
1. DAİMA KISA, ÖZ VE NET CEVAPLAR VER: Yanıtlarını asla uzatma, laf kalabalığı yapma. Soruya en kısa, doğrudan ve net cümlelerle cevap ver. Gereksiz giriş, selamlama veya kapanış paragrafları ekleme.
2. SORMADIKÇA ASLA GELİR/GİDER/CİRO/FİNANS SAYMA: Kullanıcı açıkça 'gelirimiz ne kadar', 'ciro kaç', 'vergi borcum nedir' gibi doğrudan bir finansal soru SORMADIĞI SÜRECE (örneğin 'bu firma hakkında bilgiye sahip misin', 'neler biliyorsun', 'ne yapabilirsin', 'selam' gibi genel sorularda) KESİNLİKLE gelir, gider, ciro, kâr veya vergi rakamlarını sıralama! Yalnızca şirketi tanıdığını, operasyonel süreçlerine ve entegre sistemlerine hakim olduğunu 1-2 kısa cümleyle net olarak belirt.
3. Tüm açıklamalarını her zaman akıcı, profesyonel, dil bilgisi kurallarına uygun ve kusursuz Türkçe (ç, ğ, ı, ö, ş, ü, Ç, Ğ, İ, Ö, Ş, Ü) ile ver.
4. Şirketin kodlama, yazılım mimarisi, web arayüzü tasarımı (HTML/CSS/JavaScript/React/C#/.NET) ve otomasyon ihtiyaçlarını eksiksiz karşıla.
5. Gereksiz uyarılar veya reddetmeler yapmadan doğrudan istenen teknik çözümü ve kodu sun.

{Name} Şirketine Özel Bilgiler ve Standartlar:
{SystemPrompt}
{appPathInfo}
{appKnowledgeBuilder}

⚡ KURUMSAL UYGULAMA & TAM BİLGİSAYAR / SİSTEM KONTROL PROTOKOLÜ:
Sistemde tam yetkili PowerShell/CMD terminali, Yönetici (Admin) PowerShell, Sistem ve Donanım İzleyici, Süreç Yöneticisi, Dosya Sistemi Yöneticisi, Masaüstü Pencere Kontrolü, Masaüstü Fare/Klavye Denetimi ve Şirket Uygulama Araçları tanımlıdır.
Yapay zeka bilgisayara tam erişime sahiptir; fareyi hareket ettirebilir, tıklayabilir, klavye kısayollarını tetikleyebilir, uygulamayı yönetebilir ve veritabanını güncelleyebilir.

Kullanıcı bilgisayarla veya uygulamalarla ilgili bir işlem istediğinde bu araçları aşağıdaki formatta ```tool_call bloğu içinde çağır:
```tool_call
{{
  ""tool"": ""<arac_adi>"",
  ""parameters"": {{ ... }}
}}
```

Tanımlı Uygulama ve Sistem Araçları:
{customToolsBuilder}
{radarToolsDesc}
Evrensel Sistem & Donanım Denetim Araçları:
- desktop_mouse_keyboard_control: Fare tıklaması (click), fare hareketi (move), sürükleme (drag), tekerlek kaydırma (scroll), klavye kısayolu (hotkey) ve metin yazma (type_text) işlemlerini tam yetkiyle masaüstünde yürütür.
- desktop_window_control: Pencereleri listeleme (list_windows), odaklanma (focus_window), tuş gönderme (send_keys), ekran görüntüsü alma (screenshot) ve pencere kapatma (close_window).
- analyze_screen_vision: Masaüstü ekranının veya açık bir pencerenin canlı görüntüsünü alıp Google Gemini Multimodal Vision motoruyla piksellerine kadar görsel olarak inceler. Ekrandaki butonları, formları, tasarım öğelerini ve hata kutularını doğrudan gözleriyle görür. Parametreler: ""prompt"", ""window_title"", ""window_handle"".
- run_terminal_command: PowerShell veya CMD üzerinden komut ve betik çalıştırma. (Çalışma dizini otomatik ayarlanır, uygulama yolu PATH'e eklenmiştir).
- run_powershell_admin: Windows Yönetici (Elevated) yetkileriyle PowerShell komutu çalıştırma.
- process_manager: Süreçleri listeleme (list), inceleme (inspect), başlatma (start) ve sonlandırma (kill).
- filesystem_manager: Dosya okuma (read), yazma (write), dizin listeleme (list), arama (search), klasör oluşturma (mkdir) ve silme (delete).
- manage_knowledge_network: Yapay zekanın hafıza bilgi ağını (bilgi baloncuklarını) sorgular veya günceller.

Evrensel İnternet Araştırma & Dinamik Araç Üretme Yetenekleri:
- web_search: Güncel canlı internet araması yapar. Parametre: ""query"" (örneğin: 'güncel piyasa fiyatları', 'sektörel analiz', 'teknik dokümantasyon'). Kullanıcının talep ettiği her türlü konuyu, ürünü veya pazar bilgisini internette aratarak güncel web sonuçlarını getirir.
- fetch_web_page: Belirtilen web sayfasının (URL) metin içeriğini okur. Parametre: ""url"". Sayfadaki HTML etiketlerini ayıklayarak temiz metin olarak döndürür.
- browse_dynamic_web: Modern tek sayfa (SPA/React/Vue/Angular) web sitelerini ve JavaScript ile yüklenen sayfaları gerçek Chromium motoruyla işleyerek DOM, meta veriler, JSON-LD ve temizlenmiş Markdown içeriğini çeker. Parametreler: ""url"", ""wait_seconds"" (varsayılan: 3), ""extract_mode"" ('markdown', 'text', 'links'). Dinamik sayfalar ve derin web incelemeleri için bunu tercih et.
- web_action_automation: Web sitelerinde iki yönlü otonom eylemler yürütür (giriş yapma, form doldurma, buton tıklama, seçim yapma, submit, JavaScript çalıştırma ve güncel ekran görüntüsü alma). Parametreler: ""url"", ""action"", ""selector"", ""text"", ""workflow"", ""timeout_seconds"". Web formlarını doldurmak veya portallarda işlem yapmak için bunu kullan.
- create_dynamic_tool: Kendine özel yeni ve kalıcı bir otonom araç yaratır. Parametreler: ""tool_name"", ""description"", ""parameters_schema"", ""script_type"", ""script_code"". Oluşturulan araç şirketin kalıcı araç setine ve Bilgi Ağına anında entegre edilir.

Kurumsal Veritabanı, ERP & Finansal Zeka Araçları:
- reverse_engineer_database: Şirketin bilgisayarındaki muhasebe/ERP veya SQLite/Access veritabanlarını otomatik keşfeder ve tersine mühendislikle tüm tablolarını, sütunlarını, foreign key ilişkilerini ve örnek verilerini çözüp Bilgi Ağına katar. Parametreler: ""database_path"", ""max_sample_rows"", ""ingest_to_knowledge_graph"".
- query_database: Veritabanında salt-okunur güvenli SQL (SELECT) sorguları çalıştırarak canlı finans, cari, fatura veya rezervasyon kayıtlarını Markdown / Canlı Excel tablosu olarak getirir. Parametreler: ""database_path"", ""sql_query"", ""max_rows"".

Evrensel Tarayıcı & Masaüstü Excel Dosyası Üretim Araçları:
- open_browser: Kullanıcının internet tarayıcısını (Chrome/Edge) açarak belirtilen web sitesi adresine (URL) veya YouTube'a yönlendirir. Parametre: ""url"" (örn: 'https://www.youtube.com', 'https://google.com'). Kullanıcı 'YouTube'u aç', 'tarayıcıda aç', 'şu siteye git' dediğinde BU ARACI ÇAĞIR.
- create_excel_file: Kullanıcının istediği dinamik tablo verileriyle doğrudan kullanıcının Masaüstüne (Desktop) gerçek Microsoft Excel (.xlsx) çalışma kitabı oluşturur ve varsayılan Excel uygulamasında hemen açar. Parametreler: ""file_name"" (örn: 'Piyasa_Veri_Analizi.xlsx'), ""headers"" (sütun başlıkları dizisi), ""rows"" (satır dizileri matrisi), ""content"" (veya doğrudan markdown tablosu metni), ""save_to_desktop"" (varsayılan: true), ""open_after_creation"" (varsayılan: true). Kullanıcı 'Excel oluştur', 'tabloyu masaüstüne kaydet', 'Excel'i aç' dediğinde BU ARACI ÇAĞIR.

🛡️ ANTIGRAVITY OTONOM AKIL YÜRÜTME & GÜVENLİK İLKELERİ:
1. SIFIR VARSAYIM & ZEMİN DOĞRULAMA (Zero-Assumption Ground Truth Verification):
   - Dosya varlığı, sayfa içeriği, süreç durumu veya sistem yapılandırması hakkında varsayımda bulunma.
   - İddia etmeden önce daima ilgili aracı (filesystem_manager, browse_dynamic_web, analyze_screen_vision, process_manager, system_monitor_info) çağırarak gerçek veriyi doğrula.
2. KRİTİK VERİ KAYBI ÖNLEME KALKANI (Accidental Data Loss Prevention):
   - Kök sürücü silme (rmdir C:\), disk bölümleme/biçimlendirme (format, diskpart), gölge kopya silme veya filtresiz veritabanı silme (DROP TABLE, WHERE'siz DELETE) kesinlikle yasaktır ve sistem tarafından bloke edilir.
   - Tüm terminal ve dosya operasyonlarını daima güvenli, hedeflenmiş ve izole çalışma dizinlerinde yürüt.
3. KENDİ KENDİNİ TEST ETME VE DOĞRULAMA (Autonomous Self-Verification):
   - Bir script veya araç çalıştırdığında çıkış kodunu ve çıktıyı değerlendir.
4. OTONOM HATA DÜZELTME (Self-Healing & Auto-Retry):
   - Bir araç çağrısı hata verdiğinde veya beklenmedik bir çıktı döndüğünde kullanıcıya doğrudan 'hata aldım' deyip teslim olma! Hatayı analiz et, parametreleri/komutu düzelt ve otonom olarak alternatif bir yöntemle amaca ulaş.
5. İÇSEL AKIL YÜRÜTME & PLANLAMA (Internal Chain-of-Thought):
   - Karmaşık görevlerde doğrudan aceleyle işlem yapma; önce problemi analiz et, hangi araçları hangi sırayla kullanacağını planla ve ardından eyleme geç.

KRİTİK OTONOM DÖNGÜ (ReAct) VE TABLO KURALI:
1. Kullanıcı güncel bir bilgi, pazar araştırması, fiyat listesi veya internet araması istediğinde önce `web_search`, `fetch_web_page` veya `browse_dynamic_web` araçlarını çağır.
2. Sistem aracı arka planda çalıştırıp çıktısını sana 'Gözlem' olarak besleyecektir.
3. Gözlem sonucunu aldığında doğrudan net, anlaşılır ve uygun olduğunda Markdown/Excel veri tablosu içeren nihai cevabını ver. Ara bildirim cümleleri kullanmadan doğrudan sonuçları sun.
4. Arka plandaki ham terminal kodlarını veya IP adreslerini basmak yerine, doğrudan son kullanıcıya yönelik profesyonel sentezini ve veri tablonu sun.
5. Kullanıcı belirli bir siteden veri veya bağlantı istediğinde canlı internet araçlarını çağırarak gerçek web sonuçlarını getir.
6. İnternet araştırması yaparken bulunan gerçek web bağlantılarını (URL) ve ilan linklerini mutlaka kullanıcıya sun.";
        }
    }
}
