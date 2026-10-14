using System.Windows;

namespace AudiobookPlayer;

public partial class BookmarkWindow : Window
{
    public string BookmarkTitle => TitleBox.Text.Trim();
    public string Note => NoteBox.Text.Trim();

    public BookmarkWindow(string suggestedTitle)
    {
        InitializeComponent();
        TitleBox.Text = suggestedTitle;
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
