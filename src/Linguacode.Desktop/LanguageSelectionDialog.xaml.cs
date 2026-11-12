using System.Globalization;
using System.Windows;
using Linguacode.Application;
using Linguacode.Audio;

namespace Linguacode.Desktop;

public partial class LanguageSelectionDialog : Window
{
    private sealed record Choice(string Code, string Name);
    private readonly bool translation;
    public string SelectedLanguage => LanguageInput.SelectedItem is Choice choice && LanguageInput.Text == choice.Name
        ? choice.Code : LanguageInput.Text.Trim().ToLowerInvariant();

    public LanguageSelectionDialog(bool translation, string language, string explanation)
    {
        this.translation = translation;
        InitializeComponent();
        Title = translation ? "Translate to…" : "Re-transcribe recording";
        LanguageLabel.Content = translation ? "Target language" : "Source language";
        System.Windows.Automation.AutomationProperties.SetName(LanguageInput, LanguageLabel.Content.ToString());
        ConfirmButton.Content = translation ? "Translate" : "Re-transcribe";
        Explanation.Text = explanation;
        var choices = translation
            ? CultureInfo.GetCultures(CultureTypes.NeutralCultures).Where(c => c.Name.Length > 0)
                .Select(c => new Choice(c.Name.ToLowerInvariant(), c.EnglishName)).OrderBy(c => c.Name).ToList()
            : new List<Choice> { new("auto", "Auto detect"), new("en", "English"), new("hr", "Croatian"), new("sl", "Slovenian") };
        if (!choices.Any(c => c.Code == language)) choices.Add(new(language, CallRecorder.TranslationLanguageName(language)));
        LanguageInput.ItemsSource = choices;
        LanguageInput.SelectedValue = language;
        LanguageInput.ToolTip = translation ? "Select a language or type its language code, such as de or it." : "Select Auto detect or a source language; other supported language codes can be typed.";
        Loaded += (_, _) => LanguageInput.Focus();
    }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (translation)
            {
                if (string.IsNullOrWhiteSpace(SelectedLanguage) || SelectedLanguage == "auto" ||
                    !System.Text.RegularExpressions.Regex.IsMatch(SelectedLanguage, @"^[a-z]{2,3}(-[a-zA-Z]{2,8})?$"))
                    throw new ArgumentException("Select a target language or enter a language code, such as de or it.");
            }
            else RecordingAnalysis.ValidateOptions(new(Language: SelectedLanguage));
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or Linguacode.Core.UserFacingException)
        { Validation.Text = ex.Message; Validation.Visibility = Visibility.Visible; }
    }
}
