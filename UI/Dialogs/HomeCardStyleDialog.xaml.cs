using System.Windows;
using Vermilune.Core;

namespace Vermilune.UI.Dialogs;

public partial class HomeCardStyleDialog : Window
{
    public HomeCardStyle CardStyle { get; private set; }
    public bool DeleteRequested { get; private set; }
    private readonly Action<HomeCardStyle>? _preview;
    private bool _ready;

    public HomeCardStyleDialog(HomeCardStyle source, bool canDelete, Action<HomeCardStyle>? preview = null)
    {
        _preview = preview;
        InitializeComponent();
        CardStyle = new HomeCardStyle
        {
            Rotation = source.Rotation,
            Scale = source.Scale,
            FontScale = source.FontScale,
            Opacity = source.Opacity
        };
        RotationSlider.Value = Math.Clamp(CardStyle.Rotation, -180, 180);
        ScaleSlider.Value = Math.Clamp(CardStyle.Scale * 100, 50, 200);
        FontSlider.Value = Math.Clamp(CardStyle.FontScale * 100, 50, 200);
        OpacitySlider.Value = Math.Clamp(CardStyle.Opacity * 100, 20, 100);
        DeleteButton.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
        UpdateLabels();
        _ready = true;
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
    }

    private void ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        if (!_ready || _preview is null) return;
        _preview(BuildStyle());
    }

    private HomeCardStyle BuildStyle() => new()
    {
        Rotation = RotationSlider.Value,
        Scale = ScaleSlider.Value / 100.0,
        FontScale = FontSlider.Value / 100.0,
        Opacity = OpacitySlider.Value / 100.0
    };

    private void UpdateLabels()
    {
        if (RotationValue is null || ScaleValue is null || FontValue is null || OpacityValue is null) return;
        RotationValue.Text = $"{RotationSlider.Value:0}°";
        ScaleValue.Text = $"{ScaleSlider.Value:0}%";
        FontValue.Text = $"{FontSlider.Value:0}%";
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        RotationSlider.Value = 0;
        ScaleSlider.Value = 100;
        FontSlider.Value = 100;
        OpacitySlider.Value = 100;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CardStyle.Rotation = RotationSlider.Value;
        CardStyle.Scale = ScaleSlider.Value / 100.0;
        CardStyle.FontScale = FontSlider.Value / 100.0;
        CardStyle.Opacity = OpacitySlider.Value / 100.0;
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
