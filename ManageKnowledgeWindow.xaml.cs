using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EnterpriseAIApp.Models;
using EnterpriseAIApp.Services;

namespace EnterpriseAIApp
{
    public partial class ManageKnowledgeWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

        private readonly Company _company;
        private readonly KnowledgeGraphManager _graphManager;
        private readonly Action _onChanged;

        public ManageKnowledgeWindow(Company company, KnowledgeGraphManager graphManager, Action onChanged)
        {
            InitializeComponent();
            _company = company;
            _graphManager = graphManager;
            _onChanged = onChanged;

            SourceInitialized += ManageKnowledgeWindow_SourceInitialized;

            TxtManageSubtitle.Text = $"Aktif Şirket: {_company.Name} • İstediğiniz belgeyi veya kavramı silebilirsiniz.";
            RefreshList();
        }

        private void ManageKnowledgeWindow_SourceInitialized(object? sender, EventArgs e)
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

        private void RefreshList()
        {
            PanelKnowledgeItems.Children.Clear();
            var graph = _graphManager.GetGraphData(_company);

            if (graph.Nodes.Count <= 1)
            {
                PanelKnowledgeItems.Children.Add(new TextBlock
                {
                    Text = "Henüz taranmış ek bir bilgi veya doküman bulunmuyor.",
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                    Margin = new Thickness(14),
                    FontSize = 13
                });
                return;
            }

            // 1. Dokümanlar (Önce dokümanları listele)
            var docs = graph.Nodes.Where(n => n.Group == "document" || n.Id.StartsWith("doc_")).ToList();
            if (docs.Count > 0)
            {
                var docHeader = new TextBlock
                {
                    Text = "📄 YÜKLENEN VE ÖĞRENİLEN ŞİRKET BELGELERİ",
                    Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Margin = new Thickness(4, 8, 4, 8)
                };
                PanelKnowledgeItems.Children.Add(docHeader);

                foreach (var doc in docs)
                {
                    var childCount = graph.Nodes.Count(n => n.ParentId == doc.Id || (!string.IsNullOrEmpty(doc.DocSource) && n.DocSource == doc.DocSource && n.Id != doc.Id));

                    var itemBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 10, 12, 10),
                        Margin = new Thickness(0, 0, 0, 6)
                    };

                    var dp = new DockPanel();

                    var delBtn = new Button
                    {
                        Content = "🗑️ Belgeyi ve Kurallarını Sil",
                        Background = new SolidColorBrush(Color.FromArgb(40, 248, 81, 73)),
                        Foreground = new SolidColorBrush(Color.FromRgb(248, 81, 73)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(218, 54, 51)),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(10, 5, 10, 5),
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 11,
                        Cursor = System.Windows.Input.Cursors.Hand,
                        Tag = doc.Id
                    };
                    delBtn.Click += (s, e) =>
                    {
                        if (MessageBox.Show($"'{doc.Label}' belgesini ve bu belgeden çıkarılan TÜM {childCount} kuralı silmek istiyor musunuz?", "Belgeyi Sil", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        {
                            _graphManager.DeleteNode(_company.Id, doc.Id);
                            _onChanged?.Invoke();
                            RefreshList();
                        }
                    };
                    DockPanel.SetDock(delBtn, Dock.Right);
                    dp.Children.Add(delBtn);

                    var infoStack = new StackPanel();
                    var title = new TextBlock
                    {
                        Text = doc.Label,
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.Bold,
                        FontSize = 13
                    };
                    var sub = new TextBlock
                    {
                        Text = $"Kaynak: {doc.DocSource} • {childCount} adet alt iş kuralı ve kavram içeriyor",
                        Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                        FontSize = 11,
                        Margin = new Thickness(0, 2, 0, 0)
                    };
                    infoStack.Children.Add(title);
                    infoStack.Children.Add(sub);
                    dp.Children.Add(infoStack);

                    itemBorder.Child = dp;
                    PanelKnowledgeItems.Children.Add(itemBorder);
                }
            }

            // 2. Diğer Kavramlar
            var otherNodes = graph.Nodes.Where(n => n.Id != "core" && n.Group != "document" && !n.Id.StartsWith("doc_")).ToList();
            if (otherNodes.Count > 0)
            {
                var conceptHeader = new TextBlock
                {
                    Text = "💡 DİĞER KAVRAMLAR VE KURALLAR",
                    Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Margin = new Thickness(4, 16, 4, 8)
                };
                PanelKnowledgeItems.Children.Add(conceptHeader);

                foreach (var node in otherNodes)
                {
                    var itemBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 8, 12, 8),
                        Margin = new Thickness(0, 0, 0, 6)
                    };

                    var dp = new DockPanel();

                    var delBtn = new Button
                    {
                        Content = "🗑️ Sil",
                        Background = new SolidColorBrush(Color.FromArgb(30, 248, 81, 73)),
                        Foreground = new SolidColorBrush(Color.FromRgb(248, 81, 73)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(218, 54, 51)),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 4, 8, 4),
                        FontSize = 11,
                        Cursor = System.Windows.Input.Cursors.Hand,
                        Tag = node.Id
                    };
                    delBtn.Click += (s, e) =>
                    {
                        if (MessageBox.Show($"'{node.Label}' kuralını silmek istiyor musunuz?", "Kavramı Sil", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        {
                            _graphManager.DeleteNode(_company.Id, node.Id);
                            _onChanged?.Invoke();
                            RefreshList();
                        }
                    };
                    DockPanel.SetDock(delBtn, Dock.Right);
                    dp.Children.Add(delBtn);

                    var infoStack = new StackPanel();
                    var title = new TextBlock
                    {
                        Text = node.Label,
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 12
                    };
                    var desc = new TextBlock
                    {
                        Text = node.Description,
                        Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                        FontSize = 11,
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 2, 0, 0)
                    };
                    infoStack.Children.Add(title);
                    infoStack.Children.Add(desc);
                    dp.Children.Add(infoStack);

                    itemBorder.Child = dp;
                    PanelKnowledgeItems.Children.Add(itemBorder);
                }
            }
        }

        private void BtnResetAllKnowledge_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                $"'{_company.Name}' şirketine ait tüm taranmış belgeler ve ek kurallar silinecek, başlangıç fabrika ayarlarına dönülecektir.\n\nEmin misiniz?",
                "Tüm Bilgi Ağını Sıfırla",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res == MessageBoxResult.Yes)
            {
                _graphManager.ResetGraphToDefaults(_company.Id, _company);
                _onChanged?.Invoke();
                RefreshList();
                MessageBox.Show("Şirket bilgi ağı varsayılan ayarlara sıfırlandı.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnCloseManage_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
