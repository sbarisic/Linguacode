using System.Windows;
using System.Windows.Controls;

namespace Linguacode.Desktop;

public partial class RenameSpeakerDialog : Window
{
    public string SpeakerName => NameInput.Text.Trim();
    public RenameSpeakerDialog(string name)
    {
        InitializeComponent();
        NameInput.Text = name;
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
    }
    private void NameChanged(object sender, TextChangedEventArgs e)
    {
        if (SaveButton is not null) SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameInput.Text) && !NameInput.Text.Any(char.IsControl);
    }
    private void SaveClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
