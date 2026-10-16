using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using EnterpriseAIApp.Services;

namespace EnterpriseAIApp
{
    public partial class ApiKeySettingsWindow : Window
    {
        private readonly CloudAiClient _cloudAiClient;

        public bool KeyChanged { get; private set; }

        public ApiKeySettingsWindow(CloudAiClient cloudAiClient)
        {
            InitializeComponent();
            _cloudAiClient = cloudAiClient;

            TxtOpenAiKey.Password = _cloudAiClient.Config.OpenAiKey;
            TxtClaudeKey.Password = _cloudAiClient.Config.ClaudeKey;
            TxtGoogleKey.Password = _cloudAiClient.Config.GoogleKey;

            Loaded += ApiKeySettingsWindow_Loaded;
        }

        private async void ApiKeySettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshAllStatusAsync();
        }

        private async Task RefreshAllStatusAsync()
        {
            // OpenAI
            if (!string.IsNullOrWhiteSpace(_cloudAiClient.Config.OpenAiKey))
            {
                TxtOpenAiStatus.Text = "Kontrol ediliyor...";
                bool ok = await _cloudAiClient.TestConnectionAsync("openai", _cloudAiClient.Config.OpenAiKey);
                UpdateStatusUI(DotOpenAiStatus, TxtOpenAiStatus, ok, "Bağlı & Aktif", "Geçersiz Anahtar");
            }
            else
            {
                UpdateStatusUI(DotOpenAiStatus, TxtOpenAiStatus, null, "", "Tanımlı Değil");
            }

            // Claude
            if (!string.IsNullOrWhiteSpace(_cloudAiClient.Config.ClaudeKey))
            {
                TxtClaudeStatus.Text = "Kontrol ediliyor...";
                bool ok = await _cloudAiClient.TestConnectionAsync("claude", _cloudAiClient.Config.ClaudeKey);
                UpdateStatusUI(DotClaudeStatus, TxtClaudeStatus, ok, "Bağlı & Aktif", "Geçersiz Anahtar");
            }
            else
            {
                UpdateStatusUI(DotClaudeStatus, TxtClaudeStatus, null, "", "Tanımlı Değil");
            }

            // Google
            if (!string.IsNullOrWhiteSpace(_cloudAiClient.Config.GoogleKey))
            {
                TxtGoogleStatus.Text = "Kontrol ediliyor...";
                bool ok = await _cloudAiClient.TestConnectionAsync("google", _cloudAiClient.Config.GoogleKey);
                UpdateStatusUI(DotGoogleStatus, TxtGoogleStatus, ok, "Bağlı & Aktif", "Geçersiz Anahtar");
            }
            else
            {
                UpdateStatusUI(DotGoogleStatus, TxtGoogleStatus, null, "", "Tanımlı Değil");
            }
        }

        private void UpdateStatusUI(System.Windows.Shapes.Ellipse dot, System.Windows.Controls.TextBlock txt, bool? isOk, string okMsg, string failMsg)
        {
            if (isOk == true)
            {
                dot.Fill = new SolidColorBrush(Color.FromRgb(48, 209, 88));
                txt.Text = okMsg;
                txt.Foreground = new SolidColorBrush(Color.FromRgb(48, 209, 88));
            }
            else if (isOk == false)
            {
                dot.Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                txt.Text = failMsg;
                txt.Foreground = new SolidColorBrush(Color.FromRgb(255, 69, 58));
            }
            else
            {
                dot.Fill = new SolidColorBrush(Color.FromRgb(134, 134, 139));
                txt.Text = failMsg;
                txt.Foreground = new SolidColorBrush(Color.FromRgb(134, 134, 139));
            }
        }

        private async void BtnTestOpenAi_Click(object sender, RoutedEventArgs e)
        {
            var key = TxtOpenAiKey.Password.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                MessageBox.Show("Lütfen önce bir OpenAI API anahtarı girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnTestOpenAi.IsEnabled = false;
            BtnTestOpenAi.Content = "⏳...";
            TxtOpenAiStatus.Text = "Test ediliyor...";

            bool ok = await _cloudAiClient.TestConnectionAsync("openai", key);
            UpdateStatusUI(DotOpenAiStatus, TxtOpenAiStatus, ok, "✅ Doğrulandı", "❌ Başarısız");
            BtnTestOpenAi.IsEnabled = true;
            BtnTestOpenAi.Content = "Test Et";
        }

        private async void BtnTestClaude_Click(object sender, RoutedEventArgs e)
        {
            var key = TxtClaudeKey.Password.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                MessageBox.Show("Lütfen önce bir Anthropic Claude API anahtarı girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnTestClaude.IsEnabled = false;
            BtnTestClaude.Content = "⏳...";
            TxtClaudeStatus.Text = "Test ediliyor...";

            bool ok = await _cloudAiClient.TestConnectionAsync("claude", key);
            UpdateStatusUI(DotClaudeStatus, TxtClaudeStatus, ok, "✅ Doğrulandı", "❌ Başarısız");
            BtnTestClaude.IsEnabled = true;
            BtnTestClaude.Content = "Test Et";
        }

        private async void BtnTestGoogle_Click(object sender, RoutedEventArgs e)
        {
            var key = TxtGoogleKey.Password.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                MessageBox.Show("Lütfen önce bir Google Cloud AI anahtarı girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnTestGoogle.IsEnabled = false;
            BtnTestGoogle.Content = "⏳...";
            TxtGoogleStatus.Text = "Test ediliyor...";

            bool ok = await _cloudAiClient.TestConnectionAsync("google", key);
            UpdateStatusUI(DotGoogleStatus, TxtGoogleStatus, ok, "✅ Doğrulandı", "❌ Başarısız");
            BtnTestGoogle.IsEnabled = true;
            BtnTestGoogle.Content = "Test Et";
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var openAi = TxtOpenAiKey.Password.Trim();
            var claude = TxtClaudeKey.Password.Trim();
            var google = TxtGoogleKey.Password.Trim();

            _cloudAiClient.SaveConfig(openAi, claude, google);
            KeyChanged = true;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
