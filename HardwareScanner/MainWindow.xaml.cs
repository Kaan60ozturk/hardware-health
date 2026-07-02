using System;
using System.IO;
using System.Windows;
using HardwareScanner.ViewModels;
using Microsoft.Win32;

namespace HardwareScanner
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                await vm.LoadDataAsync();
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                await vm.LoadDataAsync();
            }
        }

        private void BtnSaveReport_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                SaveFileDialog sfd = new SaveFileDialog
                {
                    Filter = "HTML Raporu (*.html)|*.html|Metin Dosyası (*.txt)|*.txt",
                    Title = "Raporu Kaydet",
                    FileName = $"Donanim_Raporu_{DateTime.Now:yyyyMMdd_HHmm}"
                };

                if (sfd.ShowDialog() == true)
                {
                    try
                    {
                        bool isHtml = Path.GetExtension(sfd.FileName)
                            .Equals(".html", StringComparison.OrdinalIgnoreCase);

                        string report = isHtml ? vm.GenerateReportHtml() : vm.GenerateReportText();
                        // BOM'lu UTF-8: Türkçe karakterler eski editörlerde de doğru görünsün
                        File.WriteAllText(sfd.FileName, report, new System.Text.UTF8Encoding(true));
                        MessageBox.Show("Rapor başarıyla kaydedildi.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Rapor kaydedilemedi: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                try
                {
                    Clipboard.SetText(vm.GenerateReportText());
                    MessageBox.Show("Rapor panoya kopyalandı.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Panoya kopyalanamadı: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
