using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EnterpriseAIApp.Services;

namespace EnterpriseAIApp
{
    public partial class LearningReportWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

        private readonly ScanResult _result;
        private readonly string _companyId;
        private readonly KnowledgeGraphManager _graphManager;

        public bool WasDeleted { get; private set; } = false;

        public LearningReportWindow(ScanResult result, string companyId, KnowledgeGraphManager graphManager)
        {
            InitializeComponent();
            _result = result;
            _companyId = companyId;
            _graphManager = graphManager;

            SourceInitialized += LearningReportWindow_SourceInitialized;

            PopulateData();
        }

        private void LearningReportWindow_SourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                int darkMode = 1;
                int res = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                if (res != 0)
                {
                    DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                }
            }
            catch { }
        }

        private void PopulateData()
        {
            TxtReportDocTitle.Text = $"📄 {_result.FileName}";
            TxtReportEngineStats.Text = $"Analiz Motoru: {_result.AnalysisEngine} • Dosya Boyutu: {_result.FileSizeBytes / 1024.0:F1} KB • Paragraf: {_result.ExtractedParagraphs}";

            TxtChipConcepts.Text = $"{_result.Concepts.Count} Kavram & Kural";
            TxtChipLinks.Text = $"{_result.Relations.Count} Sinaps";
            TxtChipQA.Text = $"{_result.GeneratedQuestions.Count} Soru-Cevap";

            // 1. Kavramlar Listesi
            PanelConceptsList.Children.Clear();
            if (_result.Concepts.Count == 0)
            {
                PanelConceptsList.Children.Add(new TextBlock
                {
                    Text = "Bu belgeden belirgin bir kavram türetilemedi.",
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                    Margin = new Thickness(10)
                });
            }
            else
            {
                foreach (var c in _result.Concepts)
                {
                    var card = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12),
                        Margin = new Thickness(0, 0, 0, 8)
                    };

                    var sp = new StackPanel();

                    var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                    var badge = new Border
                    {
                        Background = GetCategoryBrush(c.Category),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(0, 0, 8, 0)
                    };
                    badge.Child = new TextBlock
                    {
                        Text = GetCategoryLabel(c.Category),
                        Foreground = Brushes.White,
                        FontSize = 10,
                        FontWeight = FontWeights.Bold
                    };

                    var title = new TextBlock
                    {
                        Text = c.Name,
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.Bold,
                        FontSize = 13
                    };

                    header.Children.Add(badge);
                    header.Children.Add(title);

                    var desc = new TextBlock
                    {
                        Text = c.RuleDescription,
                        Foreground = new SolidColorBrush(Color.FromRgb(201, 209, 217)),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 11,
                        LineHeight = 16
                    };

                    sp.Children.Add(header);
                    sp.Children.Add(desc);
                    card.Child = sp;
                    PanelConceptsList.Children.Add(card);
                }
            }

            // 2. Anlamsal İlişkiler Listesi
            PanelRelationsList.Children.Clear();
            if (_result.Relations.Count == 0)
            {
                PanelRelationsList.Children.Add(new TextBlock
                {
                    Text = "Kavramlar arasında doğrudan bir fiil bağlantısı tespit edilmedi. Ana belgeye hiyerarşik olarak bağlandı.",
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                    Margin = new Thickness(10)
                });
            }
            else
            {
                foreach (var r in _result.Relations)
                {
                    var card = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 8, 12, 8),
                        Margin = new Thickness(0, 0, 0, 6)
                    };

                    var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                    var src = new TextBlock { Text = r.Source, Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)), FontWeight = FontWeights.SemiBold, FontSize = 12 };
                    var arrow1 = new TextBlock { Text = " ➔ ", Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)), FontSize = 12 };
                    var verbBadge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(40, 245, 158, 11)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(4, 0, 4, 0)
                    };
                    verbBadge.Child = new TextBlock { Text = r.Verb, Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)), FontWeight = FontWeights.Bold, FontSize = 11 };

                    var arrow2 = new TextBlock { Text = " ➔ ", Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)), FontSize = 12 };
                    var tgt = new TextBlock { Text = r.Target, Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153)), FontWeight = FontWeights.SemiBold, FontSize = 12 };

                    sp.Children.Add(src);
                    sp.Children.Add(arrow1);
                    sp.Children.Add(verbBadge);
                    sp.Children.Add(arrow2);
                    sp.Children.Add(tgt);

                    card.Child = sp;
                    PanelRelationsList.Children.Add(card);
                }
            }

            // 3. Model Hafızası Listesi
            PanelQAList.Children.Clear();
            if (_result.GeneratedQuestions.Count == 0)
            {
                PanelQAList.Children.Add(new TextBlock
                {
                    Text = "Model hafıza veri setine yeni soru-cevap eklenmedi.",
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                    Margin = new Thickness(10)
                });
            }
            else
            {
                for (int i = 0; i < _result.GeneratedQuestions.Count; i++)
                {
                    var q = _result.GeneratedQuestions[i];
                    var card = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12),
                        Margin = new Thickness(0, 0, 0, 6)
                    };

                    var sp = new StackPanel();
                    var qTxt = new TextBlock
                    {
                        Text = $"Soru: {q}",
                        Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 4)
                    };

                    var aTxt = new TextBlock
                    {
                        Text = $"Cevap: {(_result.GeneratedAnswers.Count > i ? _result.GeneratedAnswers[i] : (_result.Concepts.Count > i ? _result.Concepts[i].RuleDescription : "Şirket mimari kuralı"))}",
                        Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap
                    };

                    sp.Children.Add(qTxt);
                    sp.Children.Add(aTxt);
                    card.Child = sp;
                    PanelQAList.Children.Add(card);
                }
            }
        }

        private Brush GetCategoryBrush(string category)
        {
            return category switch
            {
                "code" => new SolidColorBrush(Color.FromRgb(0, 113, 227)),
                "process" => new SolidColorBrush(Color.FromRgb(217, 119, 6)),
                "data" => new SolidColorBrush(Color.FromRgb(5, 150, 105)),
                "security" => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                "rule" => new SolidColorBrush(Color.FromRgb(124, 58, 237)),
                _ => new SolidColorBrush(Color.FromRgb(71, 85, 105))
            };
        }

        private string GetCategoryLabel(string category)
        {
            return category switch
            {
                "code" => "KODLAMA",
                "process" => "SÜREÇ",
                "data" => "VERİTABANI",
                "security" => "GÜVENLİK",
                "rule" => "KURAL",
                _ => "DOKÜMAN"
            };
        }

        private void BtnApproveReport_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnDeleteUploadedDoc_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                $"'{_result.FileName}' belgesini ve bu belgeden çıkarılan tüm kuralları/baloncukları silmek istediğinize emin misiniz?",
                "Öğrenmeyi Geri Al",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                if (!string.IsNullOrEmpty(_result.DocumentNodeId))
                {
                    _graphManager.DeleteNode(_companyId, _result.DocumentNodeId);
                }
                WasDeleted = true;
                DialogResult = false;
                Close();
            }
        }
    }
}
