using System;
using System.ComponentModel;
using System.Threading;
using System.Windows.Forms;

namespace AudiobookPlayer;

/// System-tray icon. Shows the current chapter and the skip commands, not only play/pause.
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly Player player;
    private readonly SynchronizationContext? ui;
    private readonly ToolStripMenuItem chapterItem;
    private readonly ToolStripMenuItem skipBackItem;
    private readonly ToolStripMenuItem skipForwardItem;

    public bool Visible
    {
        get => icon.Visible;
        set => icon.Visible = value;
    }

    public TrayService(Player player, Action showWindow, Action exitApp)
    {
        this.player = player;
        ui = SynchronizationContext.Current;

        var menu = new ContextMenuStrip();
        chapterItem = new ToolStripMenuItem("Nothing playing") { Enabled = false };
        skipBackItem = new ToolStripMenuItem("Skip back", null, (_, _) => Run(() => player.SkipConfigured(-1)));
        skipForwardItem = new ToolStripMenuItem("Skip forward", null, (_, _) => Run(() => player.SkipConfigured(1)));
        menu.Items.Add(chapterItem);
        menu.Items.Add("Play / Pause", null, (_, _) => Run(() => player.TogglePlay()));
        menu.Items.Add(skipBackItem);
        menu.Items.Add(skipForwardItem);
        menu.Items.Add("Previous chapter", null, (_, _) => Run(() => player.Previous()));
        menu.Items.Add("Next chapter", null, (_, _) => Run(() => player.Next()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Audiobook Player", null, (_, _) => Run(showWindow));
        menu.Items.Add("Quit", null, (_, _) => Run(exitApp));

        icon = new NotifyIcon
        {
            Text = "Audiobook Player",
            ContextMenuStrip = menu,
            Visible = false,
        };

        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res != null) icon.Icon = new System.Drawing.Icon(res.Stream);
        }
        catch { /* tray still works without the custom icon */ }

        icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Run(showWindow);
        };

        player.NowPlayingChanged += UpdateMenu;
        player.PropertyChanged += OnPlayerChanged;
        UpdateMenu();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Player.ChapterTitle) or nameof(Player.ChapterLabel)
            or nameof(Player.BookTitle) or nameof(Player.HasBook)
            or nameof(Player.SkipBackLabel) or nameof(Player.SkipForwardLabel))
            UpdateMenu();
    }

    private void Run(Action action)
    {
        if (ui != null && SynchronizationContext.Current != ui) ui.Post(_ => action(), null);
        else action();
    }

    private void UpdateMenu()
    {
        if (icon.ContextMenuStrip is { InvokeRequired: true } menu)
        {
            try { menu.BeginInvoke(UpdateMenu); } catch { /* icon is closing */ }
            return;
        }

        string chapter = player.HasBook
            ? $"{player.ChapterLabel}   {player.ChapterTitle}"
            : "Nothing playing";
        if (chapter.Length > 90) chapter = chapter[..87] + "...";
        chapterItem.Text = chapter;

        int skip = player.Settings.SkipSeconds;
        skipBackItem.Text = $"Skip back {skip} s";
        skipForwardItem.Text = $"Skip forward {skip} s";

        string tip = player.HasBook ? $"{player.ChapterLabel}: {player.ChapterTitle}" : "Audiobook Player";
        icon.Text = tip.Length > 63 ? tip[..60] + "..." : tip;
    }

    public void Dispose()
    {
        player.NowPlayingChanged -= UpdateMenu;
        player.PropertyChanged -= OnPlayerChanged;
        icon.Visible = false;
        icon.Dispose();
    }
}
