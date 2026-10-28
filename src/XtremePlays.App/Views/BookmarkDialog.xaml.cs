using System.Windows;
using XtremePlays.App.Models;
using XtremePlays.App.Services;

namespace XtremePlays.App.Views;

public partial class BookmarkDialog : Window
{
    public BookmarkDialog(Bookmark? bookmark = null)
    {
        InitializeComponent();
        DialogTitle.Text = bookmark is null ? "Add bookmark" : "Edit bookmark";
        TitleInput.Text = bookmark?.Title ?? string.Empty;
        UrlInput.Text = bookmark?.Url ?? string.Empty;
        Result = bookmark is null ? new Bookmark() : new Bookmark { Title = bookmark.Title, Url = bookmark.Url };
        Loaded += (_, _) => TitleInput.Focus();
    }

    public Bookmark Result { get; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = SettingsStore.SanitizeTitle(TitleInput.Text);
        var url = SettingsStore.NormalizeWebUrl(UrlInput.Text);
        if (title.Length == 0)
        {
            ValidationText.Text = "Enter a name for this bookmark.";
            TitleInput.Focus();
            return;
        }

        if (url is null)
        {
            ValidationText.Text = "Enter a full website address beginning with http:// or https://.";
            UrlInput.Focus();
            return;
        }

        Result.Title = title;
        Result.Url = url;
        DialogResult = true;
    }
}
