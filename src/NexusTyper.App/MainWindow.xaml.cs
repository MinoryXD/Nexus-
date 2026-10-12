using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NexusTyper.Core;

namespace NexusTyper.App;

public partial class MainWindow : Window
{
    private static readonly string TemplateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusTyper",
        "templates.json");
    private static readonly string ThemeFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusTyper",
        "theme.json");

    private readonly GlobalHotkey _hotkey = new();
    private readonly TypingSession _session = new(new WindowsInputSender());
    private readonly ObservableCollection<TyperTemplate> _templates = [];
    private readonly Dictionary<Slider, ScrollViewer> _textScrollViewers = [];
    private CancellationTokenSource? _cancellation;
    private TyperState _state = TyperState.Ready;
    private bool _updatingSpeed;
    private bool _syncingTextScroll;
    private bool _loadingTheme;
    private CancellationTokenSource? _statusHideCancellation;
    private Button? _selectedNavButton;
    private DateTime _lastStartAttempt = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureTextEditMenus();
        TemplatesListBox.ItemsSource = _templates;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        LoadTemplates();
        LoadThemePreferences();
        ShowPage(TyperPage, TyperNavButton);
        RefreshControls();
        UpdateMetrics();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            _hotkey.Register(this);
            _hotkey.StartPressed += StartHotkey_Pressed;
            _hotkey.StopPressed += StopHotkey_Pressed;
        }
        catch (Exception exception)
        {
            SetStatus(TyperState.Ready, $"Горячие клавиши недоступны: {exception.Message}");
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _cancellation?.Cancel();
        _statusHideCancellation?.Cancel();
        _hotkey.Dispose();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void ConfigureTextEditMenus()
    {
        TextBox[] textBoxes =
        [
            SpeedTextBox,
            HoldTimeTextBox,
            TextToTypeBox,
            TemplateNameTextBox,
            TemplateBodyTextBox,
            TemplateConfigTextBox,
            SpacePauseTextBox,
            JitterTextBox,
            DefaultSpeedTextBox
        ];

        foreach (var textBox in textBoxes)
        {
            textBox.ContextMenu = CreateTextEditMenu(textBox);
        }
    }

    private void MultilineTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        var slider = ReferenceEquals(textBox, TextToTypeBox) ? TextScrollSlider : TemplateScrollSlider;
        if (_textScrollViewers.ContainsKey(slider))
        {
            return;
        }

        textBox.ApplyTemplate();
        if (textBox.Template.FindName("PART_ContentHost", textBox) is not ScrollViewer viewer)
        {
            return;
        }

        _textScrollViewers.Add(slider, viewer);
        viewer.ScrollChanged += (_, _) => SyncTextScrollSlider(slider, viewer);
        slider.ValueChanged += (_, _) =>
        {
            if (!_syncingTextScroll && viewer.ScrollableHeight > 0)
            {
                viewer.ScrollToVerticalOffset(slider.Value);
            }
        };
        SyncTextScrollSlider(slider, viewer);
    }

    private void SyncTextScrollSlider(Slider slider, ScrollViewer viewer)
    {
        _syncingTextScroll = true;
        try
        {
            if (viewer.ScrollableHeight <= 0)
            {
                if (slider.Maximum != 1)
                {
                    slider.Maximum = 1;
                    slider.Value = 0.5;
                }
            }
            else
            {
                slider.Maximum = viewer.ScrollableHeight;
                slider.Value = viewer.VerticalOffset;
            }
        }
        finally
        {
            _syncingTextScroll = false;
        }
    }

    private void TextScrollSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider slider || e.Delta == 0)
        {
            return;
        }

        var step = _textScrollViewers.TryGetValue(slider, out var viewer) && viewer.ScrollableHeight > 0
            ? Math.Max(1, viewer.ViewportHeight / 8)
            : 0.1;
        slider.Value = Math.Clamp(slider.Value - Math.Sign(e.Delta) * step, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }

    private void MultilineTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox textBox || e.Delta == 0 || textBox.LineCount < 2)
        {
            return;
        }

        var firstVisibleLine = textBox.GetFirstVisibleLineIndex();
        var linesPerNotch = SystemParameters.WheelScrollLines;
        var linesToMove = linesPerNotch < 0
            ? Math.Max(1, textBox.GetLastVisibleLineIndex() - firstVisibleLine)
            : linesPerNotch;
        if (linesToMove == 0)
        {
            return;
        }

        var notches = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta) / 120d));
        var direction = e.Delta > 0 ? -1 : 1;
        var targetLine = Math.Clamp(firstVisibleLine + direction * linesToMove * notches, 0, textBox.LineCount - 1);
        textBox.ScrollToLine(targetLine);
        e.Handled = true;
    }

    private ContextMenu CreateTextEditMenu(TextBox target)
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource(typeof(ContextMenu)),
            PlacementTarget = target
        };

        var cutItem = CreateTextEditMenuItem("Вырезать", "Ctrl+X", () =>
        {
            if (target.SelectionLength == 0) return;
            Clipboard.SetText(target.SelectedText);
            target.SelectedText = string.Empty;
        });
        var copyItem = CreateTextEditMenuItem("Копировать", "Ctrl+C", () =>
        {
            if (target.SelectionLength > 0) Clipboard.SetText(target.SelectedText);
        });
        var pasteItem = CreateTextEditMenuItem("Вставить", "Ctrl+V", () =>
        {
            if (Clipboard.ContainsText()) target.SelectedText = Clipboard.GetText();
        });
        menu.Items.Add(cutItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Opened += (_, _) =>
        {
            cutItem.IsEnabled = copyItem.IsEnabled = target.SelectionLength > 0;
            try
            {
                pasteItem.IsEnabled = Clipboard.ContainsText();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                pasteItem.IsEnabled = false;
            }
        };

        return menu;
    }

    private MenuItem CreateTextEditMenuItem(string label, string shortcut, Action action)
    {
        var item = new MenuItem { Header = label, InputGestureText = shortcut };
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (InvalidOperationException)
            {
                SetStatus(_state, "Не удалось обратиться к буферу обмена. Попробуйте ещё раз.");
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                SetStatus(_state, "Буфер обмена временно занят. Попробуйте ещё раз.");
            }
        };
        return item;
    }

    private void StartHotkey_Pressed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => StartHotkey_Pressed(sender, e));
            return;
        }
        _ = StartAsync();
    }

    private void StopHotkey_Pressed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => StopHotkey_Pressed(sender, e));
            return;
        }
        _cancellation?.Cancel();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F6)
        {
            e.Handled = true;
            _ = StartAsync();
        }
        else if (e.Key == Key.F7)
        {
            e.Handled = true;
            _cancellation?.Cancel();
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartAsync();

    private void StopButton_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void TyperNavButton_Click(object sender, RoutedEventArgs e) => ShowPage(TyperPage, TyperNavButton);

    private void AutoEnterNavButton_Click(object sender, RoutedEventArgs e) => ShowPage(AutoEnterPage, AutoEnterNavButton);

    private void TemplatesNavButton_Click(object sender, RoutedEventArgs e) => ShowPage(TemplatesPage, TemplatesNavButton);

    private void TemplateEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TyperTemplate template })
        {
            TemplatesListBox.SelectedItem = template;
            ShowPage(TemplatesPage, TemplatesNavButton);
            TemplateEditorPanel.Visibility = Visibility.Collapsed;
            QuickTemplateEditorPanel.Visibility = Visibility.Visible;
            QuickTemplateEditor.Text = template.Text;
            QuickTemplateEditor.Focus();
            QuickTemplateEditor.CaretIndex = QuickTemplateEditor.Text.Length;
        }
    }

    private void QuickTemplateEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TemplatesListBox.SelectedItem is TyperTemplate template && QuickTemplateEditor is not null)
        {
            TemplateBodyTextBox.Text = QuickTemplateEditor.Text;
        }
    }

    private void QuickTemplateSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (TemplatesListBox.SelectedItem is not TyperTemplate template)
        {
            return;
        }

        var index = _templates.IndexOf(template);
        if (index < 0 || string.IsNullOrWhiteSpace(QuickTemplateEditor.Text))
        {
            return;
        }

        var updated = template with { Text = QuickTemplateEditor.Text };
        _templates[index] = updated;
        TemplatesListBox.SelectedItem = updated;
        TemplateBodyTextBox.Text = updated.Text;
        PersistTemplates();
        UpdateTemplateUi();
        SetStatus(_state, $"Шаблон «{updated.Name}» сохранён.");
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, SettingsNavButton);

    private void AboutNavButton_Click(object sender, RoutedEventArgs e) => ShowPage(AboutPage, AboutNavButton);

    private async Task StartAsync()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastStartAttempt).TotalMilliseconds < 350)
        {
            return;
        }
        _lastStartAttempt = now;

        if (TyperUiRules.CanStop(_state))
        {
            _cancellation?.Cancel();
            return;
        }

        ShowPage(TyperPage, TyperNavButton);

        if (!int.TryParse(SpeedTextBox.Text, out var speed))
        {
            SetStatus(TyperState.Ready, "Скорость должна быть целым числом.");
            ShowPage(TyperPage, TyperNavButton);
            return;
        }

        double? holdMilliseconds = null;
        if (AutoHoldCheckBox.IsChecked == true)
        {
            if (!double.TryParse(HoldTimeTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hold))
            {
                SetStatus(TyperState.Ready, "Укажите время удержания от 0,1 до 1000 мс.");
                ShowPage(TyperPage, TyperNavButton);
                return;
            }

            holdMilliseconds = hold;
        }

        if (!double.TryParse(SpacePauseTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var spacePause) ||
            !int.TryParse(JitterTextBox.Text, out var jitter))
        {
            SetStatus(TyperState.Ready, "Пауза и разброс должны быть числами.");
            ShowPage(TyperPage, TyperNavButton);
            return;
        }

        if (!TypingOptions.TryCreate(TextToTypeBox.Text, speed, out var options, out var error,
                holdMilliseconds, AppendEnterCheckBox.IsChecked == true, spacePause, jitter))
        {
            SetStatus(TyperState.Ready, error!);
            ShowPage(TyperPage, TyperNavButton);
            return;
        }

        _cancellation = new CancellationTokenSource();
        SetStatus(TyperState.Countdown, "Старт через 3 сек. Переключитесь в нужное окно.");
        await _session.StartAsync(options!, SetStatus, _cancellation.Token);
        _cancellation.Dispose();
        _cancellation = null;
        RefreshControls();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSpeed || SpeedTextBox is null)
        {
            return;
        }

        _updatingSpeed = true;
        SpeedTextBox.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
        _updatingSpeed = false;
        UpdateMetrics();
        RefreshControls();
    }

    private void SpeedTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingSpeed || SpeedSlider is null || !int.TryParse(SpeedTextBox.Text, out var speed))
        {
            RefreshControls();
            return;
        }

        _updatingSpeed = true;
        SpeedSlider.Value = Math.Clamp(speed, 1, 300);
        _updatingSpeed = false;
        UpdateMetrics();
        RefreshControls();
    }

    private void HoldTimeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateMetrics();
        RefreshControls();
    }

    private void AdvancedConfigTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateMetrics();
        RefreshControls();
    }

    private void AutoHoldCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (HoldTimeTextBox is not null)
        {
            HoldTimeTextBox.IsEnabled = AutoHoldCheckBox.IsChecked == true;
        }

        UpdateMetrics();
        RefreshControls();
    }

    private void TextToTypeBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshControls();

    private void DefaultSpeedTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SpeedTextBox is not null && int.TryParse(DefaultSpeedTextBox.Text, out var speed) && speed is >= 1 and <= 300)
        {
            SpeedTextBox.Text = speed.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void AccentThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingTheme && AccentThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string accent)
        {
            ApplyTheme(accent, GetSelectedThemeTag(MenuThemeComboBox, "dark"), true);
        }
    }

    private void MenuThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingTheme && MenuThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string menu)
        {
            ApplyTheme(GetSelectedThemeTag(AccentThemeComboBox, "red"), menu, true);
        }
    }

    private void ThemePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTheme || ThemePresetComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string preset)
        {
            return;
        }

        var (accent, menu) = preset.ToLowerInvariant() switch
        {
            "ocean" => ("sky", "navy"),
            "cosmos" => ("violet", "purple"),
            "emerald" => ("green", "forest"),
            "sunset" => ("orange", "brown"),
            "pink-graphite" => ("pink", "graphite"),
            "gold-luxe" => ("gold", "black"),
            "ice" => ("teal", "black"),
            _ => ("red", "black"),
        };

        _loadingTheme = true;
        try
        {
            SelectThemeItem(AccentThemeComboBox, accent, "red");
            SelectThemeItem(MenuThemeComboBox, menu, "dark");
        }
        finally
        {
            _loadingTheme = false;
        }

        ApplyTheme(accent, menu, true);
    }

    private static string GetSelectedThemeTag(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : fallback;
    }

    private void LoadThemePreferences()
    {
        var preferences = new ThemePreferences("red", "dark");
        try
        {
            if (File.Exists(ThemeFile))
            {
                preferences = JsonSerializer.Deserialize<ThemePreferences>(File.ReadAllText(ThemeFile)) ?? preferences;
            }
        }
        catch
        {
            // Defaults keep the app usable when the preferences file is damaged.
        }

        _loadingTheme = true;
        try
        {
            SelectThemeItem(AccentThemeComboBox, preferences.Accent, "red");
            SelectThemeItem(MenuThemeComboBox, preferences.Menu, "dark");
        }
        finally
        {
            _loadingTheme = false;
        }

        ApplyTheme(GetSelectedThemeTag(AccentThemeComboBox, "red"), GetSelectedThemeTag(MenuThemeComboBox, "dark"), false);
    }

    private static void SelectThemeItem(ComboBox comboBox, string tag, string fallback)
    {
        var item = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(candidate =>
            string.Equals(candidate.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(candidate =>
                string.Equals(candidate.Tag as string, fallback, StringComparison.OrdinalIgnoreCase));
        comboBox.SelectedItem = item;
    }

    private void ApplyTheme(string accentName, string menuName, bool persist)
    {
        var accent = accentName.ToLowerInvariant() switch
        {
            "blue" => Color.FromRgb(47, 128, 237),
            "violet" => Color.FromRgb(145, 92, 246),
            "green" => Color.FromRgb(39, 174, 96),
            "sky" => Color.FromRgb(56, 189, 248),
            "teal" => Color.FromRgb(20, 184, 166),
            "orange" => Color.FromRgb(249, 115, 22),
            "pink" => Color.FromRgb(236, 72, 153),
            "gold" => Color.FromRgb(234, 179, 8),
            "lime" => Color.FromRgb(132, 204, 22),
            _ => Color.FromRgb(229, 42, 54),
        };
        var menuPalette = menuName.ToLowerInvariant() switch
        {
            "black" => (Color.FromRgb(5, 6, 8), Color.FromRgb(10, 11, 13)),
            "graphite" => (Color.FromRgb(27, 29, 33), Color.FromRgb(38, 40, 45)),
            "navy" => (Color.FromRgb(8, 18, 35), Color.FromRgb(17, 31, 53)),
            "burgundy" => (Color.FromRgb(31, 8, 14), Color.FromRgb(49, 15, 23)),
            "forest" => (Color.FromRgb(7, 25, 18), Color.FromRgb(14, 42, 29)),
            "purple" => (Color.FromRgb(20, 11, 35), Color.FromRgb(34, 20, 56)),
            "teal" => (Color.FromRgb(5, 27, 29), Color.FromRgb(12, 48, 49)),
            "brown" => (Color.FromRgb(29, 18, 12), Color.FromRgb(47, 29, 19)),
            _ => (Color.FromRgb(12, 13, 15), Color.FromRgb(21, 22, 25)),
        };
        var menu = menuPalette.Item1;
        var surface = menuPalette.Item2;
        var hover = Blend(menu, accent, 0.18);

        SetThemeBrush("AccentBrush", accent);
        SetThemeBrush("AccentStrongBrush", Lighten(accent, 0.12));
        SetThemeBrush("AccentHoverBrush", Lighten(accent, 0.3));
        SetThemeBrush("AccentDarkBrush", Darken(accent, 0.18));
        SetThemeBrush("AccentSoftBrush", Blend(menu, accent, 0.24));
        SetThemeBrush("MenuBackgroundBrush", menu);
        SetThemeBrush("MenuSurfaceBrush", surface);
        SetThemeBrush("MenuHoverBrush", hover);
        SetThemeBrush("MenuBorderBrush", Blend(menu, Colors.White, 0.15));
        Application.Current.Resources["AccentGlowColor"] = accent;

        if (_selectedNavButton is not null)
        {
            _selectedNavButton.Background = new SolidColorBrush(Color.FromArgb(0xAA, accent.R, accent.G, accent.B));
            _selectedNavButton.Foreground = Brushes.White;
        }

        if (persist)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!);
                File.WriteAllText(ThemeFile, JsonSerializer.Serialize(new ThemePreferences(accentName, menuName)));
                SetStatus(_state, "Тема применена.");
                HideStatusBannerLater();
            }
            catch (Exception exception)
            {
                SetStatus(_state, $"Не удалось сохранить тему: {exception.Message}");
            }
        }
    }

    private static void SetThemeBrush(string key, Color color)
    {
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private static Color Lighten(Color color, double amount) => Blend(color, Colors.White, amount);

    private static Color Darken(Color color, double amount) => Blend(color, Colors.Black, amount);

    private void SaveTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        var name = TemplateNameTextBox.Text.Trim();
        if (name.Length == 0 || string.IsNullOrWhiteSpace(TemplateBodyTextBox.Text))
        {
            SetStatus(_state, "Введите название и текст шаблона.");
            return;
        }

        var existing = _templates.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _templates.Remove(existing);
        }

        _templates.Insert(0, new TyperTemplate(name, TemplateBodyTextBox.Text));
        TemplatesListBox.SelectedItem = _templates[0];
        TemplateConfigTextBox.Text = SerializeTemplateConfig(_templates[0]);
        PersistTemplates();
        UpdateTemplateUi();
        SetStatus(_state, $"Шаблон «{name}» сохранён.");
    }

    private void CopyTemplateConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (TemplatesListBox.SelectedItem is not TyperTemplate template)
        {
            SetStatus(_state, "Сначала выберите шаблон для экспорта.");
            return;
        }

        try
        {
            var config = SerializeTemplateConfig(template);
            TemplateConfigTextBox.Text = config;
            Clipboard.SetText(config);
            SetStatus(_state, "Конфиг скопирован в буфер обмена.");
        }
        catch (Exception exception)
        {
            SetStatus(_state, $"Не удалось скопировать конфиг: {exception.Message}");
        }
    }

    private void PasteTemplateConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TemplateConfigTextBox.Text))
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    TemplateConfigTextBox.Text = Clipboard.GetText();
                }
            }
            catch
            {
                SetStatus(_state, "Не удалось прочитать буфер обмена.");
            }
        }

        if (!TryImportTemplateConfig(TemplateConfigTextBox.Text, out var message))
        {
            SetStatus(_state, message);
        }
    }

    private static string SerializeTemplateConfig(TyperTemplate template) =>
        JsonSerializer.Serialize(new TemplateConfig(template.Name, template.Text), new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private bool TryImportTemplateConfig(string json, out string message)
    {
        try
        {
            var config = JsonSerializer.Deserialize<TemplateConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (config is null || string.IsNullOrWhiteSpace(config.Name) || string.IsNullOrWhiteSpace(config.Text))
            {
                message = "В конфиге должны быть заполнены поля name и text.";
                return false;
            }

            var name = config.Name.Trim();
            var existing = _templates.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _templates.Remove(existing);
            }

            var imported = new TyperTemplate(name, config.Text);
            _templates.Insert(0, imported);
            TemplatesListBox.SelectedItem = imported;
            TemplateConfigTextBox.Text = SerializeTemplateConfig(imported);
            PersistTemplates();
            UpdateTemplateUi();
            SetStatus(_state, $"Шаблон «{name}» импортирован.");
            message = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            message = "Не удалось прочитать JSON. Проверьте скобки, кавычки и поля name/text.";
            return false;
        }
        catch (Exception exception)
        {
            message = $"Не удалось импортировать конфиг: {exception.Message}";
            return false;
        }
    }

    private void UseTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (TemplatesListBox.SelectedItem is not TyperTemplate template)
        {
            SetStatus(_state, "Сначала выберите шаблон.");
            return;
        }

        TextToTypeBox.Text = template.Text;
        ShowPage(TyperPage, TyperNavButton);
        SetStatus(TyperState.Ready, $"Шаблон «{template.Name}» загружен в тайпер.");
    }

    private void DeleteTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (TemplatesListBox.SelectedItem is TyperTemplate template)
        {
            _templates.Remove(template);
            PersistTemplates();
            UpdateTemplateUi();
            SetStatus(_state, $"Шаблон «{template.Name}» удалён.");
        }
    }

    private void ClearTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        TemplatesListBox.SelectedItem = null;
        TemplateNameTextBox.Clear();
        TemplateBodyTextBox.Clear();
        TemplateConfigTextBox.Clear();
        TemplateNameTextBox.Focus();
    }

    private void TemplatesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplatesListBox.SelectedItem is TyperTemplate template)
        {
            TemplateNameTextBox.Text = template.Name;
            TemplateBodyTextBox.Text = template.Text;
            TemplateConfigTextBox.Text = SerializeTemplateConfig(template);
            StatusTextBlock.Text = $"Выбран шаблон «{template.Name}».";
            if (QuickTemplateEditorPanel.Visibility == Visibility.Visible)
            {
                QuickTemplateEditor.Text = template.Text;
            }
        }
    }

    private void UpdateTemplateUi()
    {
        if (TemplateCountTextBlock is null || EmptyTemplatesPanel is null)
        {
            return;
        }

        TemplateCountTextBlock.Text = $"СОХРАНЁННЫЕ ШАБЛОНЫ · {_templates.Count}";
        EmptyTemplatesPanel.Visibility = _templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(TyperState state, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(state, message));
            return;
        }

        _state = state;
        StatusBanner.Visibility = Visibility.Visible;
        StatusTextBlock.Text = message;
        TopStatusText.Text = state switch
        {
            TyperState.Countdown => "ОТСЧЁТ",
            TyperState.Typing => "ПЕЧАТЬ",
            TyperState.Error => "ОШИБКА",
            _ => "НЕ ЗАПУЩЕН",
        };
        StatusDot.Fill = state switch
        {
            TyperState.Typing => Brushes.LimeGreen,
            TyperState.Countdown => Brushes.Orange,
            TyperState.Error => Brushes.OrangeRed,
            _ => (Application.Current.Resources["AccentStrongBrush"] as SolidColorBrush)?.Clone()
                ?? new SolidColorBrush(Color.FromRgb(255, 39, 51)),
        };
        RefreshControls();
    }

    private async void HideStatusBannerLater()
    {
        _statusHideCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _statusHideCancellation = cancellation;

        try
        {
            await Task.Delay(1800, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                StatusBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)));
                await Task.Delay(230, cancellation.Token);
                StatusBanner.Visibility = Visibility.Collapsed;
                StatusBanner.Opacity = 1;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RefreshControls()
    {
        if (TextToTypeBox is null || SpeedTextBox is null || StartButton is null || StopButton is null)
        {
            return;
        }

        var hasSpeed = int.TryParse(SpeedTextBox.Text, out var speed);
        var hold = 0d;
        var holdEnabled = AutoHoldCheckBox?.IsChecked == true;
        var hasHoldTime = !holdEnabled ||
                          double.TryParse(HoldTimeTextBox?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out hold);
        var hasSpacePause = double.TryParse(SpacePauseTextBox?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var spacePause);
        var hasJitter = int.TryParse(JitterTextBox?.Text, out var jitter);
        var hasValidInput = hasSpeed && hasHoldTime && hasSpacePause && hasJitter &&
                            TypingOptions.TryCreate(TextToTypeBox.Text, speed, out _, out _,
                                holdEnabled ? hold : null,
                                AppendEnterCheckBox?.IsChecked == true, spacePause, jitter);
        var active = TyperUiRules.CanStop(_state);
        StartButton.IsEnabled = !active && hasValidInput;
        StopButton.IsEnabled = active;
        TextToTypeBox.IsEnabled = !active;
        SpeedSlider.IsEnabled = !active;
        SpeedTextBox.IsEnabled = !active;
        if (AutoHoldCheckBox is not null)
        {
            AutoHoldCheckBox.IsEnabled = !active;
            if (HoldTimeTextBox is not null)
            {
                HoldTimeTextBox.IsEnabled = !active && AutoHoldCheckBox.IsChecked == true;
            }
        }
    }

    private void UpdateMetrics()
    {
        if (MetricsTextBlock is null || !int.TryParse(SpeedTextBox?.Text, out var speed))
        {
            return;
        }

        var cps = speed / 12d;
        var interval = 12000d / speed;
        var holdText = AutoHoldCheckBox?.IsChecked == true &&
                       double.TryParse(HoldTimeTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hold)
            ? $"{hold.ToString("F1", CultureInfo.GetCultureInfo("ru-RU"))} мс удержание"
            : "без удержания";
        var gap = Math.Max(0, interval - (AutoHoldCheckBox?.IsChecked == true &&
            double.TryParse(HoldTimeTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var holdValue) ? holdValue : 0));
        var spacePause = double.TryParse(SpacePauseTextBox?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pause) ? pause : 0;
        var jitter = int.TryParse(JitterTextBox?.Text, out var jitterValue) ? jitterValue : 0;
        MetricsTextBlock.Text = $"{cps.ToString("F1", CultureInfo.GetCultureInfo("ru-RU"))} симв./с   ·   {interval.ToString("F1", CultureInfo.GetCultureInfo("ru-RU"))} мс интервал   ·   {holdText}   ·   {gap.ToString("F1", CultureInfo.GetCultureInfo("ru-RU"))} мс пауза   ·   пробел +{spacePause.ToString("F0", CultureInfo.InvariantCulture)} мс   ·   разброс {jitter}%";
    }

    private void ShowPage(FrameworkElement page, Button selectedButton)
    {
        TyperPage.Visibility = Visibility.Collapsed;
        AutoEnterPage.Visibility = Visibility.Collapsed;
        TemplatesPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        page.Opacity = 0;
        var slide = new TranslateTransform(10, 0);
        page.RenderTransform = slide;

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = easing,
        });
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = easing,
        });

        var accent = (Application.Current.Resources["AccentDarkBrush"] as SolidColorBrush)?.Color
                     ?? Color.FromRgb(160, 12, 22);
        var selectedBackground = Color.FromArgb(0xAA, accent.R, accent.G, accent.B);
        var normalForeground = Color.FromRgb(197, 200, 205);
        var selectedForeground = Color.FromRgb(255, 255, 255);
        foreach (var button in new[] { TyperNavButton, AutoEnterNavButton, TemplatesNavButton, SettingsNavButton, AboutNavButton })
        {
            var wasSelected = button == _selectedNavButton;
            var willBeSelected = button == selectedButton;
            var background = new SolidColorBrush(wasSelected ? selectedBackground : Colors.Transparent);
            button.Background = background;
            background.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(
                wasSelected ? selectedBackground : Colors.Transparent,
                willBeSelected ? selectedBackground : Colors.Transparent,
                TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });

            var foreground = new SolidColorBrush(wasSelected ? selectedForeground : normalForeground);
            button.Foreground = foreground;
            foreground.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(
                wasSelected ? selectedForeground : normalForeground,
                willBeSelected ? selectedForeground : normalForeground,
                TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }

        _selectedNavButton = selectedButton;
    }

    private void LoadTemplates()
    {
        try
        {
            if (!File.Exists(TemplateFile))
            {
                return;
            }

            var templates = JsonSerializer.Deserialize<List<TyperTemplate>>(File.ReadAllText(TemplateFile));
            if (templates is not null)
            {
                foreach (var template in templates)
                {
                    _templates.Add(template);
                }
            }

            UpdateTemplateUi();
        }
        catch (Exception exception)
        {
            SetStatus(TyperState.Ready, $"Не удалось загрузить шаблоны: {exception.Message}");
        }
    }

    private void PersistTemplates()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TemplateFile)!);
            File.WriteAllText(TemplateFile, JsonSerializer.Serialize(_templates));
        }
        catch (Exception exception)
        {
            SetStatus(TyperState.Ready, $"Не удалось сохранить шаблоны: {exception.Message}");
        }
    }

    private void FileDropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            FileDropZone.BorderBrush = (Brush)FindResource("AccentStrongBrush");
            FileDropZone.Background = new SolidColorBrush(Color.FromArgb(0x28, 229, 42, 54));
            FileDropZoneText.Foreground = (Brush)FindResource("AccentStrongBrush");
            FileDropZoneIcon.Stroke = (Brush)FindResource("AccentStrongBrush");
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void FileDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FileDropZone_DragLeave(object sender, DragEventArgs e)
    {
        ResetDropZoneVisual();
        e.Handled = true;
    }

    private void FileDropZone_MouseEnter(object sender, MouseEventArgs e)
    {
        FileDropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x48, 0x50));
        FileDropZone.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1B));
        FileDropZoneText.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9D, 0xA5));
        FileDropZoneIcon.Stroke = new SolidColorBrush(Color.FromRgb(0x9A, 0x9D, 0xA5));
    }

    private void FileDropZone_MouseLeave(object sender, MouseEventArgs e)
    {
        ResetDropZoneVisual();
    }

    private void ResetDropZoneVisual()
    {
        FileDropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2F, 0x34));
        FileDropZone.Background = new SolidColorBrush(Color.FromRgb(0x11, 0x12, 0x15));
        FileDropZoneText.Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x6F, 0x76));
        FileDropZoneIcon.Stroke = new SolidColorBrush(Color.FromRgb(0x6C, 0x6F, 0x76));
    }

    private void FileDropZone_Drop(object sender, DragEventArgs e)
    {
        ResetDropZoneVisual();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            LoadTextFromFile(files[0]);
        }
        e.Handled = true;
    }

    private void TextToTypeBox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            LoadTextFromFile(files[0]);
            e.Handled = true;
        }
    }

    private void FileDropZone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*",
            Title = "Выберите файл с текстом"
        };

        if (dialog.ShowDialog() == true)
        {
            LoadTextFromFile(dialog.FileName);
        }
    }

    private void LoadTextFromFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            TextToTypeBox.Text = text;
            var fileName = Path.GetFileName(path);
            FileDropZoneText.Text = $"Загружен: {fileName} ({text.Length} симв.)";
            FileDropZoneText.Foreground = (Brush)FindResource("AccentStrongBrush");
            FileDropZoneIcon.Stroke = (Brush)FindResource("AccentStrongBrush");
            SetStatus(_state, $"Текст из «{fileName}» успешно загружен ({text.Length} симв.).");
        }
        catch (Exception ex)
        {
            SetStatus(_state, $"Ошибка чтения файла: {ex.Message}");
        }
    }

    private void AuthorTelegramLink_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://t.me/MinoryXD",
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore if default browser cannot open
        }
    }

    private sealed record TyperTemplate(string Name, string Text)
    {
        public override string ToString() => Name;
    }

    private sealed record TemplateConfig(string Name, string Text);

    private sealed record ThemePreferences(string Accent, string Menu);

}
