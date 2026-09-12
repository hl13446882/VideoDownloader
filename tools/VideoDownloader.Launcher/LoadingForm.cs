using System;
using System.Drawing;
using System.Windows.Forms;

namespace VideoDownloader.Launcher;

/// <summary>Simple centered splash so the user sees progress while the main app boots.</summary>
internal sealed class LoadingForm : Form
{
    private readonly Label _status;

    public LoadingForm()
    {
        Text = "Video Downloader";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Color.FromArgb(32, 32, 36);
        Size = new Size(360, 140);
        DoubleBuffered = true;

        var title = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 48,
            TextAlign = ContentAlignment.BottomCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            Text = "Video Downloader",
            Padding = new Padding(0, 0, 0, 4)
        };

        _status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopCenter,
            ForeColor = Color.FromArgb(200, 200, 205),
            Font = new Font("Segoe UI", 10F, FontStyle.Regular),
            Text = "正在启动，请稍候…",
            Padding = new Padding(16, 8, 16, 16)
        };

        Controls.Add(_status);
        Controls.Add(title);

        Paint += (_, e) =>
        {
            using (var pen = new Pen(Color.FromArgb(70, 70, 78)))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };
    }

    public void SetStatus(string text)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(text)));
            return;
        }

        _status.Text = text;
        _status.Refresh();
        Application.DoEvents();
    }
}
