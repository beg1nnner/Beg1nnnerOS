using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Beg1nnnerOS.Core;

namespace Beg1nnnerOS.Desktop;

/// <summary>Диалог крестика: закрыть, свернуть в трей или отменить (с запоминанием выбора). Оформлен в палитре панели.</summary>
public sealed class CloseDialog : Window
{
    public string Choice = "cancel";
    public bool Remember;

    // палитра панели (тема «Студия»)
    private const string Bg = "#111116", Line = "#262631", Fg = "#F4F4F7", Muted = "#B6B6C4", Accent = "#8B5CF6", AccentHover = "#9D74F8", Field = "#1B1B22", FieldHover = "#24242D";

    // кнопки и переключатель — шаблоны в XAML, чтобы были наведение, нажатие и рамка фокуса
    private static readonly string ButtonXaml = $@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
  <Setter Property='Foreground' Value='{Fg}'/>
  <Setter Property='FontSize' Value='13'/>
  <Setter Property='FontWeight' Value='SemiBold'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='FocusVisualStyle' Value='{{x:Null}}'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Grid>
          <Border x:Name='Ring' CornerRadius='12' Margin='-3' BorderThickness='2' BorderBrush='Transparent'/>
          <Border x:Name='Bd' CornerRadius='10' Padding='16,9' Background='{{TemplateBinding Background}}'
                  BorderBrush='{{TemplateBinding BorderBrush}}' BorderThickness='{{TemplateBinding BorderThickness}}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.88'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.72'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Ring' Property='BorderBrush' Value='#C4B5FD'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

    private static readonly string ToggleXaml = $@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='CheckBox'>
  <Setter Property='Foreground' Value='{Fg}'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='FocusVisualStyle' Value='{{x:Null}}'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='CheckBox'>
        <StackPanel Orientation='Horizontal' Background='Transparent'>
          <Grid Width='40' Height='24' VerticalAlignment='Center'>
            <Border x:Name='Ring' CornerRadius='14' Margin='-3' BorderThickness='2' BorderBrush='Transparent'/>
            <Border x:Name='Track' CornerRadius='12' Background='#2E2E38'/>
            <Ellipse x:Name='Thumb' Width='18' Height='18' Fill='White' HorizontalAlignment='Left' Margin='3,0,0,0'/>
          </Grid>
          <ContentPresenter Margin='12,0,0,0' VerticalAlignment='Center'/>
        </StackPanel>
        <ControlTemplate.Triggers>
          <Trigger Property='IsChecked' Value='True'>
            <Setter TargetName='Track' Property='Background' Value='{Accent}'/>
            <Setter TargetName='Thumb' Property='Margin' Value='19,0,0,0'/>
          </Trigger>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Track' Property='Opacity' Value='0.9'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Ring' Property='BorderBrush' Value='#C4B5FD'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

    private static Brush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);

    public CloseDialog(bool trayAvailable)
    {
        Title = "Beg1nnnerOS";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;

        var btnStyle = (Style)XamlReader.Parse(ButtonXaml);
        var toggleStyle = (Style)XamlReader.Parse(ToggleXaml);

        var card = new Border
        {
            Background = B(Bg), BorderBrush = B(Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18),
            Margin = new Thickness(24), MaxWidth = 520,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 40, ShadowDepth = 10, Opacity = 0.6 },
        };
        var root = new StackPanel();
        card.Child = root;

        // своя полоса заголовка: тёмная, за неё можно перетаскивать, крестик = «Отмена»
        var head = new Grid { Margin = new Thickness(20, 14, 10, 0), Background = Brushes.Transparent };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(6), Background = B("#2A8B5CF6"), BorderBrush = B(Accent), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "B1", FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 9, FontWeight = FontWeights.SemiBold,
                Foreground = B("#C4B5FD"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        brand.Children.Add(new TextBlock { Text = "Beg1nnnerOS", Foreground = B(Muted), FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(brand);
        var x = new Button
        {
            Style = btnStyle, Background = Brushes.Transparent, Foreground = B(Muted), ToolTip = L.T("Отмена"),
            Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10"), Stroke = B(Muted), StrokeThickness = 1.4, Width = 10, Height = 10 },
        };
        System.Windows.Automation.AutomationProperties.SetName(x, L.T("Отмена"));
        x.Click += (s, e) => { Choice = "cancel"; DialogResult = false; };
        Grid.SetColumn(x, 1);
        head.Children.Add(x);
        head.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        root.Children.Add(head);

        var body = new StackPanel { Margin = new Thickness(24, 10, 24, 22) };
        body.Children.Add(new TextBlock
        {
            Text = trayAvailable ? L.T("Закрыть Beg1nnnerOS или свернуть в трей?") : L.T("Закрыть Beg1nnnerOS?"),
            FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = B(Fg), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = L.T("В трее музыка, чат, таймеры и источники OBS продолжают работать. При закрытии всё останавливается."),
            TextWrapping = TextWrapping.Wrap, Foreground = B(Muted), FontSize = 13, LineHeight = 19, Margin = new Thickness(0, 0, 0, 18),
        });
        var remember = new CheckBox
        {
            Style = toggleStyle,
            Content = new TextBlock { Text = L.T("Запомнить выбор (можно изменить в настройках)"), Foreground = B(Fg), FontSize = 13, TextWrapping = TextWrapping.Wrap },
            Margin = new Thickness(0, 0, 0, 22),
        };
        body.Children.Add(remember);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };

        Button Make(string text, string choice, bool primary)
        {
            var b = new Button
            {
                Style = btnStyle, Content = text, Margin = new Thickness(8, 0, 0, 0), MinWidth = 110,
                Background = primary ? B(Accent) : B(Field),
                BorderBrush = primary ? Brushes.Transparent : B(Line), BorderThickness = new Thickness(primary ? 0 : 1),
                Foreground = primary ? Brushes.White : B(Fg), IsDefault = primary, IsCancel = choice == "cancel",
            };
            b.Click += (s, e) =>
            {
                Choice = choice;
                Remember = remember.IsChecked == true && choice != "cancel";
                DialogResult = true;
            };
            return b;
        }

        buttons.Children.Add(Make(L.T("Отмена"), "cancel", false));
        buttons.Children.Add(Make(L.T("Закрыть программу"), "exit", !trayAvailable));
        if (trayAvailable) buttons.Children.Add(Make(L.T("Свернуть в трей"), "tray", true));
        body.Children.Add(buttons);
        root.Children.Add(body);
        Content = card;
        Loaded += (s, e) =>
        {
            // фокус на основном действии — Enter подтверждает, Esc отменяет
            foreach (var c in buttons.Children) if (c is Button b && b.IsDefault) { b.Focus(); break; }
        };
    }
}
