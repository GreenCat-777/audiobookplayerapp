using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AudiobookPlayer;

public partial class MainWindow : Window
{
    private readonly Player player;
    private TrayService? tray;
    private bool exiting;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();

        player = new Player(Dispatcher);
        DataContext = player;

        player.ChapterChanged += id => Dispatcher.InvokeAsync(() => ScrollChapterIntoView(id), DispatcherPriority.Background);
        player.BookmarkRequested += ShowBookmarkDialog;

        tray = new TrayService(player, ShowFromTray, ExitApp) { Visible = player.Settings.ShowTray };

        ChapterScrub.Dragged += f => player.SetChapterDrag(f);
        ChapterScrub.Committed += f => { player.SetChapterDrag(null); player.SeekInChapter(f); };
        BookScrub.Dragged += f => player.SetBookDrag(f);
        BookScrub.Committed += f => { player.SetBookDrag(null); player.SeekBook(f); };
        VolumeScrub.Dragged += f => player.Volume = f;
        VolumeScrub.Committed += f => player.Volume = f;

        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Activated += (_, _) => player.RefreshLibrary();
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) player.OpenDropped(paths[0]);
        };
    }

    // ── window plumbing ──────────────────────────────────────

    private void EnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));   // Windows 11 / 10 20H1+
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));   // older Windows 10
        }
        catch { }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        exiting = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting && player.Settings.CloseToTray && player.Settings.ShowTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        exiting = true;
        tray?.Dispose();
        tray = null;
        player.Shutdown();
    }

    private void ScrollChapterIntoView(int id)
    {
        if (id >= 0 && id < player.ChapterRows.Count) ChaptersList.ScrollIntoView(player.ChapterRows[id]);
    }

    // ── keyboard ─────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl)
        {
            switch (key)
            {
                case Key.O: player.ChooseLibrary(); break;
                case Key.R: player.Rescan(); break;
                case Key.D: ShowBookmarkDialog(); break;
                case Key.Right: player.Next(); break;
                case Key.Left: player.Previous(); break;
                case Key.Up: player.ChangeVolume(0.1); break;
                case Key.Down: player.ChangeVolume(-0.1); break;
                case Key.OemPlus: player.StepSpeed(1); break;
                case Key.OemMinus: player.StepSpeed(-1); break;
                case Key.OemComma: OpenSettings(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox || !player.HasBook) return;

        double skip = player.Settings.SkipSeconds;
        switch (key)
        {
            case Key.Space: player.TogglePlay(); break;
            case Key.Left: player.Skip(shift ? -60 : -skip); break;
            case Key.Right: player.Skip(shift ? 60 : skip); break;
            case Key.OemOpenBrackets: player.Previous(); break;
            case Key.OemCloseBrackets: player.Next(); break;
            case Key.B: ShowBookmarkDialog(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ── sidebar ──────────────────────────────────────────────

    private static T? Context<T>(object sender) where T : class
        => (sender as FrameworkElement)?.DataContext as T;

    private void BookRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (Context<BookRowVM>(sender) is { } vm) player.Open(vm.Book);
    }

    private void BookPlay_Click(object sender, RoutedEventArgs e)
    {
        if (Context<BookRowVM>(sender) is { } vm) { player.Open(vm.Book); player.Play(); }
    }

    private void BookReveal_Click(object sender, RoutedEventArgs e)
    {
        if (Context<BookRowVM>(sender) is not { } vm || vm.Book.Tracks.Count == 0) return;
        try { Process.Start("explorer.exe", $"/select,\"{vm.Book.Tracks[0].Path}\""); } catch { }
    }

    private void BookRemoveCover_Click(object sender, RoutedEventArgs e)
    {
        if (Context<BookRowVM>(sender) is { } vm) player.RemoveCustomCover(vm.Book);
    }

    private void BookReset_Click(object sender, RoutedEventArgs e)
    {
        if (Context<BookRowVM>(sender) is { } vm) player.ResetProgress(vm.Book);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        player.SetSearch(SearchBox.Text);
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => player.ChooseLibrary();
    private void Rescan_Click(object sender, RoutedEventArgs e) => player.Rescan();

    private void ShowLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        if (player.LibraryPath is { } p)
            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        new SettingsWindow(player) { Owner = this }.ShowDialog();
        if (tray != null) tray.Visible = player.Settings.ShowTray;
    }

    // ── transport ────────────────────────────────────────────

    private void PlayPause_Click(object sender, RoutedEventArgs e) => player.TogglePlay();
    private void Prev_Click(object sender, RoutedEventArgs e) => player.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => player.Next();
    private void Back60_Click(object sender, RoutedEventArgs e) => player.Skip(-60);
    private void Forward60_Click(object sender, RoutedEventArgs e) => player.Skip(60);
    private void SkipBack_Click(object sender, RoutedEventArgs e) => player.SkipConfigured(-1);
    private void SkipForward_Click(object sender, RoutedEventArgs e) => player.SkipConfigured(1);

    private void Speed_Click(object sender, RoutedEventArgs e) => SpeedPopup.IsOpen = !SpeedPopup.IsOpen;

    private void SpeedItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is double s)
        {
            player.SetSpeed(s);
            SpeedPopup.IsOpen = false;
        }
    }

    private void Sleep_Click(object sender, RoutedEventArgs e)
    {
        if (SleepBtn.ContextMenu is { } menu)
        {
            menu.PlacementTarget = SleepBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        }
    }

    private void SleepOff_Click(object sender, RoutedEventArgs e) => player.CancelSleep();

    private void SleepMinutes_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is string s && int.TryParse(s, out int minutes)) player.StartSleep(minutes);
    }

    private void SleepChapter_Click(object sender, RoutedEventArgs e) => player.SleepAtEndOfChapter();

    // ── chapters, bookmarks, cover ───────────────────────────

    private void Chapter_Click(object sender, MouseButtonEventArgs e)
    {
        if (Context<ChapterRowVM>(sender) is { } vm) player.SelectChapter(vm.Id);
    }

    private void Bookmark_Click(object sender, MouseButtonEventArgs e)
    {
        if (Context<BookmarkVM>(sender) is { } vm) player.JumpTo(vm);
    }

    private void BookmarkDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Context<BookmarkVM>(sender) is { } vm) player.DeleteBookmark(vm);
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e) => ShowBookmarkDialog();

    private void ShowBookmarkDialog()
    {
        if (!player.HasBook) return;
        var dlg = new BookmarkWindow(player.DefaultBookmarkTitle) { Owner = this };
        if (dlg.ShowDialog() == true) player.AddBookmark(dlg.BookmarkTitle, dlg.Note);
    }

    private void ChangeCover_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose cover art",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) player.SetCover(dlg.FileName);
    }

    /// Let the mouse wheel continue to the page when the inner list hits its end.
    private void InnerList_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        var sv = FindScrollViewer(d);
        if (sv == null) return;
        bool atTop = sv.VerticalOffset <= 0 && e.Delta > 0;
        bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight && e.Delta < 0;
        if (sv.ScrollableHeight <= 0 || atTop || atBottom)
        {
            e.Handled = true;
            MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset - e.Delta);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
