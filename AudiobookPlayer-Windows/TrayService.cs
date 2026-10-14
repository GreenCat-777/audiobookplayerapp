using System;

namespace AudiobookPlayer;

/// System-tray icon with a small play/pause/skip menu (the Windows take on the Mac menu-bar player).
public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon icon;
    private readonly Player player;

    public bool Visible
    {
        get => icon.Visible;
        set => icon.Visible = value;
    }

    public TrayService(Player player, Action showWindow, Action exitApp)
    {
        this.player = player;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Play / Pause", null, (_, _) => player.TogglePlay());
        menu.Items.Add("Previous chapter", null, (_, _) => player.Previous());
        menu.Items.Add("Next chapter", null, (_, _) => player.Next());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Open Audiobook Player", null, (_, _) => showWindow());
        menu.Items.Add("Quit", null, (_, _) => exitApp());

        icon = new System.Windows.Forms.NotifyIcon
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
        catch { }

        icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) showWindow();
        };

        player.NowPlayingChanged += UpdateTip;
    }

    private void UpdateTip()
    {
        string text = player.HasBook ? $"{player.BookTitle} - {player.ChapterTitle}" : "Audiobook Player";
        icon.Text = text.Length > 63 ? text[..60] + "..." : text;
    }

    public void Dispose()
    {
        player.NowPlayingChanged -= UpdateTip;
        icon.Visible = false;
        icon.Dispose();
    }
}
