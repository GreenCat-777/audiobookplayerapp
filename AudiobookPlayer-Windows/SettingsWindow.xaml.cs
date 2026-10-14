using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace AudiobookPlayer;

public partial class SettingsWindow : Window
{
    private readonly Player player;
    private readonly bool loading = true;

    public SettingsWindow(Player player)
    {
        this.player = player;
        InitializeComponent();

        var s = player.Settings;
        Skip10.IsChecked = s.SkipSeconds == 10;
        Skip15.IsChecked = s.SkipSeconds == 15;
        Skip30.IsChecked = s.SkipSeconds == 30;
        Rew0.IsChecked = s.AutoRewind == 0;
        Rew3.IsChecked = s.AutoRewind == 3;
        Rew5.IsChecked = s.AutoRewind == 5;
        Rew10.IsChecked = s.AutoRewind == 10;
        Rew15.IsChecked = s.AutoRewind == 15;
        Rew30.IsChecked = s.AutoRewind == 30;
        PerBookToggle.IsChecked = s.PerBookSpeed;
        TrayToggle.IsChecked = s.ShowTray;
        CloseTrayToggle.IsChecked = s.CloseToTray;
        LibraryText.Text = player.LibraryPath ?? "None chosen";
        loading = false;
    }

    private void Skip_Checked(object sender, RoutedEventArgs e)
    {
        if (loading || (sender as RadioButton)?.Tag is not string t || !int.TryParse(t, out int v)) return;
        player.Settings.SkipSeconds = v;
        player.SettingsChanged();
    }

    private void Rewind_Checked(object sender, RoutedEventArgs e)
    {
        if (loading || (sender as RadioButton)?.Tag is not string t || !int.TryParse(t, out int v)) return;
        player.Settings.AutoRewind = v;
        player.SettingsChanged();
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var s = player.Settings;
        s.PerBookSpeed = PerBookToggle.IsChecked == true;
        s.ShowTray = TrayToggle.IsChecked == true;
        s.CloseToTray = CloseTrayToggle.IsChecked == true;
        player.SettingsChanged();
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        player.ChooseLibrary();
        LibraryText.Text = player.LibraryPath ?? "None chosen";
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => player.Rescan();

    private void DataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(player.DataFolder) { UseShellExecute = true }); } catch { }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
