using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vermilune.Core;

namespace Vermilune.UI.Dialogs;

public partial class YggdrasilCharacterDialog : Window
{
    public YggdrasilCharacter? Selected { get; private set; }

    private YggdrasilCharacterDialog(IReadOnlyList<YggdrasilCharacter> characters, string selectedId)
    {
        InitializeComponent();
        CharacterList.ItemsSource = characters;
        CharacterList.SelectedItem = characters.FirstOrDefault(c =>
                                        string.Equals(c.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                                    ?? characters.FirstOrDefault();
    }

    public static YggdrasilCharacter? Show(Window? owner, IReadOnlyList<YggdrasilCharacter> characters,
        string selectedId)
    {
        if (characters.Count == 0) return null;
        var dialog = new YggdrasilCharacterDialog(characters, selectedId) { Owner = owner };
        if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (CharacterList.SelectedItem is not YggdrasilCharacter character) return;
        Selected = character;
        DialogResult = true;
    }

    private void CharacterList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CharacterList.SelectedItem is YggdrasilCharacter) Apply_Click(sender, e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
