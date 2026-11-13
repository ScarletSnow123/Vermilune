using System.Windows;
using System.Windows.Controls;
using Vermilune.Core;

namespace Vermilune.UI.Dialogs;

public partial class HomeCustomizeDialog : Window
{
    private readonly List<(string Key, CheckBox Box)> _items = new();
    private readonly Action<string>? _styleAction;
    public Dictionary<string, bool> CardVisibility { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HomeCustomizeDialog(AppSettings settings, Action<string>? styleAction = null)
    {
        InitializeComponent();
        _styleAction = styleAction;
        // 角色外观和当前实例是固定卡片，不参与这里的列表。
        Add("skin3d", "3D 皮肤", settings.ShowHome3D);
        Add("quick", "快捷入口", settings.ShowHomeQuick);
        Add("recent", "最近游玩", settings.ShowHomeRecent);
        Add("log", "实时日志", settings.ShowHomeLog);
        Add("featured", "自定义卡片", settings.ShowHomeFeatured);
        foreach (var widget in settings.HomeWidgets)
        {
            var type = widget.Type switch
            {
                "clock" => "时钟",
                "image" => "图片",
                _ => "MC 新闻"
            };
            var suffix = widget.Id.Length > 5 ? widget.Id[..5] : widget.Id;
            Add("widget:" + widget.Id, $"{type} · {suffix}", widget.Visible);
        }
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
    }

    private void Add(string key, string title, bool visible)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var check = new CheckBox
        {
            Content = title,
            IsChecked = visible,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(check);
        if (_styleAction is not null)
        {
            var style = new Button
            {
                Content = "样式",
                Style = (Style)FindResource("LinkButton"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10, 0, 0, 0)
            };
            style.Click += (_, _) => _styleAction(key);
            Grid.SetColumn(style, 1);
            row.Children.Add(style);
        }
        CardsPanel.Children.Add(row);
        _items.Add((key, check));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CardVisibility.Clear();
        foreach (var (key, box) in _items) CardVisibility[key] = box.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (_, box) in _items) box.IsChecked = true;
    }
}
