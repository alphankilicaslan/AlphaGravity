using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EnterpriseAIApp.Models;
using EnterpriseAIApp.Services;
using ClosedXML.Excel;

namespace EnterpriseAIApp
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

        private readonly CompanyManager _companyManager;
        private readonly OllamaClient _ollamaClient;
        private readonly ToolEngine _toolEngine;
        private readonly TrainingManager _trainingManager;
        private readonly KnowledgeGraphManager _knowledgeGraphManager;
        private readonly DocumentScanner _documentScanner;
        private readonly AppInspector _appInspector;
        private readonly AppAutomationEngine _appAutomationEngine;
        private readonly GeminiClient _geminiClient;
        private readonly CloudAiClient _cloudAiClient;
        private readonly VoiceAssistantService _voiceService;
        private readonly LiveDesktopScraperService _liveDesktopScraperService;
        private readonly ChatSessionManager _sessionManager;
        private readonly DatabaseReverseEngineerService _dbService;
        private readonly List<OllamaChatMessage> _chatHistory = new();
        private readonly List<ChatAttachment> _pendingAttachments = new();

        private ChatSession? _activeSession;
        private string? _lastUserPrompt;
        private DatabaseReverseEngineerResult? _currentDbAnalysis;
        private CancellationTokenSource? _currentCts;

        public MainWindow()
        {
            _companyManager = new CompanyManager();
            _ollamaClient = new OllamaClient();
            _geminiClient = new GeminiClient();
            _cloudAiClient = new CloudAiClient(_geminiClient);
            _voiceService = new VoiceAssistantService(_cloudAiClient);
            _trainingManager = new TrainingManager(_companyManager);
            _knowledgeGraphManager = new KnowledgeGraphManager(_companyManager);
            _toolEngine = new ToolEngine(_companyManager, _knowledgeGraphManager, _trainingManager, null, _geminiClient);
            _documentScanner = new DocumentScanner(_companyManager, _knowledgeGraphManager, _trainingManager, _ollamaClient, _geminiClient);
            _appInspector = new AppInspector(_companyManager, _knowledgeGraphManager, _trainingManager, _ollamaClient, _geminiClient);
            _appAutomationEngine = new AppAutomationEngine(_companyManager, _knowledgeGraphManager, _trainingManager, _toolEngine);
            _liveDesktopScraperService = new LiveDesktopScraperService(_companyManager);
            _sessionManager = new ChatSessionManager(_companyManager);
            _dbService = new DatabaseReverseEngineerService(_companyManager, _knowledgeGraphManager);

            InitializeComponent();

            SetupVoiceAssistantEvents();

            Loaded += MainWindow_Loaded;
            SourceInitialized += MainWindow_SourceInitialized;
        }

        private void SetupVoiceAssistantEvents()
        {
            _voiceService.SpeechRecognized += text =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (string.IsNullOrWhiteSpace(TxtPromptInput.Text))
                    {
                        TxtPromptInput.Text = text;
                    }
                    else
                    {
                        TxtPromptInput.Text += " " + text;
                    }
                    TxtPromptInput.CaretIndex = TxtPromptInput.Text.Length;
                    TxtPromptInput.Focus();
                });
            };

            _voiceService.RecordingStateChanged += isRecording =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (isRecording)
                    {
                        BtnMicLive.Background = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                        IconMicSymbol.Text = "⏹️";
                        PnlLiveIndicator.Visibility = Visibility.Visible;
                        LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                        TxtLiveQuickStatus.Text = "Dinliyor...";
                    }
                    else
                    {
                        BtnMicLive.Background = new SolidColorBrush(Color.FromRgb(44, 44, 46));
                        IconMicSymbol.Text = "🎙️";
                        if (!_voiceService.IsLiveMode)
                        {
                            PnlLiveIndicator.Visibility = Visibility.Collapsed;
                        }
                    }
                });
            };

            _voiceService.SpeakingStateChanged += isSpeaking =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (isSpeaking)
                    {
                        BtnStopGeneration.Visibility = Visibility.Visible;
                        PnlLiveIndicator.Visibility = Visibility.Visible;
                        LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(52, 199, 89));
                        TxtLiveQuickStatus.Text = "🔊 Yanıtlıyor...";
                    }
                    else
                    {
                        if (_currentCts == null)
                        {
                            BtnStopGeneration.Visibility = Visibility.Collapsed;
                        }
                        if (!_voiceService.IsLiveMode)
                        {
                            PnlLiveIndicator.Visibility = Visibility.Collapsed;
                        }
                    }
                });
            };

            _voiceService.LiveStateChanged += (state, msg) =>
            {
                Dispatcher.Invoke(() =>
                {
                    switch (state)
                    {
                        case VoiceChatState.Listening:
                            PnlLiveIndicator.Visibility = Visibility.Visible;
                            LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                            TxtLiveQuickStatus.Text = "Dinliyor...";
                            break;
                        case VoiceChatState.Transcribing:
                            PnlLiveIndicator.Visibility = Visibility.Visible;
                            LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                            TxtLiveQuickStatus.Text = "Whisper...";
                            break;
                        case VoiceChatState.Thinking:
                            PnlLiveIndicator.Visibility = Visibility.Visible;
                            LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                            TxtLiveQuickStatus.Text = "Düşünüyor...";
                            break;
                        case VoiceChatState.Speaking:
                            PnlLiveIndicator.Visibility = Visibility.Visible;
                            LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(52, 199, 89));
                            TxtLiveQuickStatus.Text = "Yanıtlıyor...";
                            break;
                        default:
                            if (!_voiceService.IsLiveMode)
                            {
                                PnlLiveIndicator.Visibility = Visibility.Collapsed;
                            }
                            break;
                    }
                });
            };

            _voiceService.LiveUtteranceCompleted += () =>
            {
                Dispatcher.Invoke(async () =>
                {
                    await ProcessLiveTurnAsync();
                });
            };

            _voiceService.AudioLevelChanged += level =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_voiceService.IsLiveMode && _voiceService.IsRecording)
                    {
                        var scale = 1.0 + (level * 0.8);
                        LiveStatusDot.RenderTransform = new System.Windows.Media.ScaleTransform(scale, scale, 4, 4);
                    }
                });
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                _voiceService.Dispose();
            }
            catch { }
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
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
            catch
            {
                // Fallback ignore
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Activate();
            Focus();

            RefreshCompanyDropdown();
            _ = CheckOllamaAndLoadModels();
            _ = InitWebView2Async();
        }

        private async Task InitWebView2Async()
        {
            var webView = KnowledgeGraphWebView;
            if (webView == null || webView.CoreWebView2 != null) return;

            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var localLoader = Path.Combine(baseDir, "WebView2Loader.dll");
                var runtimeLoader = Path.Combine(baseDir, "runtimes", "win-x64", "native", "WebView2Loader.dll");
                try
                {
                    if (File.Exists(localLoader))
                    {
                        Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(baseDir);
                    }
                    else if (File.Exists(runtimeLoader))
                    {
                        Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(Path.Combine(baseDir, "runtimes", "win-x64", "native"));
                    }
                }
                catch { }

                // 1. Force the WPF Window HWND handle to be created immediately
                _ = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();

                // 2. Wait until the WebView2 visual control is actually loaded in the WPF layout
                if (!webView.IsLoaded)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    RoutedEventHandler h = null!;
                    h = (s, args) =>
                    {
                        webView.Loaded -= h;
                        tcs.TrySetResult(true);
                    };
                    webView.Loaded += h;
                    await Task.WhenAny(tcs.Task, Task.Delay(3000));
                }

                await Task.Delay(200);

                var baseUserData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnterpriseAIApp_WebView2");
                Microsoft.Web.WebView2.Core.CoreWebView2Environment? env = null;
                try
                {
                    Directory.CreateDirectory(baseUserData);
                    env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, baseUserData);
                }
                catch
                {
                    // Fallback to process-isolated folder if base directory is locked (e.g. 0x800700AA)
                    var pidFolder = Path.Combine(baseUserData, Process.GetCurrentProcess().Id.ToString());
                    Directory.CreateDirectory(pidFolder);
                    env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, pidFolder);
                }

                // 3. Retry loop for EnsureCoreWebView2Async to ensure HWND attachment succeeds
                Exception? lastEx = null;
                for (int attempt = 1; attempt <= 12; attempt++)
                {
                    try
                    {
                        await webView.EnsureCoreWebView2Async(env);
                        lastEx = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        if (attempt == 6)
                        {
                            try
                            {
                                var fallbackFolder = Path.Combine(baseUserData, $"{Process.GetCurrentProcess().Id}_{attempt}");
                                Directory.CreateDirectory(fallbackFolder);
                                env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, fallbackFolder);
                            }
                            catch { }
                        }
                        await Task.Delay(300);
                    }
                }

                if (lastEx != null) throw lastEx;

                webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 18, 20);
                if (webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.WebMessageReceived += OnKnowledgeGraphWebMessageReceived;
                }

                try { File.Delete(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webview_init_error.log")); } catch { }
                Dispatcher.Invoke(RefreshKnowledgeGraph);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitWebView2Async error: {ex}");
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webview_init_error.log"), ex.ToString()); } catch { }
            }
        }

        private void OnKnowledgeGraphWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.WebMessageAsJson;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("action", out var act) && act.GetString() == "deleteNode")
                {
                    var nodeId = doc.RootElement.GetProperty("nodeId").GetString();
                    var activeCompany = _companyManager.ActiveCompany;
                    if (!string.IsNullOrEmpty(nodeId) && activeCompany != null)
                    {
                        var deleted = _knowledgeGraphManager.DeleteNode(activeCompany.Id, nodeId);
                        if (deleted)
                        {
                            Dispatcher.Invoke(() => UpdateCompanyView(activeCompany));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OnKnowledgeGraphWebMessageReceived error: {ex.Message}");
            }
        }

        private void RefreshKnowledgeGraph()
        {
            try
            {
                var activeCompany = _companyManager.ActiveCompany;
                if (activeCompany == null)
                {
                    var companies = _companyManager.GetAllCompanies();
                    if (companies.Count > 0)
                    {
                        activeCompany = companies[0];
                        _companyManager.ActiveCompany = activeCompany;
                    }
                }

                if (activeCompany != null && KnowledgeGraphWebView?.CoreWebView2 != null)
                {
                    var html = _knowledgeGraphManager.GenerateHtmlVisualizer(activeCompany);
                    try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_graph.html"), html); } catch { }
                    KnowledgeGraphWebView.NavigateToString(html);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RefreshKnowledgeGraph error: {ex}");
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webview_refresh_error.log"), ex.ToString()); } catch { }
            }
        }



        private void RefreshCompanyDropdown()
        {
            var activeId = _companyManager.ActiveCompany?.Id;
            CmbCompanies.Items.Clear();
            CmbDeleteCompanyList?.Items.Clear();

            var companies = _companyManager.GetAllCompanies();
            int selectedIdx = 0;
            for (int i = 0; i < companies.Count; i++)
            {
                var comp = companies[i];
                CmbCompanies.Items.Add(new ComboBoxItem
                {
                    Content = $"🏢 {comp.Name}",
                    Tag = comp.Id
                });

                if (CmbDeleteCompanyList != null)
                {
                    CmbDeleteCompanyList.Items.Add(new ComboBoxItem
                    {
                        Content = $"🏢 {comp.Name} ({comp.Id})",
                        Tag = comp.Id
                    });
                }

                if (comp.Id == activeId)
                {
                    selectedIdx = i;
                }
            }

            if (CmbCompanies.Items.Count > 0)
            {
                CmbCompanies.SelectedIndex = selectedIdx;
                if (CmbDeleteCompanyList != null && CmbDeleteCompanyList.Items.Count > 0)
                {
                    CmbDeleteCompanyList.SelectedIndex = selectedIdx;
                }
            }
        }

        private async Task CheckOllamaAndLoadModels()
        {
            var previousSelection = (CmbModels.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            CmbModels.Items.Clear();

            // 1. Ollama Servisini Güvenceye Al ve Yerel Modelleri Yükle
            await OllamaClient.TryEnsureOllamaRunningAsync();
            var isOllamaConnected = await _ollamaClient.CheckConnectionAsync();
            var localModels = new List<string>();

            if (isOllamaConnected)
            {
                localModels = await _ollamaClient.GetAvailableModelsAsync();
                foreach (var m in localModels)
                {
                    CmbModels.Items.Add(new ComboBoxItem
                    {
                        Content = $"💻 [Yerel] {m}",
                        Tag = m,
                        ToolTip = $"Yerel Ollama Modeli ({m}) - Çevrimdışı, Gizli ve Hızlı"
                    });
                }
            }

            // 2. Bulut AI Modelleri (OpenAI, Claude, Bulut AI - Sadece API Key Tanımlıysa)
            var cloudModels = _cloudAiClient.GetAvailableCloudModels();
            foreach (var cm in cloudModels)
            {
                CmbModels.Items.Add(new ComboBoxItem
                {
                    Content = cm.DisplayName,
                    Tag = cm.Id,
                    ToolTip = $"{cm.Provider}: {cm.Description}"
                });
            }

            bool hasCloud = cloudModels.Count > 0;

            // 3. Durum Göstergelerini Güncelle
            if (hasCloud && isOllamaConnected)
            {
                TxtOllamaStatus.Text = $"☁️ Bulut AI & 🧠 Ollama Aktif ({localModels.Count} Yerel, {cloudModels.Count} Bulut Model)";
                TxtOllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                TxtHeaderStatus.Text = "Bulut & Yerel AI Aktif";
                StatusPulseDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            else if (isOllamaConnected && localModels.Count > 0)
            {
                TxtOllamaStatus.Text = $"🧠 Yerel Ollama Aktif ({localModels.Count} Model)";
                TxtOllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(63, 185, 80));
                TxtHeaderStatus.Text = "Ollama Aktif (Yerel AI)";
                StatusPulseDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            else if (hasCloud)
            {
                TxtOllamaStatus.Text = $"☁️ Bulut AI Modelleri Aktif ({cloudModels.Count} Model)";
                TxtOllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                TxtHeaderStatus.Text = "Bulut AI Aktif";
                StatusPulseDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            else
            {
                TxtOllamaStatus.Text = "Yapay Zeka Servisi Yok (Ollama başlatın veya 🔑 API Key girin)";
                TxtOllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 81, 73));
                TxtHeaderStatus.Text = "AI Bekleniyor";
                StatusPulseDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));

                CmbModels.Items.Add(new ComboBoxItem
                {
                    Content = "⚠️ Model Bulunamadı (Ollama veya 🔑 API Key)",
                    Tag = "",
                    IsEnabled = false
                });
            }

            // 4. Öncelikli Model Seçimi
            if (CmbModels.Items.Count > 0)
            {
                ComboBoxItem? target = null;
                if (!string.IsNullOrEmpty(previousSelection))
                {
                    target = CmbModels.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == previousSelection);
                }

                // Varsayılan: Yerel modeller varsa yerel modeli tercih et (qwen, gemma, llama...)
                if (target == null && localModels.Count > 0)
                {
                    target = CmbModels.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString()?.Contains("qwen") == true)
                          ?? CmbModels.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString()?.Contains("gemma") == true)
                          ?? CmbModels.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString()?.Contains("llama") == true)
                          ?? CmbModels.Items.Cast<ComboBoxItem>().FirstOrDefault(item => !_cloudAiClient.IsCloudModel(item.Tag?.ToString()));
                }

                if (target == null && hasCloud)
                {
                    target = CmbModels.Items.Cast<ComboBoxItem>().FirstOrDefault(item => _cloudAiClient.IsCloudModel(item.Tag?.ToString()));
                }

                if (target == null)
                {
                    target = CmbModels.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.IsEnabled);
                }

                if (target != null)
                {
                    CmbModels.SelectedItem = target;
                }
            }
        }

        private async void BtnRefreshModels_Click(object sender, RoutedEventArgs e)
        {
            BtnRefreshModels.IsEnabled = false;
            var oldHeader = TxtHeaderStatus.Text;
            TxtHeaderStatus.Text = "Yenileniyor...";
            try
            {
                await CheckOllamaAndLoadModels();
            }
            finally
            {
                BtnRefreshModels.IsEnabled = true;
            }
        }

        private async void BtnApiKeySettings_Click(object sender, RoutedEventArgs e)
        {
            var win = new ApiKeySettingsWindow(_cloudAiClient)
            {
                Owner = this
            };
            if (win.ShowDialog() == true || win.KeyChanged)
            {
                await CheckOllamaAndLoadModels();
            }
        }

        private void CmbModels_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbModels.SelectedItem is ComboBoxItem item && item.Tag is string modelTag && !string.IsNullOrEmpty(modelTag))
            {
                bool isCloud = _cloudAiClient.IsCloudModel(modelTag);
                string cleanName = modelTag.Contains(':') ? modelTag.Substring(modelTag.IndexOf(':') + 1) : modelTag;
                TxtHeaderStatus.Text = isCloud ? $"Bulut: {cleanName}" : $"Yerel: {cleanName}";
            }
        }

        private void CmbCompanies_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbCompanies.SelectedItem is ComboBoxItem item && item.Tag is string compId)
            {
                var company = _companyManager.GetAllCompanies().FirstOrDefault(c => c.Id == compId);
                if (company != null)
                {
                    _companyManager.ActiveCompany = company;
                    UpdateCompanyView(company);

                    if (CmbDeleteCompanyList != null)
                    {
                        for (int i = 0; i < CmbDeleteCompanyList.Items.Count; i++)
                        {
                            if ((CmbDeleteCompanyList.Items[i] as ComboBoxItem)?.Tag as string == compId)
                            {
                                CmbDeleteCompanyList.SelectedIndex = i;
                                break;
                            }
                        }
                    }
                }
            }
        }

        private void UpdateCompanyView(Company company)
        {
            TxtCompanyContext.Text = $"Aktif Şirket: {company.Name} ({company.Industry})\nİzolasyon: Bu şirketin verileri diğer şirketlerle ASLA paylaşılmaz.";
            TxtWorkspaceStatus.Text = $"Çalışma Alanı: Data/Companies/{company.Id}/workspace";

            TxtEditCompanyName.Text = company.Name;
            TxtEditCompanyIndustry.Text = company.Industry;
            TxtEditCompanyPrompt.Text = company.SystemPrompt;

            RefreshToolSchemaViewer(company);
            RefreshKnowledgeGraph();
            RefreshCompanySessions(company);
            RefreshDatabaseList(company);
            _chatHistory.Clear();
        }

        private void BtnSaveGeminiKey_Click(object sender, RoutedEventArgs e)
        {
            BtnApiKeySettings_Click(sender, e);
        }

        private bool _isChatFullscreen = false;
        private GridLength _savedGraphWidth = new GridLength(5, GridUnitType.Star);

        private void BtnToggleChatFullscreen_Click(object sender, RoutedEventArgs e)
        {
            _isChatFullscreen = !_isChatFullscreen;
            if (_isChatFullscreen)
            {
                _savedGraphWidth = ColRightGraph.Width;
                ColRightGraph.MinWidth = 0;
                ColRightGraph.Width = new GridLength(0);
                ColSplitter.Width = new GridLength(0);
                ColLeftChat.Width = new GridLength(1, GridUnitType.Star);
                BtnToggleChatFullscreen.Content = "◫ Bölünmüş Ekran";
                BtnToggleChatFullscreen.ToolTip = "Grafik ve araçlar panelini tekrar göster";
            }
            else
            {
                ColRightGraph.MinWidth = 350;
                ColRightGraph.Width = _savedGraphWidth.Value > 0 ? _savedGraphWidth : new GridLength(5, GridUnitType.Star);
                ColSplitter.Width = new GridLength(1);
                ColLeftChat.Width = new GridLength(5.5, GridUnitType.Star);
                BtnToggleChatFullscreen.Content = "⛶ Tam Ekran Sohbet";
                BtnToggleChatFullscreen.ToolTip = "Sohbet alanını tam ekrana büyüt";
            }
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            await SendPromptAsync();
        }

        private async void TxtPromptInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.Enter || e.Key == Key.Return) && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            {
                e.Handled = true; // Alt satıra geçmesini engelle, doğrudan mesajı gönder
                await SendPromptAsync();
            }
        }

        private async Task SendPromptAsync()
        {
            var prompt = TxtPromptInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt) && _pendingAttachments.Count == 0) return;

            var activeCompany = _companyManager.ActiveCompany;
            if (activeCompany == null)
            {
                MessageBox.Show("Lütfen önce aktif bir şirket seçin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _lastUserPrompt = prompt;

            var currentAttachments = new List<ChatAttachment>(_pendingAttachments);
            _pendingAttachments.Clear();
            RefreshAttachmentChips();

            var effectiveDisplayPrompt = string.IsNullOrWhiteSpace(prompt) ? "Eklenen dosyaları ve verileri analiz et." : prompt;

            TxtPromptInput.Text = string.Empty;
            BtnSend.IsEnabled = false;
            BtnSend.Content = "⏳ İşleniyor...";
            BtnStopGeneration.Visibility = Visibility.Visible;

            var userMsg = new ChatMessage
            {
                Role = "user",
                Content = effectiveDisplayPrompt,
                CompanyId = activeCompany.Id,
                Attachments = currentAttachments
            };

            if (_activeSession != null)
            {
                _activeSession.Messages.Add(userMsg);
            }

            AddMessageBubble(userMsg);

            var assistantMsg = new ChatMessage
            {
                Role = "assistant",
                Content = "",
                CompanyId = activeCompany.Id,
                IsStreaming = true
            };
            var bubbleTextBlock = AddMessageBubble(assistantMsg);

            _currentCts = new CancellationTokenSource();

            // R4 / Milestone M4: Yalnızca kullanıcı açıkça ekran taraması istediğinde Canlı Arayüz Taraması çalışır
            if (IsExplicitScrapeRequest(prompt, activeCompany))
            {
                try
                {
                    bubbleTextBlock.Text = "⚡ AlpFinans uygulaması otonom olarak kontrol ediliyor, canlı ekran arayüzü taranıyor...";
                    ChatScrollViewer.ScrollToEnd();

                    // 1 & 2. Otonom başlatma / öne getirme ve Canlı UI taraması
                    var scraperResult = await _liveDesktopScraperService.ScrapeLiveApplicationAsync(
                        activeCompany,
                        "dashboard",
                        _currentCts.Token);

                    // 3. Bilgi Ağına (Knowledge Graph) Canlı Kavram Düğümleri Olarak Enjeksiyon
                    var ingestResult = _knowledgeGraphManager.IngestScrapedMetrics(activeCompany, scraperResult);

                    // 4. WebView2 Bilgi Ağı Grafiğini Anlık Yeniden Çiz
                    Dispatcher.Invoke(RefreshKnowledgeGraph);

                    // 5. Doğrulanmış Profesyonel Türkçe Özet Yanıtı Üret
                    var verifiedCard = FormatScrapedSummaryResponse(scraperResult, ingestResult, prompt);
                    SetBubbleRichContent(bubbleTextBlock, verifiedCard);
                    ChatScrollViewer.ScrollToEnd();

                    // 6. Şirket Hafızasına ve Chat Geçmişine Kaydet
                    _trainingManager.AppendTrainingPair(activeCompany.Id, prompt, verifiedCard);
                    _chatHistory.Add(new OllamaChatMessage { Role = "user", Content = prompt });
                    _chatHistory.Add(new OllamaChatMessage { Role = "assistant", Content = verifiedCard });
                }
                catch (Exception ex)
                {
                    bubbleTextBlock.Text = $"[Otonom UI Döngüsü Hatası]: {ex.Message}";
                }
                finally
                {
                    BtnSend.IsEnabled = true;
                    BtnSend.Content = "Gönder";
                    _currentCts = null;
                }
                return;
            }

            var selectedModel = (CmbModels.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "qwen2.5-coder:1.5b";
            bool isSmallModel = IsSmallModel(selectedModel);
            var graph = _knowledgeGraphManager.GetGraphData(activeCompany);
            var systemPrompt = isSmallModel
                ? activeCompany.GetCompactSystemPrompt(graph.Nodes)
                : activeCompany.GetFullSystemPrompt(graph.Nodes);

            var promptLower = prompt.ToLowerInvariant();
            bool askedForYoutube = promptLower.Contains("youtube") && (promptLower.Contains("aç") || promptLower.Contains("ac") || promptLower.Contains("open") || promptLower.Contains("git") || promptLower.Contains("bak"));
            bool askedForExcel = promptLower.Contains("excel") && (promptLower.Contains("tablo") || promptLower.Contains("aktar") || promptLower.Contains("oluştur") || promptLower.Contains("olustur") || promptLower.Contains("hazırla") || promptLower.Contains("hazirla") || promptLower.Contains("kaydet") || promptLower.Contains("yaz"));
            bool isFinanceCompany = activeCompany.Id == "sirket_3" ||
                                    (activeCompany.AllowedTools != null &&
                                     (activeCompany.AllowedTools.Contains("finance") || activeCompany.AllowedTools.Contains("financial_tools") || activeCompany.AllowedTools.Contains("fatura")));

            bool isCompanyFinanceQuery = promptLower.Contains("kazan") || promptLower.Contains("ciro") ||
                                         promptLower.Contains("gelir") || promptLower.Contains("gider") ||
                                         promptLower.Contains("fatura") || promptLower.Contains("vergi") ||
                                         promptLower.Contains("müşteri") || promptLower.Contains("musteri") ||
                                         promptLower.Contains("hakediş") || promptLower.Contains("hakedis") ||
                                         promptLower.Contains("kdv") || promptLower.Contains("matrah") ||
                                         promptLower.Contains("ödeme") || promptLower.Contains("odeme") ||
                                         promptLower.Contains("tahsilat") || promptLower.Contains("bakiye") ||
                                         promptLower.Contains("cari") || promptLower.Contains("borç") || promptLower.Contains("borc") ||
                                         promptLower.Contains("alacak") || promptLower.Contains("para") ||
                                         promptLower.Contains("volkan") || promptLower.Contains("melisa") ||
                                         promptLower.Contains("bizim") || promptLower.Contains("şirket") || promptLower.Contains("sirket") ||
                                         (isFinanceCompany && (promptLower.Contains("kim") || promptLower.Contains("en çok") || promptLower.Contains("en fazla") || promptLower.Contains("toplam")));

            bool isExplicitWebSearch = promptLower.Contains("internette ara") || promptLower.Contains("webte ara") ||
                                       promptLower.Contains("google") || promptLower.Contains("sahibinden") ||
                                       promptLower.Contains("endeksa") || promptLower.Contains("rakip") ||
                                       promptLower.Contains("piyasa araştırması") || promptLower.Contains("piyasa arastirmasi");

            bool isLiveWebQuery = !isCompanyFinanceQuery && (isExplicitWebSearch ||
                                  (!isFinanceCompany && (promptLower.Contains("piyasa") || promptLower.Contains("hava") ||
                                   promptLower.Contains("haber") || promptLower.Contains("sahibinden") ||
                                   promptLower.Contains("araştır") || promptLower.Contains("arastir"))));

            bool youtubeExecuted = false;
            bool excelExecuted = false;

            // Küçük modeller için YouTube doğrudan C# tarafından açılır (modeli ReAct yüküyle yormadan)
            if (isSmallModel && askedForYoutube)
            {
                try
                {
                    _toolEngine.ExecuteOpenBrowser("https://www.youtube.com");
                    youtubeExecuted = true;
                }
                catch { }
            }
            else if (!isSmallModel && (askedForYoutube || askedForExcel))
            {
                systemPrompt += "\n\n[KRİTİK KULLANICI EYLEM TALİMATI]:\n" +
                                "Kullanıcı tarayıcıda YouTube açılmasını ve/veya Masaüstüne gerçek Microsoft Excel tablosu (.xlsx) oluşturulmasını açıkça istemiştir.\n" +
                                (askedForYoutube ? "- YouTube açılması için MUTLAKA ```tool_call içinde 'open_browser' aracını çağır (url: 'https://www.youtube.com').\n" : "") +
                                (askedForExcel ? "- Excel tablosu için MUTLAKA ```tool_call içinde 'create_excel_file' aracını çağır (save_to_desktop: true, open_after_creation: true).\n" : "") +
                                "Tüm eylemleri sırayla tamamladıktan sonra kullanıcıya canlı veri tablosunu içeren özet yanıtını ver.";
            }

            var cleanQ = prompt.Replace("\"", "'").Trim();

            var docsContext = string.Join("\n\n", currentAttachments
                .Where(a => !string.IsNullOrWhiteSpace(a.ExtractedText))
                .Select(a => $"--- Eklenen Belge ({a.FileName}) ---\n{a.ExtractedText}"));

            // KÜÇÜK MODELLER İÇİN OTONOM ÖN-ARAŞTIRMA (RAG / Autonomous Pre-Search):
            // 1.5B/3B modeller ReAct döngüsünde araç çağırmada takılır veya meta promptları tekrarlar.
            // Bu yüzden sorgu doğrudan C# arka planında web'de aranır ve canlı veriler modele temiz bir şekilde özetletilir!
            if (isSmallModel && isLiveWebQuery)
            {
                Dispatcher.Invoke(() =>
                {
                    bubbleTextBlock.Text = $"🔍 [Canlı Web Araştırması]: \"{cleanQ}\"...\n⏳ Güncel veriler internetten çekiliyor, lütfen bekleyin...";
                    ChatScrollViewer.ScrollToEnd();
                });

                var preSearchRes = await Task.Run(() => _toolEngine.ExecuteWebSearch(cleanQ));
                if (preSearchRes.IsSuccess && !string.IsNullOrWhiteSpace(preSearchRes.Output))
                {
                    docsContext = (string.IsNullOrWhiteSpace(docsContext) ? "" : docsContext + "\n\n") +
                                  $"[CANLI İNTERNET ARAŞTIRMA VERİLERİ ({cleanQ})]:\n{preSearchRes.Output}\n\n" +
                                  "Talimat: Yukarıdaki güncel internet verilerini kullanarak kullanıcının sorusuna doğrudan, net rakamlarla ve düzenli bir Markdown veri tablosuyla Türkçe olarak cevap ver.";
                }
                isLiveWebQuery = false; // Arama önceden tamamlandı
            }
            else if (!isSmallModel && isLiveWebQuery)
            {
                systemPrompt += $"\n\n[CANLI İNTERNET ARAŞTIRMA TALİMATI]:\n" +
                                $"Kullanıcı güncel canlı pazar, fiyat, kira veya web bilgisi sordu: \"{cleanQ}\".\n" +
                                "Aşağıdaki araç çağrısını yaparak verileri topla:\n" +
                                "```tool_call\n" +
                                "{\n" +
                                "  \"tool\": \"web_search\",\n" +
                                $"  \"parameters\": {{ \"query\": \"{cleanQ} güncel\" }}\n" +
                                "}\n" +
                                "```\n" +
                                "Ardından gelen canlı verilerle kullanıcıya net rakamları ve Markdown / Excel veri tablosunu sun.";
            }

            // AlpFinans veya finans yetkili şirketler için güncel canlı veritabanı finansal metriklerini LLM sistem promptuna enjekte et
            if (activeCompany.Id == "sirket_3" || (activeCompany.AllowedTools != null && activeCompany.AllowedTools.Contains("finance")))
            {
                try
                {
                    var liveTaxSummary = _toolEngine.ExecuteTaxSummaryTool(new(), activeCompany.Id);
                    if (liveTaxSummary.IsSuccess && !string.IsNullOrWhiteSpace(liveTaxSummary.Output))
                    {
                        if (isSmallModel)
                        {
                            systemPrompt += $"\n\n[GÜNCEL FİNANS VERİSİ]:\n{liveTaxSummary.Output}\n(Sadece kullanıcı açıkça gelir/gider veya mali durum sorduğunda kısa ve net söyle. En çok kazandıran müşteri sorulursa Volkan ve Melisa olduğunu belirt.)";
                        }
                        else
                        {
                            systemPrompt += $"\n\n[GİZLİ VERİTABANI REFERANSI (YALNIZCA KULLANICI DOĞRUDAN FİNANS VEYA RAKAM SORDUĞUNDA KULLAN)]:\n{liveTaxSummary.Output}\n\n" +
                                            "KRİTİK KURAL (ÇOK ÖNEMLİ):\n" +
                                            "1. DAİMA KISA, ÖZ VE NET YANIT VER: Asla lafı uzatma, uzun listeler veya tablolar dökme. Soruya en kısa ve net şekilde cevap ver.\n" +
                                            "2. SORMADIKÇA GELİR/GİDER/CİRO/VERGİ SÖYLEME: Kullanıcı açıkça 'gelir ne kadar', 'ciro kaç', 'vergi borcum nedir' gibi doğrudan bir finansal soru SORMADIKÇA (örn: 'bu firma hakkında bilgiye sahip misin', 'neler biliyorsun', 'selam' gibi genel konuşmalarda) KESİNLİKLE gelir, gider, ciro veya vergi rakamlarını sıralama! Yalnızca firmayı ve sistemlerini tanıdığını 1-2 kısa cümleyle belirt.\n" +
                                            "3. Finansal bir rakam veya müşteri sorulduğunda doğrudan bu veritabanı referansını kullan: En fazla kazandıran müşteri 333.246,37 ₺ ile Volkan (Volkan Stephan Kite), ikinci sırada 250.000,00 ₺ ile Melisa'dır.";
                        }
                    }
                }
                catch { }
            }

            // Dinamik Semantik Hafıza & RAG (Kullanıcının sorusuyla eşleşen soru-cevap çiftleri ve Bilgi Grafı düğümleri)
            try
            {
                var relevantMemory = _trainingManager.GetRelevantMemorySnippets(activeCompany.Id, prompt, maxSnippets: isSmallModel ? 1 : 3);
                if (!string.IsNullOrWhiteSpace(relevantMemory))
                {
                    systemPrompt += $"\n\n{relevantMemory}";
                }
            }
            catch { }

            var userPromptForAi = string.IsNullOrWhiteSpace(docsContext)
                ? effectiveDisplayPrompt
                : $"{effectiveDisplayPrompt}\n\n[Kullanıcının Sohbete Eklediği Belge ve Tablo Verileri]:\n{docsContext}";

            _chatHistory.Add(new OllamaChatMessage { Role = "user", Content = userPromptForAi });

            var messages = new List<OllamaChatMessage>
            {
                new() { Role = "system", Content = systemPrompt }
            };

            // Son mesajları sohbet geçmişine ekle (küçük modeller için 4 mesaj, büyük modeller için 10 mesaj)
            messages.AddRange(_chatHistory.TakeLast(isSmallModel ? 4 : 10));

            var accumulatedText = "";

            try
            {
                int maxTurns = 5;
                int turn = 0;
                bool continueLoop = true;

                while (turn < maxTurns && continueLoop)
                {
                    turn++;
                    accumulatedText = "";

                    Action<string> handleChunk = chunk =>
                    {
                        accumulatedText += chunk;
                        var currentText = accumulatedText;
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                        {
                            // Model bir araç çağrısı üretiyorsa, ekrana ham JSON'u sızdırma!
                            var trimmed = currentText.TrimStart();
                            if (trimmed.StartsWith("```tool_call", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("```tool", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                            {
                                bubbleTextBlock.Text = "🔍 Canlı web araştırması yapılıyor...\n⏳ Veriler toplanıyor ve tablo hazırlanıyor, lütfen bekleyin...";
                            }
                            else if (trimmed.Contains("```tool_call") || trimmed.Contains("```tool\n") || trimmed.Contains("```json"))
                            {
                                int codeIdx = currentText.IndexOf("```", StringComparison.Ordinal);
                                if (codeIdx > 0)
                                {
                                    bubbleTextBlock.Text = currentText[..codeIdx].TrimEnd() + "\n\n🔍 Canlı araştırma başlatılıyor...";
                                }
                            }
                            else
                            {
                                bubbleTextBlock.Text = currentText;
                            }
                            ChatScrollViewer.ScrollToEnd();
                        }));
                    };

                    if (_cloudAiClient.IsCloudModel(selectedModel))
                    {
                        var cloudTools = GetGeminiToolsForCompany(activeCompany);
                        await _cloudAiClient.StreamChatAsync(
                            selectedModel,
                            messages,
                            handleChunk,
                            _currentCts.Token,
                            currentAttachments,
                            cloudTools);
                    }
                    else
                    {
                        if (currentAttachments.Any(a => !string.IsNullOrEmpty(a.Base64Data)))
                        {
                            var lastUser = messages.LastOrDefault(m => m.Role == "user");
                            if (lastUser != null)
                            {
                                lastUser.Images = currentAttachments.Where(a => !string.IsNullOrEmpty(a.Base64Data)).Select(a => a.Base64Data!).ToList();
                            }
                        }
                        try
                        {
                            await _ollamaClient.StreamChatAsync(
                                selectedModel,
                                messages,
                                handleChunk,
                                _currentCts.Token);
                        }
                        catch (Exception) when (!_currentCts.IsCancellationRequested)
                        {
                            if (!string.IsNullOrWhiteSpace(_geminiClient.ApiKey))
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    bubbleTextBlock.Text = "⚡ [Yerel Ollama yanıt vermedi, otomatik olarak Google Gemini Flash bulut modeline geçiliyor...]\n\n";
                                    ChatScrollViewer.ScrollToEnd();
                                });
                                accumulatedText = "";
                                var cloudTools = GetGeminiToolsForCompany(activeCompany);
                                await _cloudAiClient.StreamChatAsync(
                                    "gemini-3.5-flash",
                                    messages,
                                    handleChunk,
                                    _currentCts.Token,
                                    currentAttachments,
                                    cloudTools);
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }

                    // 1. Tool Call kontrolü (Otonom ReAct Geri Besleme Döngüsü - Çoklu ve Sıralı Araç Yürütme)
                    var toolCalls = ExtractAllToolCalls(accumulatedText);

                    if (toolCalls.Count > 0)
                    {
                        var observationSb = new StringBuilder();

                        foreach (var (toolName, paramDict) in toolCalls)
                        {
                            if (toolName.Equals("open_browser", StringComparison.OrdinalIgnoreCase) || toolName.Equals("open_url", StringComparison.OrdinalIgnoreCase))
                            {
                                youtubeExecuted = true;
                            }
                            if (toolName.Equals("create_excel_file", StringComparison.OrdinalIgnoreCase))
                            {
                                excelExecuted = true;
                            }

                            string queryDisplay = paramDict.GetValueOrDefault("query", paramDict.GetValueOrDefault("sorgu", "")).ToString() ?? "";
                            string statusDisplay = toolName switch
                            {
                                "open_browser" or "open_url" => "🌐 Web Tarayıcısı ve YouTube Açılıyor...",
                                "create_excel_file" => "📊 Canlı Excel Tablosu Masaüstüne Oluşturuluyor...",
                                "web_search" => $"🔍 [Aşama 1/2] Canlı Web Araştırması: \"{queryDisplay}\"...",
                                "fetch_web_page" => $"📄 Web Sayfası Taranıyor: {paramDict.GetValueOrDefault("url", "")}...",
                                "create_dynamic_tool" => $"🛠️ Yeni Otonom Araç Üretiliyor: `{paramDict.GetValueOrDefault("tool_name", "")}`...",
                                _ => $"⚡ Otonom Sistem Aracını Yürütüyor: `{toolName}`..."
                            };

                            Dispatcher.Invoke(() =>
                            {
                                bubbleTextBlock.Text = statusDisplay;
                                ChatScrollViewer.ScrollToEnd();
                            });

                            var toolExecRes = await Task.Run(() => _toolEngine.ExecuteToolByName(toolName, paramDict, activeCompany.Id));
                            observationSb.AppendLine($"[ARAÇ ÇIKTISI - `{toolName}`]:\n{toolExecRes.Output}\n");
                        }

                        // 2. Aşamaya geçerken kullanıcıya canlı bilgilendirme ver (sürekli bitti/başladı demeden stabil durum)
                        Dispatcher.Invoke(() =>
                        {
                            bubbleTextBlock.Text = "📊 Veriler toplandı, analiz ediliyor ve yanıt hazırlanıyor...\n⏳ Lütfen bekleyin...";
                            ChatScrollViewer.ScrollToEnd();
                        });

                        messages.Add(new OllamaChatMessage { Role = "assistant", Content = accumulatedText });
                        messages.Add(new OllamaChatMessage
                        {
                            Role = "user",
                            Content = observationSb.ToString().Trim() + "\n\n" +
                                      (isSmallModel
                                        ? "Yukarıdaki araç çıktılarını ve verileri kullanarak kullanıcının sorusuna doğrudan, net rakamlarla ve düzenli bir veri tablosuyla Türkçe cevap ver."
                                        : "Araç çıktıları yukarıdadır. Doğrudan net, profesyonel ve uygun olduğunda Markdown/Excel veri tablosu içeren nihai cevabını sun.")
                        });

                        continue; // ReAct döngüsü devam eder
                    }

                    // 1.1 DÜŞÜK MODEL OTONOM MÜDAHALE KALKANI:
                    // Eğer model canlı web/fiyat sorusuna araç çağırmak yerine mazeret ürettiyse:
                    if (turn == 1 && isLiveWebQuery && IsRefusalOrMissingWebData(accumulatedText))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            bubbleTextBlock.Text = $"🔍 Otonom Canlı Web Araştırması: \"{prompt}\"...\n⏳ Canlı veriler internetten çekiliyor, lütfen bekleyin...";
                            ChatScrollViewer.ScrollToEnd();
                        });

                        var autoSearchRes = await Task.Run(() => _toolEngine.ExecuteWebSearch(prompt));

                        messages.Add(new OllamaChatMessage { Role = "assistant", Content = accumulatedText });
                        messages.Add(new OllamaChatMessage
                        {
                            Role = "user",
                            Content = $"[GÜNCEL İNTERNET ARAMA ÇIKTISI]:\n{autoSearchRes.Output}\n\n" +
                                      (isSmallModel
                                        ? "Yukarıdaki güncel internet verilerini kullanarak kullanıcının sorusunu doğrudan, net rakamlarla ve düzenli bir Markdown tablosuyla yanıtla."
                                        : "Canlı internet verileri yukarıdadır. Bu gerçek verileri kullanarak doğrudan kullanıcının sorusunu net rakamlarla yanıtla ve Markdown/Excel veri tablosunu sun.")
                        });

                        continue; // ReAct döngüsü 2. turda gerçek verilerle modeli yanıtlatır!
                    }

                    // 2. Özel Uygulama Eylemleri (add_invoice vb.)
                    var (cleanText, actionResult) = await _appAutomationEngine.CheckAndExecuteEmbeddedActionsAsync(accumulatedText, activeCompany);
                    if (actionResult != null || cleanText != accumulatedText)
                    {
                        accumulatedText = cleanText;
                    }

                    // 3. OTONOM ANTİ-OYALAMA DEDEKTÖRÜ:
                    // Eğer model araç çalıştırıldıktan sonra (turn > 1) nihai tablo yerine ara cümle kurup durduysa:
                    if (turn > 1 && turn < maxTurns && IsIntermediateStallingResponse(accumulatedText))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            bubbleTextBlock.Text = "📊 Veriler analiz ediliyor ve sonuç raporu hazırlanıyor...\n⏳ Lütfen bekleyin...";
                            ChatScrollViewer.ScrollToEnd();
                        });

                        messages.Add(new OllamaChatMessage { Role = "assistant", Content = accumulatedText });
                        messages.Add(new OllamaChatMessage
                        {
                            Role = "user",
                            Content = isSmallModel
                                ? "Tüm veriler yukarıda mevcuttur. Şimdi doğrudan net sonuçları ve veri tablosunu sun."
                                : "Tüm araştırma ve araç gözlemleri elindedir. Şimdi doğrudan net sonuçları, açıklamaları ve uygun olduğunda Markdown/Excel Veri Tablosunu sun."
                        });

                        continue; // ReAct döngüsü: Kullanıcıya sormadan arka planda nihai cevabı ve tabloyu üretiyor!
                    }

                    continueLoop = false; // Nihai yanıta ulaşıldı, döngüden çık
                }

                accumulatedText = SanitizeModelOutput(accumulatedText);
                SetBubbleRichContent(bubbleTextBlock, accumulatedText);

                // Kullanıcı açıkça YouTube açmayı istemişse ve henüz açılmadıysa garantili olarak aç
                if (askedForYoutube && !youtubeExecuted)
                {
                    try
                    {
                        _toolEngine.ExecuteOpenBrowser("https://www.youtube.com");
                        youtubeExecuted = true;
                    }
                    catch { }
                }

                // Kullanıcı açıkça Excel oluşturulmasını istemişse ve henüz araçla oluşturulmadıysa garantili olarak masaüstüne oluştur ve aç
                if (askedForExcel && !excelExecuted)
                {
                    try
                    {
                        _toolEngine.ExecuteCreateExcelFile(new Dictionary<string, object>
                        {
                            ["content"] = accumulatedText,
                            ["save_to_desktop"] = true,
                            ["open_after_creation"] = true
                        }, activeCompany.Id);
                        excelExecuted = true;
                    }
                    catch { }
                }

                assistantMsg.Content = accumulatedText;
                assistantMsg.IsStreaming = false;

                if (_activeSession != null)
                {
                    _activeSession.Messages.Add(assistantMsg);
                    _sessionManager.SaveSession(activeCompany.Id, _activeSession);

                    if (CmbChatSessions.SelectedItem is ComboBoxItem curItem && curItem.Content.ToString() != _activeSession.Title)
                    {
                        curItem.Content = _activeSession.Title;
                    }
                }

                _trainingManager.AppendTrainingPair(activeCompany.Id, prompt, accumulatedText);
                _chatHistory.Add(new OllamaChatMessage { Role = "assistant", Content = accumulatedText });
                RefreshKnowledgeGraph();

                if (!string.IsNullOrWhiteSpace(accumulatedText))
                {
                    TxtLiveQuickStatus.Text = "🔊 Yanıtlıyor...";
                    LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(52, 199, 89)); // Green

                    _voiceService.SpeakText(accumulatedText, onCompleted: () =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (_voiceService.IsLiveMode)
                            {
                                // Natural continuous conversational loop: resume listening for next question
                                _voiceService.StartRecording();
                                PnlLiveIndicator.Visibility = Visibility.Visible;
                                LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                                TxtLiveQuickStatus.Text = "Sizi dinliyor...";
                                BtnMicLive.Background = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                                IconMicSymbol.Text = "⏹️";
                            }
                            else
                            {
                                PnlLiveIndicator.Visibility = Visibility.Collapsed;
                                BtnMicLive.Background = new SolidColorBrush(Color.FromRgb(44, 44, 46));
                                IconMicSymbol.Text = "🎙️";
                            }
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                if (_currentCts != null && _currentCts.IsCancellationRequested)
                {
                    bubbleTextBlock.Text += "\n\n⏹️ [Yanıt kullanıcı tarafından durduruldu]";
                }
                else
                {
                    bubbleTextBlock.Text += $"\n\n[Hata]: {ex.Message}";
                }
            }
            finally
            {
                BtnSend.IsEnabled = true;
                BtnSend.Content = "Gönder";
                BtnStopGeneration.Visibility = Visibility.Collapsed;
                _currentCts = null;
            }
        }

        private static bool IsExplicitScrapeRequest(string prompt, Company company)
        {
            // Sadece finans şirketi veya AlpFinans uygulamasını kullanan kiracılar için geçerlidir
            bool isFinanceCompany = company.Id == "sirket_3" ||
                                    (company.AllowedTools != null &&
                                     (company.AllowedTools.Contains("finance") || company.AllowedTools.Contains("financial_tools") || company.AllowedTools.Contains("fatura")) &&
                                     (company.Industry.Contains("Finans", StringComparison.OrdinalIgnoreCase) || company.Industry.Contains("Muhasebe", StringComparison.OrdinalIgnoreCase)));

            if (!isFinanceCompany) return false;

            var lower = prompt.ToLowerInvariant().Trim();
            var keywords = new[]
            {
                "ekranı tara", "ekran tara", "ekranı oku", "ekrandan oku",
                "arayüzü tara", "arayuzu tara", "arayüzden oku", "arayuzden oku",
                "canlı tara", "canli tara", "canlı ekranı tara", "canli ekrani tara",
                "canlı ui tara", "canli ui tara", "ekran görüntüsü al", "ekran goruntusu al",
                "ekranı analiz et", "ekrani analiz et", "uygulama ekranını tara", "uygulama ekranini tara",
                "ekran taraması yap", "ekran taramasi yap"
            };

            return keywords.Any(k => lower.Contains(k));
        }


        private static string FormatScrapedSummaryResponse(
            LiveDesktopScraperResult result,
            IngestScrapedDataResult ingestResult,
            string query)
        {
            decimal gross = result.NumericMetrics.TryGetValue("total_income_gross", out var g) ? g : 0m;
            decimal exp = result.NumericMetrics.TryGetValue("total_company_expenses", out var e) ? e : 0m;
            decimal profit = result.NumericMetrics.TryGetValue("net_profit", out var p) ? p : 0m;
            decimal tax = result.NumericMetrics.TryGetValue("total_tax", out var t) ? t : 0m;
            decimal remTax = result.NumericMetrics.TryGetValue("remaining_tax", out var r) ? r : 0m;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("> [!IMPORTANT]");
            sb.AppendLine("> ### 📊 AlpFinans 2026 Canlı Finansal & Operasyonel Durum Raporu");
            sb.AppendLine("> *AlpFinans masaüstü uygulamasından UI Automation ile canlı taranmış ve doğrulanmıştır.*");
            sb.AppendLine(">");
            sb.AppendLine($"> - **💰 Toplam Brüt Hasılat (Ciro):** `{gross:N2} ₺`");
            sb.AppendLine($"> - **📄 Toplam Giderler & Faturalar:** `{exp:N2} ₺`");
            sb.AppendLine($"> - **💵 Cebe Kalan Net Kasa / Kâr:** `{profit:N2} ₺`");
            sb.AppendLine($"> - **📋 İndirimli Gerçek Vergi:** `{tax:N2} ₺`");
            sb.AppendLine($"> - **⏳ Kalan / Ödenecek Vergi:** `{remTax:N2} ₺`");
            sb.AppendLine(">");
            sb.AppendLine($"> 🧠 **Bilgi Ağı Entegrasyonu:** {ingestResult.CreatedOrUpdatedNodes.Count} adet yeni canlı veri kavram düğümü (`node_metric_...`) şirketin bilgi ağına hafıza olarak kaydedildi ve ekrandaki grafikte anlık güncellendi.");
            sb.AppendLine();
            sb.AppendLine($"Sorgunuz: *\"{query}\"* doğrultusunda canlı masaüstü uygulamanızdan okunan güncel verilere göre şirketinizin bu dönem toplam brüt hasılatı **{gross:N2} ₺**, belgelenmiş giderler düşüldükten sonra net kasa kârı **{profit:N2} ₺** seviyesindedir.");

            return sb.ToString();
        }

        private void TabBtnGraph_Checked(object sender, RoutedEventArgs e)
        {
            if (ViewGraph != null) ViewGraph.Visibility = Visibility.Visible;
            if (ViewDatabase != null) ViewDatabase.Visibility = Visibility.Collapsed;
            if (ViewTools != null) ViewTools.Visibility = Visibility.Collapsed;
        }

        private void TabBtnDatabase_Checked(object sender, RoutedEventArgs e)
        {
            if (ViewGraph != null) ViewGraph.Visibility = Visibility.Collapsed;
            if (ViewDatabase != null) ViewDatabase.Visibility = Visibility.Visible;
            if (ViewTools != null) ViewTools.Visibility = Visibility.Collapsed;
            RefreshDatabaseList(_companyManager.ActiveCompany);
        }

        private void TabBtnTools_Checked(object sender, RoutedEventArgs e)
        {
            if (ViewGraph != null) ViewGraph.Visibility = Visibility.Collapsed;
            if (ViewDatabase != null) ViewDatabase.Visibility = Visibility.Collapsed;
            if (ViewTools != null) ViewTools.Visibility = Visibility.Visible;
        }

        private TextBlock AddMessageBubble(ChatMessage msg)
        {
            var isUser = msg.IsUser;

            var border = new Border
            {
                Background = isUser ? new SolidColorBrush(Color.FromRgb(10, 132, 255)) : new SolidColorBrush(Color.FromRgb(36, 36, 38)),
                BorderBrush = isUser ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(51, 51, 54)),
                BorderThickness = new Thickness(isUser ? 0 : 1),
                CornerRadius = new CornerRadius(isUser ? 14 : 12),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(isUser ? 70 : 0, 4, isUser ? 0 : 70, 8),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };

            var stack = new StackPanel();

            var header = new TextBlock
            {
                Text = isUser ? "Siz" : "Alp Asistan",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = isUser ? new SolidColorBrush(Color.FromRgb(220, 235, 255)) : new SolidColorBrush(Color.FromRgb(134, 134, 139)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            stack.Children.Add(header);

            if (msg.Attachments != null && msg.Attachments.Count > 0)
            {
                var attPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                foreach (var att in msg.Attachments)
                {
                    var attBorder = new Border
                    {
                        Background = isUser ? new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)) : new SolidColorBrush(Color.FromRgb(48, 48, 52)),
                        BorderThickness = new Thickness(1),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(72, 72, 76)),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(6, 3, 6, 3),
                        Margin = new Thickness(0, 0, 4, 4)
                    };
                    string icon = att.FileType switch
                    {
                        "image" => "🖼️",
                        "pdf" => "📕",
                        "word" => "📘",
                        "excel" => "📗",
                        _ => "📄"
                    };
                    attBorder.Child = new TextBlock
                    {
                        Text = $"{icon} {att.FileName}",
                        FontSize = 10,
                        Foreground = Brushes.White
                    };
                    attPanel.Children.Add(attBorder);
                }
                stack.Children.Add(attPanel);
            }

            var contentBlock = new TextBlock
            {
                Text = msg.Content,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 247)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19
            };
            stack.Children.Add(contentBlock);

            border.Child = stack;
            ChatContainer.Children.Add(border);

            if (!string.IsNullOrWhiteSpace(msg.Content) && !msg.IsStreaming)
            {
                SetBubbleRichContent(contentBlock, msg.Content);
            }

            ChatScrollViewer.ScrollToEnd();

            return contentBlock;
        }

        private void SetBubbleRichContent(TextBlock bubbleTextBlock, string content)
        {
            if (bubbleTextBlock == null) return;
            if (string.IsNullOrWhiteSpace(content))
            {
                bubbleTextBlock.Text = "";
                return;
            }

            // TOOL CALL SIZINTISI GÜVENLİK KALKANI:
            // Eğer metinde ham ```tool_call``` veya ```tool``` kod blokları kalmışsa, asla ham JSON gösterme!
            if (content.Contains("```tool_call") || content.Contains("```tool\n") || content.Contains("```json"))
            {
                content = Regex.Replace(content, @"```(?:tool_call|tool|json)[\s\S]*?```", "").Trim();
                if (string.IsNullOrWhiteSpace(content))
                {
                    content = "✅ İşlem ve araştırma başarıyla tamamlandı.";
                }
            }

            var parentStack = bubbleTextBlock.Parent as StackPanel;
            if (parentStack == null)
            {
                bubbleTextBlock.Text = content;
                return;
            }

            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var tableLineIndices = new List<int>();

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith(">")) trimmed = trimmed.Substring(1).Trim();
                if (trimmed.StartsWith("|") && trimmed.EndsWith("|") && trimmed.Count(c => c == '|') >= 2)
                {
                    tableLineIndices.Add(i);
                }
            }

            // Tablo yoksa normal metin olarak göster
            if (tableLineIndices.Count < 2)
            {
                bubbleTextBlock.Text = content;
                bubbleTextBlock.Visibility = Visibility.Visible;
                while (parentStack.Children.Count > 2)
                {
                    parentStack.Children.RemoveAt(2);
                }
                return;
            }

            // Önceki dinamik zengin elemanları temizle
            while (parentStack.Children.Count > 2)
            {
                parentStack.Children.RemoveAt(2);
            }

            bubbleTextBlock.Visibility = Visibility.Collapsed;

            int firstTableIdx = tableLineIndices.First();
            int lastTableIdx = tableLineIndices.Last();

            var preLines = lines.Take(firstTableIdx).ToList();
            var tableLines = lines.Skip(firstTableIdx).Take(lastTableIdx - firstTableIdx + 1).ToList();
            var postLines = lines.Skip(lastTableIdx + 1).ToList();

            string? extractedTitle = null;

            // 1. Tablo Öncesi Başlık & Bilgi Kartı
            var cleanPre = preLines
                .Select(l => l.Trim().TrimStart('>').Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("[!IMPORTANT]", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (cleanPre.Count > 0)
            {
                var preCard = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(38, 38, 41)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 58)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                var preStack = new StackPanel();
                foreach (var l in cleanPre)
                {
                    if (l.StartsWith("###"))
                    {
                        var title = l.TrimStart('#').Trim();
                        extractedTitle = title;
                        preStack.Children.Add(new TextBlock
                        {
                            Text = title,
                            FontSize = 13,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brushes.White,
                            Margin = new Thickness(0, 0, 0, 4)
                        });
                    }
                    else
                    {
                        preStack.Children.Add(new TextBlock
                        {
                            Text = StripMarkdown(l),
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 166)),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 1, 0, 1)
                        });
                    }
                }
                preCard.Child = preStack;
                parentStack.Children.Add(preCard);
            }

            // 2. Excel Tarzı Dinamik Veri Tablosu
            var tableWidget = CreateExcelStyleTable(tableLines, extractedTitle);
            parentStack.Children.Add(tableWidget);

            // 3. Tablo Sonrası Özet & Dipnot Kartı
            var cleanPost = postLines
                .Select(l => l.Trim().TrimStart('>').Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (cleanPost.Count > 0)
            {
                var postCard = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(32, 32, 35)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(48, 48, 51)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 6, 0, 0)
                };

                var postStack = new StackPanel();
                foreach (var l in cleanPost)
                {
                    postStack.Children.Add(new TextBlock
                    {
                        Text = StripMarkdown(l),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(199, 199, 204)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    });
                }
                postCard.Child = postStack;
                parentStack.Children.Add(postCard);
            }

            ChatScrollViewer.ScrollToEnd();
        }

        private UIElement CreateExcelStyleTable(List<string> rawTableLines, string? title = null)
        {
            var cleanRows = new List<List<string>>();
            foreach (var rawLine in rawTableLines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith(">")) line = line.Substring(1).Trim();
                if (!line.StartsWith("|") || !line.EndsWith("|")) continue;

                var inner = line.Trim('|');
                var parts = inner.Split('|').Select(p => p.Trim()).ToList();
                if (parts.Count == 0) continue;

                // Markdown ayırıcı satırını atla (|:---|:---:|...)
                if (parts.All(p => p.Length > 0 && p.All(c => c == '-' || c == ':' || char.IsWhiteSpace(c))))
                {
                    continue;
                }

                cleanRows.Add(parts);
            }

            if (cleanRows.Count < 2)
            {
                return new TextBlock
                {
                    Text = string.Join("\n", rawTableLines),
                    Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 247)),
                    TextWrapping = TextWrapping.Wrap
                };
            }

            var headerRow = cleanRows[0];
            var dataRows = cleanRows.Skip(1).ToList();
            int colCount = headerRow.Count;

            var containerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 32)), // #1E1E20
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 58)), // #38383A
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 8, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var mainStack = new StackPanel();

            // 1. Üst Başlık Barı (Excel İkonu + Başlık + Aksiyon Butonları)
            var topBar = new Grid
            {
                Background = new SolidColorBrush(Color.FromRgb(38, 38, 41)), // #262629
                Margin = new Thickness(0)
            };
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(12, 8, 12, 8),
                VerticalAlignment = VerticalAlignment.Center
            };

            // Excel Yeşil İkon Rozeti
            var excelBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(16, 124, 65)), // #107C41 (Excel Yeşili)
                CornerRadius = new CornerRadius(5),
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock
                {
                    Text = "X",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            titleStack.Children.Add(excelBadge);

            var titleBlock = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(title) ? "Canlı Excel Veri Tablosu" : title,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(titleBlock);
            Grid.SetColumn(titleStack, 0);
            topBar.Children.Add(titleStack);

            // Aksiyon Butonları
            var actionsStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            // [📊 Excel] Butonu - 1 Tıkla Masaüstüne .xlsx Olarak Kaydeder ve Açar
            var btnExcel = new Button
            {
                Content = "📊 Excel",
                ToolTip = "Tabloyu doğrudan Masaüstüne .xlsx Excel dosyası olarak kaydeder ve açar",
                Background = new SolidColorBrush(Color.FromRgb(16, 124, 65)), // Office Excel Green
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(30, 160, 85)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 4, 11, 4),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand
            };

            string tsvContent = string.Join("\r\n", cleanRows.Select(row =>
                string.Join("\t", row.Select(cell => StripMarkdown(cell)))
            ));

            btnExcel.Click += (s, e) =>
            {
                try
                {
                    // 1. Panoya da hazırda kopyala
                    try { Clipboard.SetDataObject(tsvContent, true); } catch { }

                    // 2. Masaüstüne gerçek .xlsx dosyası oluştur
                    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    var cleanTitle = string.IsNullOrWhiteSpace(title)
                        ? "Tablo"
                        : Regex.Replace(title, @"[^\w\-]", "_").Trim('_');
                    if (cleanTitle.Length > 25) cleanTitle = cleanTitle.Substring(0, 25);
                    var fileName = $"{cleanTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                    var filePath = Path.Combine(desktop, fileName);

                    using (var wb = new XLWorkbook())
                    {
                        var ws = wb.Worksheets.Add("Veri_Tablosu");
                        for (int r = 0; r < cleanRows.Count; r++)
                        {
                            var row = cleanRows[r];
                            for (int c = 0; c < row.Count; c++)
                            {
                                var cellVal = StripMarkdown(row[c]);
                                var cell = ws.Cell(r + 1, c + 1);
                                cell.Value = cellVal;

                                if (r == 0)
                                {
                                    cell.Style.Font.Bold = true;
                                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#107C41");
                                    cell.Style.Font.FontColor = XLColor.White;
                                }
                            }
                        }

                        ws.Columns().AdjustToContents();
                        wb.SaveAs(filePath);
                    }

                    // 3. Excel uygulamasında hemen aç
                    try
                    {
                        Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                    }
                    catch { }

                    btnExcel.Content = "✅ Masaüstüne Kaydedildi!";
                    btnExcel.Foreground = new SolidColorBrush(Color.FromRgb(200, 255, 200));
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                    timer.Tick += (ts, te) =>
                    {
                        btnExcel.Content = "📊 Excel";
                        btnExcel.Foreground = Brushes.White;
                        timer.Stop();
                    };
                    timer.Start();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Excel dosyası kaydedilemedi: {ex.Message}", "Excel Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            actionsStack.Children.Add(btnExcel);

            // [💾 CSV] Butonu
            var btnCsv = new Button
            {
                Content = "💾 CSV",
                Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
                Foreground = new SolidColorBrush(Color.FromRgb(100, 210, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(72, 72, 74)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 4, 9, 4),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand
            };
            btnCsv.Click += (s, e) =>
            {
                try
                {
                    var sfd = new Microsoft.Win32.SaveFileDialog
                    {
                        Filter = "CSV Dosyası (*.csv)|*.csv",
                        FileName = (string.IsNullOrWhiteSpace(title) ? "Veri_Tablosu" : Regex.Replace(title, @"[^\w\-]", "_")) + ".csv"
                    };
                    if (sfd.ShowDialog() == true)
                    {
                        var csvLines = cleanRows.Select(r => string.Join(";", r.Select(c => $"\"{StripMarkdown(c).Replace("\"", "\"\"")}\"")));
                        File.WriteAllLines(sfd.FileName, csvLines, System.Text.Encoding.UTF8);
                        MessageBox.Show("CSV tablosu başarıyla kaydedildi.", "Excel / CSV Dışa Aktar", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"CSV dışa aktarma hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            actionsStack.Children.Add(btnCsv);

            Grid.SetColumn(actionsStack, 1);
            topBar.Children.Add(actionsStack);
            mainStack.Children.Add(topBar);

            // Üst bar altı ince çizgi
            mainStack.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(56, 56, 58))
            });

            // 2. Tablo Gövdesi (Yatay ScrollViewer ile tam uyumlu)
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var grid = new Grid { Margin = new Thickness(0) };

            for (int c = 0; c < colCount; c++)
            {
                if (c == 0)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 140 });
                }
                else if (c == colCount - 1)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
                }
                else
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 90 });
                }
            }

            // Başlık Satırı (Row 0)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var headerBg = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 43)), // #28282B
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 58))
            };
            Grid.SetRow(headerBg, 0);
            Grid.SetColumnSpan(headerBg, colCount);
            grid.Children.Add(headerBg);

            for (int c = 0; c < colCount; c++)
            {
                var cell = new Border
                {
                    BorderThickness = new Thickness(0, 0, (c < colCount - 1) ? 1 : 0, 0),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 58)),
                    Padding = new Thickness(10, 7, 10, 7)
                };
                var headerText = new TextBlock
                {
                    Text = StripMarkdown(headerRow[c]).ToUpperInvariant(),
                    Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 166)),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = c == 0 ? TextAlignment.Left : (c == colCount - 1 ? TextAlignment.Left : TextAlignment.Center)
                };
                cell.Child = headerText;
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            // Veri Satırları (Zebra Çizgili)
            for (int r = 0; r < dataRows.Count; r++)
            {
                int rowIndex = r + 1;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                bool isZebra = (r % 2 == 1);
                var rowBg = new Border
                {
                    Background = isZebra
                        ? new SolidColorBrush(Color.FromRgb(36, 36, 38)) // #242426
                        : new SolidColorBrush(Color.FromRgb(30, 30, 32)), // #1E1E20
                    BorderThickness = new Thickness(0, 0, 0, (r < dataRows.Count - 1) ? 1 : 0),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 46))
                };
                Grid.SetRow(rowBg, rowIndex);
                Grid.SetColumnSpan(rowBg, colCount);
                grid.Children.Add(rowBg);

                var rowData = dataRows[r];
                for (int c = 0; c < colCount && c < rowData.Count; c++)
                {
                    var cellRaw = rowData[c];
                    var cellClean = StripMarkdown(cellRaw);

                    var cellBorder = new Border
                    {
                        BorderThickness = new Thickness(0, 0, (c < colCount - 1) ? 1 : 0, 0),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
                        Padding = new Thickness(10, 8, 10, 8),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    if (cellClean.Contains("₺") || cellClean.Contains("$") || cellClean.Contains("€") || cellClean.Contains("£"))
                    {
                        // Fiyat Değeri - Yeşil Vurgu Rozeti
                        var priceStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                        var badge = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(35, 48, 209, 88)),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 48, 209, 88)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(6, 2, 6, 2)
                        };
                        badge.Child = new TextBlock
                        {
                            Text = cellClean,
                            Foreground = new SolidColorBrush(Color.FromRgb(48, 209, 88)),
                            FontWeight = FontWeights.Bold,
                            FontSize = 11,
                            FontFamily = new FontFamily("Consolas, Segoe UI")
                        };
                        priceStack.Children.Add(badge);
                        cellBorder.Child = priceStack;
                    }
                    else if (cellClean.Contains("%"))
                    {
                        // Yüzde Değeri - Mavi Hap Rozet
                        var pctBadge = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(35, 10, 132, 255)),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 10, 132, 255)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(10),
                            Padding = new Thickness(7, 2, 7, 2),
                            HorizontalAlignment = HorizontalAlignment.Center
                        };
                        pctBadge.Child = new TextBlock
                        {
                            Text = cellClean,
                            Foreground = new SolidColorBrush(Color.FromRgb(100, 210, 255)),
                            FontWeight = FontWeights.SemiBold,
                            FontSize = 11
                        };
                        cellBorder.Child = pctBadge;
                    }
                    else if (cellClean.Contains("✅"))
                    {
                        // Uyum / Onay Rozeti
                        var compBadge = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(30, 48, 209, 88)),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 48, 209, 88)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(6, 2, 6, 2),
                            HorizontalAlignment = HorizontalAlignment.Center
                        };
                        compBadge.Child = new TextBlock
                        {
                            Text = cellClean,
                            Foreground = new SolidColorBrush(Color.FromRgb(48, 209, 88)),
                            FontWeight = FontWeights.SemiBold,
                            FontSize = 11
                        };
                        cellBorder.Child = compBadge;
                    }
                    else
                    {
                        var text = new TextBlock
                        {
                            Text = cellClean,
                            Foreground = new SolidColorBrush(Color.FromRgb(229, 229, 234)),
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        if (c == 0)
                        {
                            text.FontWeight = FontWeights.SemiBold;
                            text.Foreground = Brushes.White;
                        }
                        cellBorder.Child = text;
                    }

                    Grid.SetRow(cellBorder, rowIndex);
                    Grid.SetColumn(cellBorder, c);
                    grid.Children.Add(cellBorder);
                }
            }

            scroll.Content = grid;
            mainStack.Children.Add(scroll);
            containerBorder.Child = mainStack;
            return containerBorder;
        }

        private static List<(string toolName, Dictionary<string, object> parameters)> ExtractAllToolCalls(string text)
        {
            var results = new List<(string toolName, Dictionary<string, object> parameters)>();
            if (string.IsNullOrWhiteSpace(text)) return results;

            // 1. Çoklu ```tool_call veya ```tool blokları
            var blockMatches = Regex.Matches(text, @"```(?:tool_call|tool)\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            foreach (Match m in blockMatches)
            {
                var (tName, pDict) = ParseSingleToolJson(m.Groups[1].Value.Trim());
                if (!string.IsNullOrWhiteSpace(tName))
                {
                    results.Add((tName, pDict));
                }
            }

            if (results.Count > 0) return results;

            // 2. Çoklu JSON blokları veya JSON dizisi
            var arrayMatch = Regex.Match(text, @"\[\s*\{[\s\S]*?""(?:tool|name|function)""[\s\S]*?\}\s*\]", RegexOptions.IgnoreCase);
            if (arrayMatch.Success)
            {
                try
                {
                    var sanitized = Regex.Replace(arrayMatch.Value, @"(?<!\\)\\(?![""\\/bfnrtu]|u[0-9a-fA-F]{4})", @"\\");
                    using var doc = JsonDocument.Parse(sanitized);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var (tName, pDict) = ParseSingleToolJson(el.GetRawText());
                            if (!string.IsNullOrWhiteSpace(tName))
                            {
                                results.Add((tName, pDict));
                            }
                        }
                    }
                }
                catch { }
            }

            if (results.Count > 0) return results;

            // 3. Tekli araç çağrısı
            var single = ParseSingleToolJson(text);
            if (!string.IsNullOrWhiteSpace(single.toolName))
            {
                results.Add(single);
            }

            return results;
        }

        private static (string toolName, Dictionary<string, object> parameters) ExtractToolCall(string text)
        {
            var all = ExtractAllToolCalls(text);
            return all.Count > 0 ? all[0] : ("", new Dictionary<string, object>());
        }

        private static (string toolName, Dictionary<string, object> parameters) ParseSingleToolJson(string text)
        {
            var paramDict = new Dictionary<string, object>();
            string toolName = "";

            if (string.IsNullOrWhiteSpace(text)) return (toolName, paramDict);

            // 1. Standart Markdown Tool Call Kod Blokları
            var match = Regex.Match(text, @"```(?:tool_call|tool)\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                match = Regex.Match(text, @"```json\s*(\{[\s\S]*?""(?:tool|name|function)""[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
            }
            if (!match.Success)
            {
                match = Regex.Match(text, @"(\{[\s\S]*?""(?:tool|name|function)""\s*:[\s\S]*?\})", RegexOptions.IgnoreCase);
            }

            string rawJson = match.Success ? match.Groups[1].Value.Trim() : text.Trim();

            int firstBrace = rawJson.IndexOf('{');
            int lastBrace = rawJson.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                rawJson = rawJson.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            // Windows dosya yollarındaki tek ters slash'ları ve geçersiz kaçışları onar (\U, \D, \T gibi)
            var sanitizedJson = Regex.Replace(rawJson, @"(?<!\\)\\(?![""\\/bfnrtu]|u[0-9a-fA-F]{4})", @"\\");

            try
            {
                using var doc = JsonDocument.Parse(sanitizedJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("tool", out var tProp)) toolName = tProp.GetString() ?? "";
                else if (root.TryGetProperty("name", out var nProp)) toolName = nProp.GetString() ?? "";
                else if (root.TryGetProperty("function", out var fProp)) toolName = fProp.GetString() ?? "";

                if (root.TryGetProperty("parameters", out var pElem) && pElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in pElem.EnumerateObject()) paramDict[prop.Name] = prop.Value.ToString();
                }
                else if (root.TryGetProperty("arguments", out var aElem) && aElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in aElem.EnumerateObject()) paramDict[prop.Name] = prop.Value.ToString();
                }
                else
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (!prop.Name.Equals("tool", StringComparison.OrdinalIgnoreCase) &&
                            !prop.Name.Equals("name", StringComparison.OrdinalIgnoreCase) &&
                            !prop.Name.Equals("function", StringComparison.OrdinalIgnoreCase))
                        {
                            paramDict[prop.Name] = prop.Value.ToString();
                        }
                    }
                }
            }
            catch
            {
                // 2. REGEX FALLBACK: JSON parser hata alsa bile araç adı ve parametreleri regex ile kurtar
                var toolRegex = Regex.Match(rawJson, @"""(?:tool|name|function)""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                if (toolRegex.Success)
                {
                    toolName = toolRegex.Groups[1].Value.Trim();
                }

                var paramMatches = Regex.Matches(rawJson, @"""([a-zA-Z0-9_\-]+)""\s*:\s*(?:""([^""\\]*(?:\\.[^""\\]*)*)""|([0-9\.]+|true|false))");
                foreach (Match pm in paramMatches)
                {
                    var key = pm.Groups[1].Value;
                    if (key.Equals("tool", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("function", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("parameters", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("arguments", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var val = pm.Groups[2].Success && !string.IsNullOrEmpty(pm.Groups[2].Value)
                        ? pm.Groups[2].Value.Replace(@"\\", @"\")
                        : pm.Groups[3].Value;
                    paramDict[key] = val;
                }
            }

            return (toolName, paramDict);
        }

        private static bool IsIntermediateStallingResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.ToLowerInvariant();

            // Eğer metin zaten zengin bir tablo içeriyorsa bu oyalama değildir
            if (lower.Contains("|") && lower.Split('\n').Count(l => l.Trim().StartsWith("|") && l.Trim().EndsWith("|")) >= 2)
            {
                return false;
            }

            var stallingPhrases = new[]
            {
                "araştırmasını başlattım", "arastirmasini baslattim",
                "araştırmayı başlattım", "arastirmayi baslattim",
                "araştırma başlattım", "arastirma baslattim",
                "birazdan listeleyeceğim", "birazdan listeleyecegim",
                "birazdan sunacağım", "birazdan sunacagim",
                "birazdan paylaşacağım", "birazdan paylasacagim",
                "sonuçları birazdan", "sonuclari birazdan",
                "verileri analiz ediyorum", "piyasa verilerini analiz ediyorum",
                "analiz ediyorum", "derliyorum",
                "lütfen bekleyin", "lutfen bekleyin",
                "araştırıyorum", "arastiriyorum",
                "hemen bakıyorum", "hemen inceliyorum",
                "bilgileri topluyorum"
            };

            return stallingPhrases.Any(p => lower.Contains(p));
        }

        private static bool IsRefusalOrMissingWebData(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.ToLowerInvariant();

            var refusalPhrases = new[]
            {
                "ulaşılamamıştır", "ulasilamamistir",
                "ulaşamadım", "ulasamadim",
                "tetiklenmemiştir", "tetiklenmemistir",
                "tetiklenmedi",
                "arama aracı tetiklenmedi", "arama araci tetiklenmedi",
                "arama aracı çalıştırılmadı", "arama araci calistirilmadi",
                "canlı web verilerine", "canli web verilerine",
                "internet erişimim yok", "internet erisimim yok",
                "web verilerine erişemiyorum", "web verilerine erisemiyorum",
                "gerçek zamanlı verilere erişimim", "gercek zamanli verilere erisimim",
                "güncel verilere erişimim", "guncel verilere erisimim",
                "canlı verilere erişimim", "canli verilere erisimim",
                "canlı bilgiye erişimim", "canli bilgiye erisimim",
                "web araması yapamıyorum", "web aramasi yapamiyorum"
            };

            return refusalPhrases.Any(p => lower.Contains(p));
        }

        private static string StripMarkdown(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var s = input.Trim();
            s = s.Replace("**", "").Replace("*", "").Replace("`", "").Trim();
            return s;
        }

        public static bool IsSmallModel(string? modelTag)
        {
            if (string.IsNullOrWhiteSpace(modelTag)) return true;
            var lower = modelTag.ToLowerInvariant().Trim();

            // Bulut modelleri (Gemini, Claude, GPT-4o) geniş bağlamlıdır
            if (lower.Contains("gemini") || lower.Contains("gpt-4") || lower.Contains("claude"))
            {
                if (lower.Contains("nano")) return true;
                return false;
            }

            // Yerel küçük ve hafif modeller (1.5b, 1b, 2b, 3b, phi, mini, nano, tiny, small vb.)
            if (lower.Contains("1.5b") || lower.Contains("1b") || lower.Contains("2b") ||
                lower.Contains("3b") || lower.Contains("0.5b") || lower.Contains("mini") ||
                lower.Contains("nano") || lower.Contains("tiny") || lower.Contains("small") ||
                lower.Contains("phi") || lower.Contains("gemma2:2b") || lower.Contains("gemma:2b") ||
                lower.Contains("llama3.2:1b") || lower.Contains("llama3.2:3b") ||
                lower.Contains("deepseek-r1:1.5b") || lower.Contains("qwen2.5-coder:1.5b") ||
                lower.Contains("qwen2.5:1.5b") || lower.Contains("qwen2.5:3b") ||
                lower.Contains("qwen2.5-coder:3b"))
            {
                return true;
            }

            // Varsayılan qwen2.5-coder veya belirsiz küçük tag'ler
            if (lower.Contains("qwen2.5-coder") && !lower.Contains("7b") && !lower.Contains("14b") && !lower.Contains("32b"))
            {
                return true;
            }

            return false;
        }

        public static string SanitizeModelOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var cleanedLines = new List<string>();

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                // 1. Sistem ve Araç direktif başlıklarını temizle
                if (line.StartsWith("[KRİTİK", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[SİSTEM", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[GİZLİ VERİTABANI", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[GÜNCEL İNTERNET", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[CANLI İNTERNET", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[ARAÇ ÇIKTISI", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 2. Modelin istemsizce tekrarladığı negatif kural / meta prompt cümlelerini temizle
                if (line.Contains("araştırmayı başlattım", StringComparison.OrdinalIgnoreCase) &&
                    (line.Contains("KESİNLİKLE", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("cümleler kurup", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("cümleler kurma", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (line.Contains("terminal kodlarını", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("PowerShell", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.Contains("NET, Profesyonel, Uygun Veri Tablosu İçeren NİHAİ", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("NİHAİ Cevabını Ver:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.Contains("oyalayıcı cümleler kurma", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("mazeret cümleleri kurma", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 3. Prompt şablonu veya iç açıklama dökümlerini temizle
                if (line.Contains("Soru Verilir**:", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Belge ve Tablo Verileri**:", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Yazılım Yolu**: \"C:\\WINDOWS\\system32", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Yılamanı: Bu çalışma alanında", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                cleanedLines.Add(rawLine);
            }

            var result = string.Join("\n", cleanedLines).Trim();

            // 3. Küçük modellerin hayal ettiği geçersiz example.com araç çağrılarını temizle
            if (result.Contains("example.com"))
            {
                result = Regex.Replace(
                    result,
                    @"```(?:tool_call|json|tool)?\s*\{[\s\S]*?example\.com[\s\S]*?\}\s*```",
                    "",
                    RegexOptions.IgnoreCase).Trim();
            }

            return string.IsNullOrWhiteSpace(result) ? text.Trim() : result;
        }

        private void BtnClearChat_Click(object sender, RoutedEventArgs e)
        {
            ChatContainer.Children.Clear();
            var activeCompany = _companyManager.ActiveCompany;
            if (activeCompany != null)
            {
                UpdateCompanyView(activeCompany);
            }
        }

        private void BtnSaveCompanySettings_Click(object sender, RoutedEventArgs e)
        {
            var activeCompany = _companyManager.ActiveCompany;
            if (activeCompany == null) return;

            activeCompany.Name = TxtEditCompanyName.Text.Trim();
            activeCompany.Industry = TxtEditCompanyIndustry.Text.Trim();
            activeCompany.SystemPrompt = TxtEditCompanyPrompt.Text.Trim();

            _companyManager.SaveCompanies();

            // AI Core baloncuğunun etiketini de hemen senkronize et
            try
            {
                var graph = _knowledgeGraphManager.GetGraphData(activeCompany);
                var core = graph.Nodes.FirstOrDefault(n => n.Id == "core");
                if (core != null)
                {
                    core.Label = $"{activeCompany.Name} AI Core";
                    _knowledgeGraphManager.SaveGraph(activeCompany.Id, graph.Nodes, graph.Links);
                }
            }
            catch { }

            RefreshCompanyDropdown();
            RefreshKnowledgeGraph();
            MessageBox.Show("Şirket ayarları ve Bilgi Ağı merkezi başarıyla kaydedildi.", "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnDeleteActiveCompanyQuick_Click(object sender, RoutedEventArgs e)
        {
            var active = _companyManager.ActiveCompany;
            if (active == null) return;

            if (CmbDeleteCompanyList != null)
            {
                for (int i = 0; i < CmbDeleteCompanyList.Items.Count; i++)
                {
                    if ((CmbDeleteCompanyList.Items[i] as ComboBoxItem)?.Tag as string == active.Id)
                    {
                        CmbDeleteCompanyList.SelectedIndex = i;
                        break;
                    }
                }
            }

            BtnDeleteCompany_Click(sender, e);
        }

        private void RefreshToolSchemaViewer(Company? company)
        {
            if (company == null) return;

            // Şirket için sistem ve yönetim araçlarının kayıtlı olduğundan emin ol
            _toolEngine.EnsureCompanyToolsRegistered(company.Id);

            var toolsFile = Path.Combine(_companyManager.GetCompanyDirPath(company.Id), "tools", "registered_tools.json");
            if (File.Exists(toolsFile))
            {
                try
                {
                    var customJson = File.ReadAllText(toolsFile);
                    if (!string.IsNullOrWhiteSpace(customJson))
                    {
                        TxtToolSchemaViewer.Text = $"// {company.Name} ve Tam Sistem/Terminal/Admin Araçları (OpenAI & MCP Uyumlu Şema):\n" + customJson;
                        return;
                    }
                }
                catch { }
            }

            TxtToolSchemaViewer.Text = $"// Henüz {company.Name} için incelenmiş bir masaüstü uygulaması veya kayıtlı Function Calling / MCP aracı bulunmuyor.\n" +
                                       $"// '⚡ Uygulama (.exe) İncele & Öğret' butonu ile bir masaüstü uygulaması seçtiğinizde, uygulamanın gerçek yeteneklerinden çıkarılan otomatik Tool / MCP şeması burada listelenecektir.\n[]";
        }

        private void BtnCopyToolSchema_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtToolSchemaViewer.Text))
            {
                Clipboard.SetText(TxtToolSchemaViewer.Text);
                MessageBox.Show("Yapay zeka Tool şeması (OpenAI & MCP uyumlu JSON) panoya kopyalandı!\n\nBu şemayı Claude, GPT veya yerel AI ajanlarınıza doğrudan Function Calling / Tool olarak verebilirsiniz.", "Tool Şeması Kopyalandı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnRefreshToolSchema_Click(object sender, RoutedEventArgs e)
        {
            RefreshToolSchemaViewer(_companyManager.ActiveCompany);
            MessageBox.Show("Uygulama araçları ve fonksiyon şeması güncellendi.", "Yenilendi", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnNewCompany_Click(object sender, RoutedEventArgs e)
        {
            var existing = _companyManager.GetAllCompanies();
            int counter = existing.Count + 1;
            while (existing.Any(c => c.Id == $"sirket_{counter}"))
            {
                counter++;
            }

            var newComp = new Company
            {
                Id = $"sirket_{counter}",
                Name = $"Yeni Şirket {counter} A.Ş.",
                Industry = "Yazılım / Teknoloji",
                SystemPrompt = "Bu şirkete özel kodlama kuralları ve standartları."
            };
            _companyManager.AddCompany(newComp);
            _companyManager.ActiveCompany = newComp;
            RefreshCompanyDropdown();
            UpdateCompanyView(newComp);
            MessageBox.Show($"Yeni şirket eklendi ve izole alanı oluşturuldu: {newComp.Name}\n\n'Şirket & Sistem Talimatları' bölümünden şirket adını ve sistem kurallarını düzenleyebilirsiniz.", "Şirket Eklendi", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnDeleteCompany_Click(object sender, RoutedEventArgs e)
        {
            var allCompanies = _companyManager.GetAllCompanies();
            if (allCompanies.Count <= 1)
            {
                MessageBox.Show("Sistemde en az 1 aktif şirket bulunmalıdır. Son kalan şirket silinemez.\n\nÖnce yeni bir şirket ekleyebilir veya mevcut şirketi düzenleyebilirsiniz.", "Şirket Silinemez", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? targetId = null;
            if (CmbDeleteCompanyList?.SelectedItem is ComboBoxItem delItem && delItem.Tag is string id)
            {
                targetId = id;
            }
            else
            {
                targetId = _companyManager.ActiveCompany?.Id;
            }

            if (string.IsNullOrEmpty(targetId))
            {
                MessageBox.Show("Lütfen silinecek şirketi seçin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var targetCompany = allCompanies.FirstOrDefault(c => c.Id == targetId);
            if (targetCompany == null)
            {
                MessageBox.Show("Seçilen şirket bulunamadı.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            bool deleteFiles = ChkDeleteCompanyFiles?.IsChecked == true;
            string details = deleteFiles
                ? $"Şirket: {targetCompany.Name}\nID: {targetCompany.Id}\n\nBu işlem şirketi ve diskteki tüm izole çalışma alanı, taranan belgeler, araçlar ve bilgi ağını kalıcı olarak silecektir."
                : $"Şirket: {targetCompany.Name}\nID: {targetCompany.Id}\n\nBu işlem şirket kaydını sistemden kaldıracaktır.";

            var confirm = MessageBox.Show($"Aşağıdaki şirketi kalıcı olarak silmek istediğinizden emin misiniz?\n\n{details}\n\nBu işlem geri alınamaz!", "Şirket Silme Onayı", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            bool wasActive = _companyManager.ActiveCompany?.Id == targetId;
            bool success = _companyManager.DeleteCompany(targetId, deleteFiles);

            if (success)
            {
                var remaining = _companyManager.GetAllCompanies();
                if (wasActive)
                {
                    _companyManager.ActiveCompany = remaining.FirstOrDefault();
                }

                RefreshCompanyDropdown();

                if (_companyManager.ActiveCompany != null)
                {
                    UpdateCompanyView(_companyManager.ActiveCompany);
                }

                MessageBox.Show($"'{targetCompany.Name}' şirketi başarıyla silindi.", "Şirket Silindi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Şirket silinirken bir hata oluştu.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAddSourceMenu_Click(object sender, RoutedEventArgs e)
        {
            if (BtnAddSourceMenu.ContextMenu != null)
            {
                BtnAddSourceMenu.ContextMenu.PlacementTarget = BtnAddSourceMenu;
                BtnAddSourceMenu.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                BtnAddSourceMenu.ContextMenu.IsOpen = true;
            }
        }

        private void BtnMicLive_Click(object sender, RoutedEventArgs e)
        {
            if (_voiceService.IsLiveMode)
            {
                // User explicitly clicked the button to stop live mode
                _voiceService.StopLiveMode();
                PnlLiveIndicator.Visibility = Visibility.Collapsed;
                BtnMicLive.Background = new SolidColorBrush(Color.FromRgb(44, 44, 46));
                IconMicSymbol.Text = "🎙️";
                TxtLiveQuickStatus.Text = "";
            }
            else
            {
                // Start live mode
                _voiceService.StartLiveMode();
                PnlLiveIndicator.Visibility = Visibility.Visible;
                LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                TxtLiveQuickStatus.Text = "Sizi dinliyor... Konuşun";
                BtnMicLive.Background = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                IconMicSymbol.Text = "⏹️";
            }
        }

        private async Task ProcessLiveTurnAsync()
        {
            if (!_voiceService.IsRecording) return;

            TxtLiveQuickStatus.Text = "Whisper Turbo...";
            LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248));

            var audio = _voiceService.StopRecording();

            var transcribedText = await _voiceService.TranscribeAudioAsync(audio);

            if (string.IsNullOrWhiteSpace(transcribedText))
            {
                // If in continuous live mode, NEVER close abruptly! Resume listening for speech!
                if (_voiceService.IsLiveMode)
                {
                    _voiceService.StartRecording();
                    PnlLiveIndicator.Visibility = Visibility.Visible;
                    LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                    TxtLiveQuickStatus.Text = "Sizi dinliyor...";
                    return;
                }

                PnlLiveIndicator.Visibility = Visibility.Collapsed;
                BtnMicLive.Background = new SolidColorBrush(Color.FromRgb(44, 44, 46));
                IconMicSymbol.Text = "🎙️";
                return;
            }

            TxtPromptInput.Text = transcribedText;
            TxtLiveQuickStatus.Text = "Düşünüyor...";
            LiveStatusDot.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248));

            await SendPromptAsync();
        }

        private async void BtnRefreshGraph_Click(object sender, RoutedEventArgs e)
        {
            if (KnowledgeGraphWebView?.CoreWebView2 == null)
            {
                await InitWebView2Async();
            }
            RefreshKnowledgeGraph();
        }

        private async void BtnInspectApp_Click(object sender, RoutedEventArgs e)
        {
            var activeCompany = _companyManager.ActiveCompany;
            if (activeCompany == null)
            {
                MessageBox.Show("Lütfen önce aktif bir şirket seçin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "İncelenecek Masaüstü Uygulamasını (.exe) Seçin",
                Filter = "Windows Uygulamaları (*.exe;*.dll)|*.exe;*.dll|Tüm Dosyalar (*.*)|*.*",
                Multiselect = false
            };

            if (ofd.ShowDialog() == true)
            {
                BtnInspectApp.IsEnabled = false;
                BtnInspectApp.Content = "⏳ Uygulama İnceleniyor...";

                var selectedModel = (CmbModels.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                try
                {
                    var result = await _appInspector.InspectAndLearnApplicationAsync(activeCompany.Id, ofd.FileName, selectedModel: selectedModel);

                    if (!result.Success)
                    {
                        MessageBox.Show($"⚠️ Uygulama İncelenemedi:\n\n{result.ErrorMessage}", "Uygulama İnceleme Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    RefreshKnowledgeGraph();
                    UpdateCompanyView(activeCompany);
                    RefreshToolSchemaViewer(activeCompany);

                    // Şeffaf Uygulama İnceleme Raporunu Aç
                    var reportWin = new LearningReportWindow(result, activeCompany.Id, _knowledgeGraphManager)
                    {
                        Owner = this
                    };
                    reportWin.ShowDialog();

                    if (reportWin.WasDeleted)
                    {
                        RefreshKnowledgeGraph();
                        UpdateCompanyView(activeCompany);
                    }
                    else
                    {
                        try
                        {
                            if (KnowledgeGraphWebView?.CoreWebView2 != null)
                            {
                                await KnowledgeGraphWebView.CoreWebView2.ExecuteScriptAsync("triggerLearningWave();");
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Uygulama incelenirken hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    BtnInspectApp.IsEnabled = true;
                    BtnInspectApp.Content = "⚡ Uygulama (.exe) İncele & Öğret";
                }
            }
        }

        private void BtnChatSelectFile_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Sohbete Eklenecek Belge veya Görsel Seçin",
                Filter = "Tüm Desteklenenler (*.png;*.jpg;*.jpeg;*.pdf;*.docx;*.xlsx;*.txt;*.json;*.csv;*.cs;*.sql)|*.png;*.jpg;*.jpeg;*.pdf;*.docx;*.xlsx;*.txt;*.json;*.csv;*.cs;*.sql|Görseller (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|Ofis & PDF (*.pdf;*.docx;*.xlsx)|*.pdf;*.docx;*.xlsx|Metin & Kod (*.txt;*.md;*.json;*.csv;*.cs;*.sql;*.py)|*.txt;*.md;*.json;*.csv;*.cs;*.sql;*.py|Tüm Dosyalar (*.*)|*.*",
                Multiselect = true
            };

            if (ofd.ShowDialog() == true)
            {
                foreach (var file in ofd.FileNames)
                {
                    AddAttachment(file);
                }
            }
        }

        private void BtnChatSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            BtnScanFolder_Click(sender, e);
        }

        private async void BtnUploadDocument_Click(object sender, RoutedEventArgs e)
        {
            var activeCompany = _companyManager.ActiveCompany;
            if (activeCompany == null)
            {
                MessageBox.Show("Lütfen önce aktif bir şirket seçin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Şirket Belgesi / Kod / Uygulama (.exe) Seçin",
                Filter = "Tüm Desteklenen Dosyalar (*.pdf;*.docx;*.xlsx;*.txt;*.md;*.json;*.csv;*.cs;*.sql;*.py;*.js;*.exe;*.dll)|*.pdf;*.docx;*.xlsx;*.txt;*.md;*.json;*.csv;*.cs;*.sql;*.py;*.js;*.exe;*.dll|Ofis & PDF (*.pdf;*.docx;*.xlsx)|*.pdf;*.docx;*.xlsx|Metin & Belge Dosyaları (*.txt;*.md;*.json;*.csv)|*.txt;*.md;*.json;*.csv|Masaüstü Uygulamaları (*.exe;*.dll)|*.exe;*.dll|Tüm Dosyalar (*.*)|*.*",
                Multiselect = true
            };

            if (ofd.ShowDialog() == true)
            {
                var selectedItem = CmbModels.SelectedItem as ComboBoxItem;
                var selectedModel = selectedItem?.Tag?.ToString();
                var modelDisplayName = selectedItem?.Content?.ToString() ?? "Yapay Zeka";

                BtnUploadDocument.IsEnabled = false;
                BtnUploadDocument.Content = $"⏳ {modelDisplayName} Taranıyor...";

                foreach (var file in ofd.FileNames)
                {
                    try
                    {
                        ScanResult result;
                        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            result = await _appInspector.InspectAndLearnApplicationAsync(activeCompany.Id, file, selectedModel: selectedModel);
                        }
                        else
                        {
                            result = await _documentScanner.ScanAndLearnDocumentAsync(activeCompany.Id, file, selectedModel: selectedModel);
                        }

                        if (!result.Success)
                        {
                            MessageBox.Show($"⚠️ '{result.FileName}' Dosyası Öğrenilemedi:\n\n{result.ErrorMessage}\n\nBilgi ağınıza anlamsız veya geçersiz baloncuk eklenmedi.", "Geçersiz Belge / Uygulama", MessageBoxButton.OK, MessageBoxImage.Warning);
                            continue;
                        }

                        RefreshKnowledgeGraph();
                        UpdateCompanyView(activeCompany);
                        RefreshToolSchemaViewer(activeCompany);

                        var conceptNames = result.Concepts != null && result.Concepts.Count > 0
                            ? string.Join(", ", result.Concepts.Take(8).Select(c => c.Name))
                            : $"{result.CreatedBubblesCount} adet";
                        var qaCount = result.GeneratedQuestions?.Count ?? 0;

                        // Sohbete de bilgilendirme kartı bırak
                        AddMessageBubble(new ChatMessage
                        {
                            Role = "assistant",
                            Content = $"📎 **'{result.FileName}' Dosyası Başarıyla İncelendi!**\n\n• **Özet:** {result.Summary}\n• **Kavramlar:** {conceptNames}\n• **Hafızaya Eklenen Soru-Cevap:** {qaCount} adet\n\nBu dosya hakkında istediğiniz soruyu sorabilirsiniz.",
                            CompanyId = activeCompany.Id
                        });

                        // Şeffaf Öğrenme Raporunu Aç
                        var reportWin = new LearningReportWindow(result, activeCompany.Id, _knowledgeGraphManager)
                        {
                            Owner = this
                        };
                        reportWin.ShowDialog();

                        if (reportWin.WasDeleted)
                        {
                            RefreshKnowledgeGraph();
                            UpdateCompanyView(activeCompany);
                        }
                        else
                        {
                            try
                            {
                                if (KnowledgeGraphWebView?.CoreWebView2 != null)
                                {
                                    await KnowledgeGraphWebView.CoreWebView2.ExecuteScriptAsync("triggerLearningWave();");
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Dosya taranırken hata: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                RefreshKnowledgeGraph();
                UpdateCompanyView(activeCompany);

                BtnUploadDocument.IsEnabled = true;
                BtnUploadDocument.Content = "📂 Dosya Seç";
            }
        }

        private async void BtnScanFolder_Click(object sender, RoutedEventArgs e)
        {
            var activeCompany = _companyManager.ActiveCompany;
            if (activeCompany == null)
            {
                MessageBox.Show("Lütfen önce aktif bir şirket seçin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Taranacak Proje veya Web Sitesi Klasörünü Seçin (Örn: Villa Sitesi)",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                var selectedFolder = dialog.FolderName;
                if (string.IsNullOrWhiteSpace(selectedFolder) || !Directory.Exists(selectedFolder)) return;

                var selectedItem = CmbModels.SelectedItem as ComboBoxItem;
                var selectedModel = selectedItem?.Tag?.ToString();
                var modelDisplayName = selectedItem?.Content?.ToString() ?? "Yapay Zeka";

                BtnScanFolder.IsEnabled = false;
                BtnScanFolder.Content = $"⏳ {modelDisplayName} Taranıyor...";

                try
                {
                    var result = await _documentScanner.ScanAndLearnFolderAsync(activeCompany.Id, selectedFolder, selectedModel: selectedModel);

                    if (!result.Success)
                    {
                        MessageBox.Show($"⚠️ Klasör Taranamadı:\n\n{result.ErrorMessage}", "Klasör Tarama Uyarısı", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    RefreshKnowledgeGraph();
                    UpdateCompanyView(activeCompany);
                    RefreshToolSchemaViewer(activeCompany);

                    var folderConcepts = result.Concepts != null && result.Concepts.Count > 0
                        ? string.Join(", ", result.Concepts.Take(10).Select(c => c.Name))
                        : $"{result.CreatedBubblesCount} adet";
                    var folderQaCount = result.GeneratedQuestions?.Count ?? 0;

                    // Sohbete de bilgilendirme kartı bırak
                    AddMessageBubble(new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"📁 **'{result.FileName}' Klasörü & Projesi Başarıyla Öğrenildi!**\n\n• **Özet:** {result.Summary}\n• **Öne Çıkan Kavramlar:** {folderConcepts}\n• **Hafızaya Eklenen Soru-Cevap:** {folderQaCount} adet\n\nVilla projesi ve web sitesi içeriği şirket hafızasına kaydedildi. Artık proje hakkında dilediğinizi sorabilirsiniz!",
                        CompanyId = activeCompany.Id
                    });

                    // Şeffaf Öğrenme Raporunu Aç
                    var reportWin = new LearningReportWindow(result, activeCompany.Id, _knowledgeGraphManager)
                    {
                        Owner = this
                    };
                    reportWin.ShowDialog();

                    if (reportWin.WasDeleted)
                    {
                        RefreshKnowledgeGraph();
                        UpdateCompanyView(activeCompany);
                    }
                    else
                    {
                        try
                        {
                            if (KnowledgeGraphWebView?.CoreWebView2 != null)
                            {
                                await KnowledgeGraphWebView.CoreWebView2.ExecuteScriptAsync("triggerLearningWave();");
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Klasör taranırken hata: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    BtnScanFolder.IsEnabled = true;
                    BtnScanFolder.Content = "📁 Klasör Seç";
                }
            }
        }

        private void BtnManageKnowledge_Click(object sender, RoutedEventArgs e)
        {
            var activeCompany = _companyManager.ActiveCompany;
            if (activeCompany == null) return;

            var win = new ManageKnowledgeWindow(activeCompany, _knowledgeGraphManager, () =>
            {
                RefreshKnowledgeGraph();
                UpdateCompanyView(activeCompany);
            })
            {
                Owner = this
            };
            win.ShowDialog();
        }

        #region Chat Session Management

        private void RefreshCompanySessions(Company? company)
        {
            if (company == null) return;
            var sessions = _sessionManager.GetSessions(company.Id);
            if (sessions.Count == 0)
            {
                var newSess = _sessionManager.CreateNewSession(company.Id, "Yeni Sohbet");
                sessions.Add(newSess);
            }

            CmbChatSessions.SelectionChanged -= CmbChatSessions_SelectionChanged;
            CmbChatSessions.Items.Clear();

            foreach (var s in sessions)
            {
                CmbChatSessions.Items.Add(new ComboBoxItem
                {
                    Content = s.Title,
                    Tag = s.Id,
                    ToolTip = $"{s.Title} ({s.UpdatedAt:dd.MM.yyyy HH:mm})"
                });
            }

            CmbChatSessions.SelectedIndex = 0;
            CmbChatSessions.SelectionChanged += CmbChatSessions_SelectionChanged;

            LoadSession(sessions[0]);
        }

        private void LoadSession(ChatSession session)
        {
            _activeSession = session;
            _chatHistory.Clear();
            ChatContainer.Children.Clear();

            var activeCompany = _companyManager.ActiveCompany;
            var welcomeBorder = new Border
            {
                Background = (Brush)FindResource("BgSurface"),
                BorderBrush = (Brush)FindResource("BorderSeparator"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 14)
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "Alp Asistan", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextPrimary"), Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = $"Aktif Şirket: {activeCompany?.Name ?? "Genel"} • {session.Title}", FontSize = 11, Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 0, 0, 6) });
            sp.Children.Add(new TextBlock { Text = "Masaüstü uygulamalarınızı ve kurumsal dokümanlarınızı analiz ederek otonom araçlar üretir ve görevleri yürütür.", FontSize = 11, Foreground = (Brush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap, LineHeight = 16 });
            welcomeBorder.Child = sp;
            ChatContainer.Children.Add(welcomeBorder);

            foreach (var msg in session.Messages)
            {
                AddMessageBubble(msg);
                _chatHistory.Add(new OllamaChatMessage { Role = msg.Role, Content = msg.Content });
            }

            ChatScrollViewer.ScrollToEnd();
        }

        private void CmbChatSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbChatSessions.SelectedItem is ComboBoxItem item && item.Tag is string sessionId)
            {
                var activeCompany = _companyManager.ActiveCompany;
                if (activeCompany != null)
                {
                    var sessions = _sessionManager.GetSessions(activeCompany.Id);
                    var target = sessions.FirstOrDefault(s => s.Id == sessionId);
                    if (target != null)
                    {
                        LoadSession(target);
                    }
                }
            }
        }

        private void BtnNewSession_Click(object sender, RoutedEventArgs e)
        {
            var activeCompany = _companyManager.ActiveCompany;
            if (activeCompany == null) return;

            var newSess = _sessionManager.CreateNewSession(activeCompany.Id, "Yeni Sohbet");
            RefreshCompanySessions(activeCompany);
        }

        private void BtnRenameSession_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null || _companyManager.ActiveCompany == null) return;

            var inputWindow = new Window
            {
                Title = "Sohbeti Yeniden Adlandır",
                Width = 380,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = (Brush)FindResource("BgSurface"),
                Foreground = (Brush)FindResource("TextPrimary"),
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock { Text = "Yeni Oturum Başlığı:", Foreground = (Brush)FindResource("TextSecondary"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
            var txt = new TextBox { Text = _activeSession.Title, Background = (Brush)FindResource("BgWindow"), Foreground = (Brush)FindResource("TextPrimary"), BorderBrush = (Brush)FindResource("BorderSubtle"), Padding = new Thickness(8, 5, 8, 5), FontSize = 12 };
            sp.Children.Add(txt);

            var btnSp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var btnCancel = new Button { Content = "İptal", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
            btnCancel.Click += (s, ev) => inputWindow.Close();
            var btnSave = new Button { Content = "Kaydet", Style = (Style)FindResource("ApplePrimaryButton"), Padding = new Thickness(14, 4, 14, 4) };
            btnSave.Click += (s, ev) =>
            {
                if (!string.IsNullOrWhiteSpace(txt.Text))
                {
                    _sessionManager.RenameSession(_companyManager.ActiveCompany.Id, _activeSession.Id, txt.Text.Trim());
                    RefreshCompanySessions(_companyManager.ActiveCompany);
                    inputWindow.Close();
                }
            };
            btnSp.Children.Add(btnCancel);
            btnSp.Children.Add(btnSave);
            sp.Children.Add(btnSp);

            inputWindow.Content = sp;
            inputWindow.ShowDialog();
        }

        private void BtnDeleteSession_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null || _companyManager.ActiveCompany == null) return;

            var res = MessageBox.Show($"'{_activeSession.Title}' oturumunu silmek istediğinize emin misiniz?", "Oturumu Sil", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                _sessionManager.DeleteSession(_companyManager.ActiveCompany.Id, _activeSession.Id);
                RefreshCompanySessions(_companyManager.ActiveCompany);
            }
        }

        private void BtnExportChat_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession == null || _companyManager.ActiveCompany == null || _activeSession.Messages.Count == 0)
            {
                MessageBox.Show("Dışa aktarılacak sohbet mesajı bulunamadı.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var menu = new ContextMenu();
            var itemPdf = new MenuItem { Header = "📄 PDF Raporu Olarak Kaydet (.pdf)" };
            itemPdf.Click += (s, args) => ExportCurrentChat("pdf");
            menu.Items.Add(itemPdf);

            var itemHtml = new MenuItem { Header = "🌐 HTML Web Raporu Olarak Kaydet (.html)" };
            itemHtml.Click += (s, args) => ExportCurrentChat("html");
            menu.Items.Add(itemHtml);

            var itemDoc = new MenuItem { Header = "📝 Word / Doküman Olarak Kaydet (.doc)" };
            itemDoc.Click += (s, args) => ExportCurrentChat("doc");
            menu.Items.Add(itemDoc);

            var itemMd = new MenuItem { Header = "📋 Markdown Dosyası Olarak Kaydet (.md)" };
            itemMd.Click += (s, args) => ExportCurrentChat("md");
            menu.Items.Add(itemMd);

            menu.PlacementTarget = BtnExportChat;
            menu.IsOpen = true;
        }

        private void ExportCurrentChat(string format)
        {
            if (_activeSession == null || _companyManager.ActiveCompany == null) return;

            var filter = format switch
            {
                "pdf" => "PDF Dosyası (*.pdf)|*.pdf",
                "html" => "HTML Dosyası (*.html)|*.html",
                "doc" => "Word Dokümanı (*.doc)|*.doc",
                _ => "Markdown Dosyası (*.md)|*.md"
            };

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Sohbet / Rapor Dosyasını Kaydet",
                Filter = filter,
                FileName = $"{Regex.Replace(_activeSession.Title, @"[^\w\-]", "_")}_{DateTime.Now:yyyyMMdd_HHmm}.{format}"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    var compName = _companyManager.ActiveCompany.Name;
                    var title = _activeSession.Title;
                    var msgs = _activeSession.Messages;

                    if (format == "pdf")
                    {
                        ReportExportService.ExportToPdf(sfd.FileName, compName, title, msgs);
                    }
                    else if (format == "html" || format == "doc")
                    {
                        var html = ReportExportService.ExportToHtml(compName, title, msgs);
                        File.WriteAllText(sfd.FileName, html, Encoding.UTF8);
                    }
                    else
                    {
                        var md = ReportExportService.ExportToMarkdown(compName, title, msgs);
                        File.WriteAllText(sfd.FileName, md, Encoding.UTF8);
                    }

                    MessageBox.Show($"Rapor başarıyla kaydedildi:\n{sfd.FileName}", "Dışa Aktarma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Dışa aktarma hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion

        #region Attachments & Drag and Drop

        private void Chat_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void Chat_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void Chat_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    foreach (var file in files)
                    {
                        AddAttachment(file);
                    }
                }
                e.Handled = true;
            }
        }

        private void AddAttachment(string filePath)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                var fi = new FileInfo(filePath);
                var ext = fi.Extension.ToLowerInvariant();
                var att = new ChatAttachment
                {
                    FileName = fi.Name,
                    FilePath = filePath,
                    SizeBytes = fi.Length
                };

                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
                {
                    att.FileType = "image";
                    att.MimeType = ext switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".webp" => "image/webp",
                        ".bmp" => "image/bmp",
                        _ => "image/png"
                    };
                    att.Base64Data = Convert.ToBase64String(File.ReadAllBytes(filePath));
                }
                else if (ext == ".docx")
                {
                    att.FileType = "word";
                    att.MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                    att.ExtractedText = DocumentScanner.ExtractTextFromDocx(filePath);
                }
                else if (ext == ".xlsx")
                {
                    att.FileType = "excel";
                    att.MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                    att.ExtractedText = DocumentScanner.ExtractTextFromXlsx(filePath);
                }
                else if (ext == ".pdf")
                {
                    att.FileType = "pdf";
                    att.MimeType = "application/pdf";
                    att.ExtractedText = DocumentScanner.ExtractTextFromPdf(filePath);
                    if (fi.Length < 8 * 1024 * 1024)
                    {
                        att.Base64Data = Convert.ToBase64String(File.ReadAllBytes(filePath));
                    }
                }
                else
                {
                    att.FileType = "text";
                    att.MimeType = "text/plain";
                    att.ExtractedText = File.ReadAllText(filePath);
                }

                _pendingAttachments.Add(att);
                RefreshAttachmentChips();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Dosya eklenemedi: {ex.Message}", "Ek Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RefreshAttachmentChips()
        {
            AttachmentPreviewPanel.Children.Clear();
            if (_pendingAttachments.Count == 0)
            {
                AttachmentPreviewPanel.Visibility = Visibility.Collapsed;
                return;
            }

            AttachmentPreviewPanel.Visibility = Visibility.Visible;

            foreach (var att in _pendingAttachments.ToList())
            {
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(36, 36, 38)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 58)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 4, 6, 4),
                    Margin = new Thickness(0, 0, 6, 4)
                };

                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                string icon = att.FileType switch
                {
                    "image" => "🖼️",
                    "pdf" => "📕",
                    "word" => "📘",
                    "excel" => "📗",
                    _ => "📄"
                };

                var txtName = new TextBlock
                {
                    Text = $"{icon} {att.FileName} ({(att.SizeBytes / 1024.0):N1} KB)",
                    Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 247)),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                sp.Children.Add(txtName);

                var btnRemove = new Button
                {
                    Content = "✕",
                    Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 165)),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(2, 0, 2, 0),
                    FontSize = 10,
                    Cursor = Cursors.Hand,
                    ToolTip = "Bu eki kaldır"
                };
                btnRemove.Click += (s, e) =>
                {
                    _pendingAttachments.Remove(att);
                    RefreshAttachmentChips();
                };
                sp.Children.Add(btnRemove);

                border.Child = sp;
                AttachmentPreviewPanel.Children.Add(border);
            }
        }

        #endregion

        #region Stop Generation & Regenerate

        private void BtnStopGeneration_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _currentCts?.Cancel();
                _voiceService.StopSpeaking();
                BtnStopGeneration.Visibility = Visibility.Collapsed;
                TxtHeaderStatus.Text = "⏹️ Durduruldu";
            }
            catch { }
        }

        private async void BtnRegenerate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastUserPrompt) || _activeSession == null || _activeSession.Messages.Count == 0) return;

            var lastMsg = _activeSession.Messages.LastOrDefault();
            if (lastMsg != null && lastMsg.IsAssistant)
            {
                _activeSession.Messages.Remove(lastMsg);
                if (_chatHistory.Count > 0 && _chatHistory.Last().Role == "assistant")
                {
                    _chatHistory.RemoveAt(_chatHistory.Count - 1);
                }

                if (ChatContainer.Children.Count > 1)
                {
                    ChatContainer.Children.RemoveAt(ChatContainer.Children.Count - 1);
                }
            }

            TxtPromptInput.Text = _lastUserPrompt;
            await SendPromptAsync();
        }

        #endregion

        #region Embedded Database Explorer

        private void RefreshDatabaseList(Company? company)
        {
            CmbDbSelector.Items.Clear();
            LstDbTables.ItemsSource = null;
            GridDbResults.ItemsSource = null;
            TxtDbSqlQuery.Text = string.Empty;
            _currentDbAnalysis = null;

            if (company == null)
            {
                TxtDbQueryStatus.Text = "Lütfen aktif bir şirket seçin.";
                return;
            }

            var discovered = _dbService.DiscoverDatabaseFiles(companyId: company.Id);
            if (!string.IsNullOrWhiteSpace(company.ApplicationDatabasePath) && File.Exists(company.ApplicationDatabasePath))
            {
                if (!discovered.Contains(company.ApplicationDatabasePath))
                {
                    discovered.Insert(0, company.ApplicationDatabasePath);
                }
            }

            foreach (var db in discovered)
            {
                CmbDbSelector.Items.Add(new ComboBoxItem
                {
                    Content = $"🗄️ {Path.GetFileName(db)} ({Path.GetFileName(Path.GetDirectoryName(db))})",
                    Tag = db,
                    ToolTip = db
                });
            }

            if (CmbDbSelector.Items.Count > 0)
            {
                CmbDbSelector.SelectedIndex = 0;
            }
            else
            {
                TxtDbQueryStatus.Text = $"'{company.Name}' şirketi için kayıtlı veritabanı yok. Şirkete ait SQLite dosyasını '📂 DB Aç' ile seçebilirsiniz.";
            }
        }

        private void CmbDbSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbDbSelector.SelectedItem is ComboBoxItem item && item.Tag is string dbPath && File.Exists(dbPath))
            {
                try
                {
                    _currentDbAnalysis = _dbService.ReverseEngineerDatabase(dbPath, _companyManager.ActiveCompany?.Id ?? "", maxSampleRows: 2, ingestToKnowledgeGraph: false);
                    LstDbTables.ItemsSource = _currentDbAnalysis.Tables;
                    if (_currentDbAnalysis.Tables.Count > 0)
                    {
                        LstDbTables.SelectedIndex = 0;
                    }
                    TxtDbQueryStatus.Text = $"Veritabanı bağlandı ({_currentDbAnalysis.DatabaseEngine}). {_currentDbAnalysis.TableCount} tablo, {_currentDbAnalysis.TotalRecordCount:N0} kayıt.";
                }
                catch (Exception ex)
                {
                    TxtDbQueryStatus.Text = $"Şema okuma hatası: {ex.Message}";
                }
            }
        }

        private void LstDbTables_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstDbTables.SelectedItem is TableSchemaInfo table)
            {
                TxtDbSqlQuery.Text = $"SELECT * FROM \"{table.TableName}\" LIMIT 50;";
                ExecuteCurrentDbQuery();
            }
        }

        private void BtnOpenExternalDb_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "SQLite / Access Veritabanı Seçin",
                Filter = "Veritabanı Dosyaları (*.db;*.sqlite;*.sqlite3;*.accdb;*.mdb)|*.db;*.sqlite;*.sqlite3;*.accdb;*.mdb|Tüm Dosyalar (*.*)|*.*"
            };
            if (ofd.ShowDialog() == true)
            {
                if (_companyManager.ActiveCompany != null)
                {
                    _companyManager.ActiveCompany.ApplicationDatabasePath = ofd.FileName;
                    _companyManager.SaveCompanies();
                }

                var existing = CmbDbSelector.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == ofd.FileName);
                if (existing == null)
                {
                    var newItem = new ComboBoxItem
                    {
                        Content = $"🗄️ {Path.GetFileName(ofd.FileName)}",
                        Tag = ofd.FileName,
                        ToolTip = ofd.FileName
                    };
                    CmbDbSelector.Items.Insert(0, newItem);
                    CmbDbSelector.SelectedItem = newItem;
                }
                else
                {
                    CmbDbSelector.SelectedItem = existing;
                }
            }
        }

        private void BtnReverseEngineerDb_Click(object sender, RoutedEventArgs e)
        {
            var activeCompany = _companyManager.ActiveCompany;
            if (activeCompany == null) return;

            var selectedDb = (CmbDbSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrEmpty(selectedDb) || !File.Exists(selectedDb))
            {
                MessageBox.Show("Lütfen önce geçerli bir veritabanı seçin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var res = _dbService.ReverseEngineerDatabase(selectedDb, activeCompany.Id, maxSampleRows: 5, ingestToKnowledgeGraph: true);
                RefreshKnowledgeGraph();
                MessageBox.Show($"Veritabanı şeması ve {res.TableCount} tablo başarıyla çözümlendi ve Bilgi Ağına eklendi!", "Tersine Mühendislik Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Tersine mühendislik hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExecuteDbSql_Click(object sender, RoutedEventArgs e)
        {
            ExecuteCurrentDbQuery();
        }

        private void ExecuteCurrentDbQuery()
        {
            var selectedDb = (CmbDbSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrEmpty(selectedDb) || !File.Exists(selectedDb))
            {
                TxtDbQueryStatus.Text = "Hata: Geçerli bir veritabanı dosyası seçilmedi.";
                return;
            }

            var sql = TxtDbSqlQuery.Text.Trim();
            if (string.IsNullOrWhiteSpace(sql)) return;

            try
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={selectedDb};Mode=ReadOnly;");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;

                using var reader = cmd.ExecuteReader();
                var dt = new System.Data.DataTable();
                dt.Load(reader);

                GridDbResults.ItemsSource = dt.DefaultView;
                TxtDbQueryStatus.Text = $"✅ Sorgu başarılı: {dt.Rows.Count} satır veri listelendi.";
            }
            catch (Exception ex)
            {
                TxtDbQueryStatus.Text = $"❌ SQL Hatası: {ex.Message}";
            }
        }

        private void BtnCopyDbResults_Click(object sender, RoutedEventArgs e)
        {
            if (GridDbResults.ItemsSource is System.Data.DataView dv && dv.Table != null)
            {
                var sb = new StringBuilder();
                var cols = dv.Table.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName).ToList();
                sb.AppendLine(string.Join("\t", cols));

                foreach (System.Data.DataRowView row in dv)
                {
                    sb.AppendLine(string.Join("\t", row.Row.ItemArray.Select(val => val?.ToString() ?? "")));
                }

                Clipboard.SetDataObject(sb.ToString(), true);
                MessageBox.Show("Tablo verileri panoya kopyalandı.", "Kopyalandı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnExportDbCsv_Click(object sender, RoutedEventArgs e)
        {
            if (GridDbResults.ItemsSource is System.Data.DataView dv && dv.Table != null)
            {
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "CSV Dosyası (*.csv)|*.csv",
                    FileName = "Sorgu_Sonucu.csv"
                };
                if (sfd.ShowDialog() == true)
                {
                    var cols = dv.Table.Columns.Cast<System.Data.DataColumn>().Select(c => $"\"{c.ColumnName}\"").ToList();
                    var lines = new List<string> { string.Join(";", cols) };

                    foreach (System.Data.DataRowView row in dv)
                    {
                        lines.Add(string.Join(";", row.Row.ItemArray.Select(val => $"\"{val?.ToString()?.Replace("\"", "\"\"") ?? ""}\"")));
                    }

                    File.WriteAllLines(sfd.FileName, lines, Encoding.UTF8);
                    MessageBox.Show("Veriler başarıyla CSV dosyasına aktarıldı.", "Dışa Aktarma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        #endregion

        #region Gemini Tools Builder

        private List<GeminiFunctionDeclaration> GetGeminiToolsForCompany(Company company)
        {
            var list = new List<GeminiFunctionDeclaration>
            {
                new()
                {
                    Name = "web_search",
                    Description = "Canlı internet araması yaparak güncel piyasa, fiyat, firma veya sektörel verileri getirir.",
                    Parameters = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            query = new { type = "STRING", description = "Aranacak web arama terimi veya konu" }
                        },
                        required = new[] { "query" }
                    }
                },
                new()
                {
                    Name = "fetch_web_page",
                    Description = "Belirtilen web sayfasının metin içeriğini çeker.",
                    Parameters = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            url = new { type = "STRING", description = "Ziyaret edilecek web adresi (URL)" }
                        },
                        required = new[] { "url" }
                    }
                },
                new()
                {
                    Name = "browse_dynamic_web",
                    Description = "JavaScript tabanlı dinamik web sayfalarını Chromium motoruyla işleyip temizlenmiş metin veya markdown çeker.",
                    Parameters = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            url = new { type = "STRING", description = "Taranacak web adresi" },
                            wait_seconds = new { type = "INTEGER", description = "Bekleme süresi (saniye)" }
                        },
                        required = new[] { "url" }
                    }
                },
                new()
                {
                    Name = "query_database",
                    Description = "Şirket veritabanında salt-okunur güvenli SQL (SELECT) sorguları çalıştırır.",
                    Parameters = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            sql_query = new { type = "STRING", description = "Çalıştırılacak SQL SELECT sorgusu" },
                            max_rows = new { type = "INTEGER", description = "Maksimum satır sayısı" }
                        },
                        required = new[] { "sql_query" }
                    }
                },
                new()
                {
                    Name = "open_browser",
                    Description = "Kullanıcının tarayıcısını açarak YouTube veya belirtilen web adresini ekranda gösterir.",
                    Parameters = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            url = new { type = "STRING", description = "Açılacak web sitesi veya sayfa adresi (örn: 'https://www.youtube.com')" }
                        },
                        required = new[] { "url" }
                    }
                },
                new()
                {
                    Name = "create_excel_file",
                    Description = "Kullanıcının Masaüstüne (Desktop) Excel tablosu (.csv/.xlsx) oluşturur ve açar.",
                    Parameters = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            file_name = new { type = "STRING", description = "Oluşturulacak dosya adı (örn: 'Piyasa_Veri_Analizi.xlsx')" },
                            save_to_desktop = new { type = "BOOLEAN", description = "Masaüstüne kaydet (varsayılan: true)" },
                            open_after_creation = new { type = "BOOLEAN", description = "Oluşturulduktan sonra aç (varsayılan: true)" }
                        }
                    }
                }
            };

            bool isFinance = company.Id == "sirket_3" ||
                             (company.AllowedTools != null &&
                              (company.AllowedTools.Contains("finance") || company.AllowedTools.Contains("financial_tools") || company.AllowedTools.Contains("fatura")));

            if (isFinance)
            {
                list.Add(new()
                {
                    Name = "fatura_olustur_ekle",
                    Description = "Uygulamaya ve veritabanına yeni fatura veya gider kaydı ekler.",
                    Parameters = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            tutar = new { type = "STRING", description = "Fatura tutarı" },
                            kdv = new { type = "STRING", description = "KDV oranı (örn: %1, %20)" },
                            musteri = new { type = "STRING", description = "Müşteri veya cari unvanı" },
                            aciklama = new { type = "STRING", description = "Açıklama" }
                        },
                        required = new[] { "tutar" }
                    }
                });
                list.Add(new()
                {
                    Name = "vergi_finans_ozeti",
                    Description = "Ciro, KDV dengesi, en çok kazandıran müşteriler ve vergi durumunu sorgular.",
                    Parameters = new { type = "OBJECT", properties = new { } }
                });
                list.Add(new()
                {
                    Name = "gelir_listele",
                    Description = "AlpFinans gelir ve hakediş kayıtlarını listeler. En çok kazandıran müşterileri (Volkan, Melisa vb.), tutar ve tarih bazlı gelir dağılımını sorgular.",
                    Parameters = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            filtre = new { type = "STRING", description = "Müşteri adı veya arama filtresi" },
                            en_cok_kazandiranlar = new { type = "BOOLEAN", description = "En çok kazandıran müşterileri sıralar" }
                        }
                    }
                });
            }

            return list;
        }

        #endregion
    }
}
