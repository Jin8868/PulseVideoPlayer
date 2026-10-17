using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PulseVideoTool
{
    internal sealed class MainWindow : Window
    {
        private readonly ToolLog m_log; // 当前运行的持久化日志与界面队列。
        private readonly TextBox m_input = new TextBox(); // 输入目录。
        private readonly TextBox m_output = new TextBox(); // 输出目录。
        private readonly CheckBox m_recurse = new CheckBox(); // 是否递归扫描 MP4。
        private readonly CheckBox m_alpha = new CheckBox(); // 透明视频拼接开关。
        private readonly ComboBox m_frameRate = new ComboBox(); // 固定帧率选择。
        private readonly ComboBox m_crf = new ComboBox(); // 编码质量选择。
        private readonly TextBox m_gop = new TextBox(); // 关键帧间隔。
        private readonly ComboBox m_preset = new ComboBox(); // 编码速度预设。
        private readonly ListBox m_files = new ListBox(); // 支持多选的 MP4 文件列表。
        private readonly Grid m_filePanel = new Grid(); // 视频列表及其独立选择操作栏。
        private readonly TextBox m_logs = new TextBox(); // 实时日志显示区域。
        private readonly TextBlock m_status = new TextBlock(); // 当前操作状态。
        private readonly TextBlock m_installStatus = new TextBlock(); // FFmpeg 缓存状态。
        private readonly TextBlock m_selection = new TextBlock(); // 扫描和选中文件数量。
        private readonly ProgressBar m_stageProgress = new ProgressBar(); // 当前处理阶段进度。
        private readonly ProgressBar m_batchProgress = new ProgressBar(); // 批量文件完成进度。
        private readonly Button m_initialize = new Button(); // 离线初始化入口。
        private readonly Button m_start = new Button(); // 转码入口。
        private readonly Button m_cancel = new Button(); // 当前操作取消入口。
        private readonly StackPanel m_options = new StackPanel(); // 操作期间锁定输入与编码参数。
        private readonly Grid m_root = new Grid(); // 按初始化状态调整文件区布局。
        private readonly TextBlock m_title = new TextBlock(); // 当前初始化或转码界面的标题。
        private readonly TextBlock m_initializationHint = new TextBlock(); // 未就绪时显示的离线初始化说明。
        private readonly DispatcherTimer m_logTimer = new DispatcherTimer(); // 批量刷新日志避免阻塞界面。
        private CancellationTokenSource m_cancellation; // 当前后台操作的取消源。
        private bool m_isReady; // 经过验证的 FFmpeg 缓存状态。
        private bool m_isBusy; // 阻止重复操作与关闭中的资源争用。
        private bool m_closeRequested; // 用户已确认取消任务并关闭。
        private int m_completedCount; // 后台线程提交的批次完成文件数。
        private string m_operation = ""; // 当前后台任务的中文名称。

        internal MainWindow(ToolLog log)
        {
            m_log = log;
            Title = "Pulse 视频处理工具";
            Width = 1060;
            Height = 850;
            MinWidth = 850;
            MinHeight = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            Background = new SolidColorBrush(Color.FromRgb(245, 247, 250));
            Content = BuildContent();
            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosing;
            Loaded += OnLoaded;
            Application.Current.DispatcherUnhandledException += OnUnhandledException;
            m_logTimer.Interval = TimeSpan.FromMilliseconds(120);
            m_logTimer.Tick += OnLogTick;
            m_logTimer.Start();
            RefreshControls();
        }

        private UIElement BuildContent()
        {
            Grid root = m_root;
            root.Margin = new Thickness(20);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var heading = new StackPanel();
            m_title.FontSize = 24;
            m_title.FontWeight = FontWeights.SemiBold;
            heading.Children.Add(m_title);
            var installation = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 12)
            };
            m_installStatus.Text = "正在检查本地 FFmpeg……";
            m_installStatus.VerticalAlignment = VerticalAlignment.Center;
            installation.Children.Add(m_installStatus);
            ConfigureButton(m_initialize, "初始化 FFmpeg（离线解压）", OnInitialize);
            installation.Children.Add(m_initialize);
            heading.Children.Add(installation);
            m_initializationHint.Text = "首次使用需要解压随工具附带的 FFmpeg。点击初始化后即可使用，无需联网或重启。";
            m_initializationHint.TextWrapping = TextWrapping.Wrap;
            m_initializationHint.Margin = new Thickness(0, 0, 0, 12);
            heading.Children.Add(m_initializationHint);
            heading.Children.Add(m_options);
            m_options.Children.Add(BuildDirectoryRow("输入目录", m_input, OnBrowseInput));
            m_options.Children.Add(BuildDirectoryRow("输出目录", m_output, OnBrowseOutput));
            var scanOptions = new WrapPanel();
            m_recurse.Content = "扫描子目录";
            m_recurse.VerticalAlignment = VerticalAlignment.Center;
            scanOptions.Children.Add(m_recurse);
            scanOptions.Children.Add(MakeButton("扫描 / 刷新 MP4", OnScan));
            scanOptions.Children.Add(MakeButton("选择 MP4 文件…", OnSelectFiles));
            m_options.Children.Add(scanOptions);
            var encoding = new WrapPanel { Margin = new Thickness(0, 5, 0, 5) };
            AddParameter(encoding, "帧率", m_frameRate, 70);
            AddParameter(encoding, "CRF", m_crf, 65);
            AddParameter(encoding, "GOP", m_gop, 55);
            AddParameter(encoding, "编码速度", m_preset, 105);
            foreach (int frameRate in new[] { 15, 24, 25, 30, 48, 50, 60, 90, 120 })
                m_frameRate.Items.Add(frameRate);
            m_crf.ItemsSource = Enumerable.Range(0, 52).ToArray();
            m_frameRate.MaxDropDownHeight = 250;
            m_crf.MaxDropDownHeight = 250;
            m_preset.ItemsSource = new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow" };
            m_preset.ToolTip = "从 ultrafast 到 slow，编码耗时通常增加、压缩效率通常提高；不改变播放速度。默认 veryfast。";
            m_crf.ToolTip = "0～51，数值越小画质越高，通常文件越大；默认 18。";
            m_alpha.Content = "透明通道上下拼接";
            m_alpha.Margin = new Thickness(10, 5, 0, 0);
            encoding.Children.Add(m_alpha);
            m_options.Children.Add(encoding);
            m_options.Children.Add(new TextBlock
            {
                Text = "编码速度影响转码耗时和压缩效率，不影响播放速度；CRF 越小画质越高，通常文件越大。",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5)
            });
            m_options.Children.Add(new TextBlock
            {
                Text = "输出：.pulse / 独立音频 / 字幕；元数据已内嵌。AES-CTR 加密需播放器解密层支持，当前版本未启用。",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(heading);

            // 文件区与日志区分别滚动，日志增长不挤占参数和操作入口。
            m_files.SelectionMode = SelectionMode.Multiple;
            m_files.ItemTemplate = BuildFileTemplate();
            var fileStyle = new Style(typeof(ListBoxItem));
            fileStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            m_files.ItemContainerStyle = fileStyle;
            m_files.SelectionChanged += OnSelectionChanged;
            m_files.MinHeight = 100;
            m_filePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            m_filePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var fileActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            fileActions.Children.Add(new TextBlock
            {
                Text = "视频选择", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold
            });
            fileActions.Children.Add(MakeButton("全选", OnSelectAll));
            fileActions.Children.Add(MakeButton("取消全选", OnUnselectAll));
            m_selection.VerticalAlignment = VerticalAlignment.Center;
            m_selection.Margin = new Thickness(12, 0, 0, 0);
            m_selection.Text = "已选 0 / 0";
            fileActions.Children.Add(m_selection);
            m_filePanel.Children.Add(fileActions);
            Grid.SetRow(m_files, 1);
            m_filePanel.Children.Add(m_files);
            Grid.SetRow(m_filePanel, 1);
            root.Children.Add(m_filePanel);
            var controls = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
            var actions = new WrapPanel();
            ConfigureButton(m_start, "开始转码", OnStart);
            ConfigureButton(m_cancel, "取消任务", OnCancel);
            actions.Children.Add(m_start);
            actions.Children.Add(m_cancel);
            actions.Children.Add(MakeButton("复制全部日志（Ctrl+Shift+C）", OnCopyLog));
            actions.Children.Add(MakeButton("打开日志目录", OnOpenLogs));
            controls.Children.Add(actions);
            m_status.Text = "准备就绪";
            m_status.Margin = new Thickness(0, 5, 0, 5);
            controls.Children.Add(m_status);
            m_stageProgress.Height = 10;
            m_stageProgress.Maximum = 100;
            controls.Children.Add(m_stageProgress);
            m_batchProgress.Height = 5;
            m_batchProgress.Margin = new Thickness(0, 4, 0, 0);
            controls.Children.Add(m_batchProgress);
            Grid.SetRow(controls, 2);
            root.Children.Add(controls);
            m_logs.IsReadOnly = true;
            m_logs.AcceptsReturn = true;
            m_logs.FontFamily = new FontFamily("Consolas");
            m_logs.FontSize = 12;
            m_logs.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            m_logs.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            Grid.SetRow(m_logs, 3);
            root.Children.Add(m_logs);
            return root;
        }

        private static UIElement BuildDirectoryRow(string label, TextBox input, RoutedEventHandler browse)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var caption = new TextBlock { Text = label, Width = 72, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(caption, Dock.Left);
            row.Children.Add(caption);
            Button button = MakeButton("选择…", browse);
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            input.VerticalContentAlignment = VerticalAlignment.Center;
            input.MinHeight = 30;
            row.Children.Add(input);
            return row;
        }

        private static DataTemplate BuildFileTemplate()
        {
            // 勾选状态直接绑定列表选中状态，普通点击即可切换，不依赖 Ctrl 或 Shift。
            var checkbox = new FrameworkElementFactory(typeof(CheckBox));
            checkbox.SetBinding(ContentControl.ContentProperty, new Binding("."));
            checkbox.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3));
            checkbox.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsSelected")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1),
                Mode = BindingMode.TwoWay
            });
            return new DataTemplate(typeof(string)) { VisualTree = checkbox };
        }

        private static void AddParameter(Panel panel, string label, Control input, double width)
        {
            panel.Children.Add(new TextBlock
            {
                Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });
            input.Width = width;
            input.Margin = new Thickness(0, 0, 12, 0);
            input.MinHeight = 27;
            panel.Children.Add(input);
        }

        private static Button MakeButton(string label, RoutedEventHandler handler)
        {
            var button = new Button();
            ConfigureButton(button, label, handler);
            return button;
        }

        private static void ConfigureButton(Button button, string label, RoutedEventHandler handler)
        {
            button.Content = label;
            button.Padding = new Thickness(12, 6, 12, 6);
            button.Margin = new Thickness(6, 0, 0, 0);
            button.Click += handler;
        }

        private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
        {
            try { ApplySettings(ToolSettings.Load()); }
            catch (Exception exception)
            {
                ApplySettings(new ToolSettings());
                ShowError("读取配置失败，已使用默认设置。", exception);
            }
            BeginOperation("检查 FFmpeg");
            try
            {
                m_isReady = await Task.Run(delegate
                {
                    return OfflineInstaller.IsReady(m_log.Write, m_cancellation.Token);
                });
                m_status.Text = m_isReady ? "可以开始转码。" : "请先点击初始化，离线解压后即可使用。";
            }
            catch (OperationCanceledException) { m_status.Text = "检查已取消。"; }
            catch (Exception exception) { ShowError("检查 FFmpeg 失败。", exception); }
            finally { EndOperation(); }
        }

        private async void OnInitialize(object sender, RoutedEventArgs eventArgs)
        {
            BeginOperation("初始化 FFmpeg");
            m_isReady = false;
            try
            {
                await Task.Run(delegate { OfflineInstaller.Install(m_log.Write, m_cancellation.Token); });
                m_isReady = true;
                m_status.Text = "初始化完成，可以直接开始转码。";
            }
            catch (OperationCanceledException) { m_status.Text = "初始化已取消，可以重试。"; }
            catch (Exception exception) { ShowError("FFmpeg 初始化失败。", exception); }
            finally { EndOperation(); }
        }

        private void ApplySettings(ToolSettings settings)
        {
            m_input.Text = settings.InputDirectory ?? "";
            m_output.Text = settings.OutputDirectory ?? "";
            m_recurse.IsChecked = settings.Recurse;
            m_alpha.IsChecked = settings.Alpha;
            int frameRate = settings.FrameRate >= 1 && settings.FrameRate <= 120 ? settings.FrameRate : 30;
            if (!m_frameRate.Items.Contains(frameRate)) m_frameRate.Items.Add(frameRate);
            m_frameRate.SelectedItem = frameRate;
            m_crf.SelectedItem = settings.CRF >= 0 && settings.CRF <= 51 ? settings.CRF : 18;
            m_gop.Text = settings.GOP.ToString();
            m_preset.SelectedItem = settings.Preset;
            if (m_preset.SelectedIndex < 0) m_preset.SelectedItem = "veryfast";
        }

        private void SaveSettings()
        {
            var settings = new ToolSettings();
            settings.InputDirectory = m_input.Text.Trim();
            settings.OutputDirectory = m_output.Text.Trim();
            settings.Recurse = m_recurse.IsChecked == true;
            settings.Alpha = m_alpha.IsChecked == true;
            int value;
            settings.FrameRate = int.TryParse(m_frameRate.Text, out value) ? value : 30;
            settings.CRF = int.TryParse(m_crf.Text, out value) ? value : 18;
            settings.GOP = int.TryParse(m_gop.Text, out value) ? value : 60;
            settings.Preset = (string)m_preset.SelectedItem;
            settings.Save();
        }

        private void OnBrowseInput(object sender, RoutedEventArgs eventArgs)
        {
            if (BrowseDirectory(m_input)) OnScan(sender, eventArgs);
        }

        private void OnBrowseOutput(object sender, RoutedEventArgs eventArgs) { BrowseDirectory(m_output); }

        private void OnSelectFiles(object sender, RoutedEventArgs eventArgs)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择需要转码的 MP4，可同时选择多个文件",
                Filter = "MP4 视频 (*.mp4)|*.mp4",
                Multiselect = true,
                CheckFileExists = true
            };
            if (Directory.Exists(m_input.Text.Trim())) dialog.InitialDirectory = m_input.Text.Trim();
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                string[] paths = dialog.FileNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
                m_files.ItemsSource = paths;
                m_files.SelectAll();
                m_input.Text = Path.GetDirectoryName(paths[0]);
                SaveSettings();
                m_status.Text = "已加入 " + paths.Length + " 个 MP4，勾选需要批量转码的文件。";
                m_log.Write("[选择] " + m_status.Text);
            }
            catch (Exception exception) { ShowError("选择视频文件失败。", exception); }
        }

        private bool BrowseDirectory(TextBox target)
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择视频目录";
                dialog.SelectedPath = target.Text;
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return false;
                target.Text = dialog.SelectedPath;
                try { SaveSettings(); }
                catch (Exception exception) { ShowError("保存目录失败。", exception); }
                return true;
            }
        }

        private async void OnScan(object sender, RoutedEventArgs eventArgs)
        {
            BeginOperation("扫描 MP4");
            string directory = m_input.Text.Trim();
            bool recurse = m_recurse.IsChecked == true;
            try
            {
                SaveSettings();
                string[] files = await Task.Run(delegate
                {
                    var found = new List<string>();
                    var pending = new Queue<string>();
                    pending.Enqueue(directory);
                    while (pending.Count > 0)
                    {
                        m_cancellation.Token.ThrowIfCancellationRequested();
                        string current = pending.Dequeue();
                        foreach (string file in Directory.EnumerateFiles(current, "*.mp4")) found.Add(file);
                        if (!recurse) continue;
                        foreach (string child in Directory.EnumerateDirectories(current))
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Enqueue(child);
                    }
                    found.Sort(StringComparer.OrdinalIgnoreCase);
                    return found.ToArray();
                });
                m_files.ItemsSource = files;
                m_files.SelectAll();
                m_status.Text = "找到 " + files.Length + " 个 MP4，可直接勾选多个文件后批量转码。";
                m_log.Write("[扫描] " + m_status.Text);
            }
            catch (OperationCanceledException) { m_status.Text = "扫描已取消。"; }
            catch (Exception exception) { ShowError("扫描输入目录失败。", exception); }
            finally { EndOperation(); }
        }

        private async void OnStart(object sender, RoutedEventArgs eventArgs)
        {
            ExportOptions options;
            try
            {
                if (!m_isReady) throw new InvalidOperationException("请先初始化 FFmpeg。");
                if (string.IsNullOrWhiteSpace(m_output.Text)) throw new ArgumentException("请选择输出目录。");
                if (m_files.SelectedItems.Count == 0) throw new ArgumentException("请选择需要转码的 MP4。");
                options = new ExportOptions();
                options.InputFiles = m_files.SelectedItems.Cast<string>().ToArray();
                options.OutputDirectory = Path.GetFullPath(m_output.Text.Trim());
                options.FrameRate = ParseNumber(m_frameRate.Text, "帧率", 1, 120);
                options.CRF = ParseNumber(m_crf.Text, "CRF", 0, 51);
                options.GOP = ParseNumber(m_gop.Text, "GOP", 1, 600);
                options.Preset = (string)m_preset.SelectedItem;
                options.Alpha = m_alpha.IsChecked == true;
                SaveSettings();
            }
            catch (Exception exception) { ShowError("无法开始转码。", exception); return; }
            BeginOperation("视频转码");
            m_batchProgress.Maximum = options.InputFiles.Length;
            try
            {
                await Task.Run(delegate
                {
                    VideoExporter.Export(options, m_log.Write, OnFileCompleted, m_cancellation.Token);
                });
                m_stageProgress.Value = 100;
                m_status.Text = "转码完成，产物已保存至：" + options.OutputDirectory;
            }
            catch (OperationCanceledException)
            {
                m_status.Text = "转码已取消；已完成产物保留，未完成的临时文件已清理。";
                m_log.Write("[取消] " + m_status.Text);
            }
            catch (Exception exception) { ShowError("视频转码失败，请查看日志。", exception); }
            finally { EndOperation(); }
        }

        private static int ParseNumber(string text, string label, int minimum, int maximum)
        {
            int value;
            if (!int.TryParse(text, out value) || value < minimum || value > maximum)
                throw new ArgumentException(label + " 必须为 " + minimum + "～" + maximum + " 的整数。");
            return value;
        }

        private void OnFileCompleted(int count) { Interlocked.Exchange(ref m_completedCount, count); }
        private void OnSelectAll(object sender, RoutedEventArgs eventArgs) { m_files.SelectAll(); }
        private void OnUnselectAll(object sender, RoutedEventArgs eventArgs) { m_files.UnselectAll(); }
        private void OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
        {
            m_selection.Text = "已选 " + m_files.SelectedItems.Count + " / " + m_files.Items.Count;
        }

        private void BeginOperation(string name)
        {
            m_isBusy = true;
            m_operation = name;
            m_cancellation = new CancellationTokenSource();
            m_status.Text = name + "……";
            m_stageProgress.Value = 0;
            m_stageProgress.IsIndeterminate = name != "视频转码";
            m_batchProgress.Value = 0;
            Interlocked.Exchange(ref m_completedCount, 0);
            RefreshControls();
        }

        private void EndOperation()
        {
            FlushLogs();
            m_isBusy = false;
            m_stageProgress.IsIndeterminate = false;
            m_cancellation.Dispose();
            m_cancellation = null;
            m_installStatus.Text = m_isReady ? "FFmpeg 9.0.2 已就绪" : "FFmpeg 尚未初始化或需要修复";
            RefreshControls();
            if (m_closeRequested) Close();
        }

        private void RefreshControls()
        {
            // 未确认依赖就绪前，仅显示初始化操作与日志，避免暴露暂时不可用的转码选项。
            Visibility transcodeVisibility = m_isReady ? Visibility.Visible : Visibility.Collapsed;
            m_title.Text = m_isReady ? "Pulse 视频转码与打包" : "初始化视频处理工具";
            m_options.Visibility = transcodeVisibility;
            m_filePanel.Visibility = transcodeVisibility;
            m_start.Visibility = transcodeVisibility;
            m_batchProgress.Visibility = transcodeVisibility;
            m_initialize.Visibility = m_isReady ? Visibility.Collapsed : Visibility.Visible;
            m_initializationHint.Visibility = m_isReady ? Visibility.Collapsed : Visibility.Visible;
            m_root.RowDefinitions[1].Height = m_isReady
                ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            m_options.IsEnabled = !m_isBusy;
            m_files.IsEnabled = !m_isBusy;
            m_filePanel.IsEnabled = !m_isBusy;
            m_initialize.IsEnabled = !m_isBusy && !m_isReady;
            m_start.IsEnabled = !m_isBusy && m_isReady;
            m_cancel.IsEnabled = m_isBusy;
        }

        private void OnCancel(object sender, RoutedEventArgs eventArgs)
        {
            if (m_cancellation == null) return;
            m_log.Write("[取消] 正在终止" + m_operation + "及其子进程……");
            m_cancellation.Cancel();
            m_cancel.IsEnabled = false;
        }

        private void OnLogTick(object sender, EventArgs eventArgs) { FlushLogs(); }

        private void FlushLogs()
        {
            string line;
            var lines = new System.Text.StringBuilder();
            while (m_log.TryRead(out line))
            {
                string display = line;
                int jsonStart = line.IndexOf('{');
                if (jsonStart >= 0)
                {
                    try
                    {
                        var record = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                            line.Substring(jsonStart));
                        display = line.Substring(0, jsonStart) + "[" + record["stage"] + "] " + record["message"];
                        if (record.ContainsKey("percent") && record["percent"] != null)
                            m_stageProgress.Value = Convert.ToDouble(record["percent"], CultureInfo.InvariantCulture);
                    }
                    catch (Exception) { /* 非协议日志保留原文，避免丢失原生错误信息。 */ }
                }
                lines.AppendLine(display);
            }
            if (lines.Length > 0)
            {
                m_logs.AppendText(lines.ToString());
                m_logs.ScrollToEnd();
            }
            m_batchProgress.Value = Interlocked.CompareExchange(ref m_completedCount, 0, 0);
        }

        private void OnCopyLog(object sender, RoutedEventArgs eventArgs) { CopyLogs(); }

        private void CopyLogs()
        {
            try
            {
                Clipboard.SetText(m_log.GetText());
                m_status.Text = "已复制全部日志。";
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "无法访问剪贴板：" + exception.Message + "\r\n日志文件：" + m_log.FilePath,
                    "复制失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
        {
            if (eventArgs.Key != Key.C
                || Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return;
            CopyLogs();
            eventArgs.Handled = true;
        }

        private void OnOpenLogs(object sender, RoutedEventArgs eventArgs)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.GetDirectoryName(m_log.FilePath)) { UseShellExecute = true });
            }
            catch (Exception exception) { ShowError("打开日志目录失败。", exception); }
        }

        private void ShowError(string message, Exception exception)
        {
            m_log.Write("[错误] " + message + "\r\n" + exception);
            FlushLogs();
            m_status.Text = message;
            var dialog = new Window
            {
                Title = "视频工具错误", Owner = this, Width = 780, Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var panel = new DockPanel { Margin = new Thickness(16) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(MakeButton("复制全部日志（Ctrl+Shift+C）", OnCopyLog));
            buttons.Children.Add(MakeButton("关闭", delegate { dialog.Close(); }));
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);
            string logText = m_log.GetText();
            if (logText.Length > 14000) logText = logText.Substring(logText.Length - 14000);
            panel.Children.Add(new TextBox
            {
                Text = message + "\r\n" + exception.Message + "\r\n\r\n日志文件：" + m_log.FilePath
                    + "\r\n\r\n最近日志：\r\n" + logText,
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });
            dialog.Content = panel;
            dialog.PreviewKeyDown += OnPreviewKeyDown;
            dialog.ShowDialog();
        }

        private void OnClosing(object sender, CancelEventArgs eventArgs)
        {
            if (m_isBusy)
            {
                eventArgs.Cancel = true;
                if (m_closeRequested) return;
                if (MessageBox.Show(this, "任务正在运行。是否取消任务并关闭工具？", "关闭工具",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                m_closeRequested = true;
                OnCancel(sender, new RoutedEventArgs());
                return;
            }
            try { SaveSettings(); }
            catch (Exception exception)
            {
                m_closeRequested = false;
                eventArgs.Cancel = true;
                ShowError("保存设置失败，请检查用户目录是否可写。", exception);
                return;
            }
            m_logTimer.Stop();
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
        {
            eventArgs.Handled = true;
            ShowError("工具发生未处理错误，请复制日志。", eventArgs.Exception);
        }
    }
}
