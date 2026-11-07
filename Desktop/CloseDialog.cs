using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Beg1nnnerOS.Desktop;

/// <summary>Диалог крестика: закрыть, свернуть в трей или отменить (с запоминанием выбора).</summary>
public sealed class CloseDialog : Window
{
    public string Choice = "cancel";
    public bool Remember;

    public CloseDialog(bool trayAvailable)
    {
        Title = "Beg1nnnerOS";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1F));
        var fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEF));
        var muted = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xAB));

        var root = new StackPanel { Margin = new Thickness(22), MaxWidth = 460 };
        root.Children.Add(new TextBlock
        {
            Text = trayAvailable ? "Закрыть Beg1nnnerOS или свернуть в трей?" : "Закрыть Beg1nnnerOS?",
            FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 0, 0, 8),
        });
        root.Children.Add(new TextBlock
        {
            Text = "В трее музыка, чат, таймеры и источники OBS продолжают работать. При закрытии всё останавливается.",
            TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 0, 0, 14),
        });
        var remember = new CheckBox { Content = "Запомнить выбор (можно изменить в настройках)", Foreground = fg, Margin = new Thickness(0, 0, 0, 16) };
        root.Children.Add(remember);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        Button Make(string text, string choice, bool primary)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), MinWidth = 120,
                Background = primary ? new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF)) : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x37)),
                Foreground = Brushes.White, BorderThickness = new Thickness(0),
            };
            b.Click += (s, e) =>
            {
                Choice = choice;
                Remember = remember.IsChecked == true && choice != "cancel";
                DialogResult = true;
            };
            return b;
        }

        if (trayAvailable) buttons.Children.Add(Make("Свернуть в трей", "tray", true));
        buttons.Children.Add(Make("Закрыть программу", "exit", !trayAvailable));
        buttons.Children.Add(Make("Отмена", "cancel", false));
        root.Children.Add(buttons);
        Content = root;
    }
}
