<img width="1577" height="250" alt="image" src="https://github.com/user-attachments/assets/18f91c88-1589-4304-a2ff-a67a46db407a" />
<img width="1500" height="354" alt="image" src="https://github.com/user-attachments/assets/66c27018-480b-4ab0-9102-e7303ca6cc46" />
<img width="1503" height="664" alt="image" src="https://github.com/user-attachments/assets/9df635b2-affa-462b-aec0-3d0f091409f8" />
<img width="1499" height="494" alt="image" src="https://github.com/user-attachments/assets/adf0aebb-b989-43a5-97e5-697fc54647b2" />
<img width="1500" height="265" alt="image" src="https://github.com/user-attachments/assets/9f577ac6-97f3-46bb-ba97-02fce3e8af9e" />








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
