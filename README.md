# Hardware Health

Windows için WPF tabanlı donanım sağlık ve teşhis aracı.

Uygulama sistem bilgilerini, disk SMART durumunu, pil sağlığını, ağ durumunu ve sıcaklık sensörlerini tek ekranda toplar. Raporlar HTML veya metin olarak kaydedilebilir.

## Özellikler

- Sistem, BIOS, CPU, RAM, anakart ve GPU bilgileri
- Disk kapasitesi, türü, SMART durumu, çalışma saati, okuma/yazma ve sıcaklık bilgileri
- Pil aşınması, kapasite ve şarj döngüsü bilgileri
- Ağ sağlığı: internet erişimi, ping, paket kaybı ve DNS kontrolü
- Ağ bağdaştırıcıları: IP, MAC, link hızı, anlık hız, toplam veri ve Wi-Fi sinyali
- LibreHardwareMonitor ile CPU, GPU, anakart ve depolama sıcaklık sensörleri
- Yeni sistem algılandığında otomatik rapor kaydı

## Otomatik Raporlar

Tarama tamamlandığında uygulama sistem için bir parmak izi oluşturur. Bu sistem daha önce kaydedilmemişse, uygulamanın bulunduğu klasörde `raporlar` klasörü açılır ve raporlar otomatik kaydedilir.

Kaydedilen dosyalar:

- `yyyyMMdd_HHmmss_sistem-adi.html`
- `yyyyMMdd_HHmmss_sistem-adi.txt`
- `systems.json` sistemlerin daha önce raporlanıp raporlanmadığını takip eder.

Aynı sistem tekrar tarandığında otomatik yeni rapor oluşturulmaz. İstersen sol menüdeki `Raporu Kaydet` butonuyla manuel rapor alabilirsin.

## Çalıştırma

Geliştirme ortamında:

```powershell
dotnet build HardwareScanner\HardwareScanner.csproj -c Release
```

Tek dosya Windows çıktısı almak için:

```powershell
dotnet publish HardwareScanner\HardwareScanner.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

SMART disk verileri ve bazı sensörler için uygulama yönetici izni ister.

## Gereksinimler

- Windows 10/11
- .NET SDK 7.0 ile derleme
- Yönetici izni önerilir

## Notlar

- `smartctl.exe` kaynak olarak gömülüdür ve çalışma sırasında geçici güvenli klasöre çıkarılır.
- Rapor ve publish çıktıları git deposuna dahil edilmez.
