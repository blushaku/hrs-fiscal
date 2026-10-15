using System;
using System.Drawing;
using System.Windows.Forms;

namespace Hrs.Fiscal.Tray;

/// <summary>
/// Small always-on-top window in the bottom-right corner while ATK, the fiscal server or the local Fiscal Client cannot be
/// reached. It cannot be closed while the problem lasts, only hidden for a while (it comes back after 10 minutes).
/// </summary>
internal sealed class NoticeForm : Form
{
    private readonly Label _title = new() { AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(0) };
    private readonly Label _body = new() { AutoSize = true, MaximumSize = new Size(340, 0), Font = new Font("Segoe UI", 9.5f), Margin = new Padding(0, 0, 0, 6) };
    private readonly Label _details = new() { AutoSize = true, MaximumSize = new Size(340, 0), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
    private readonly Label _footer = new() { AutoSize = true, MaximumSize = new Size(340, 0), Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(0x4F, 0x5D, 0x69), Margin = new Padding(0, 0, 0, 8) };
    private readonly Button _hide = new() { AutoSize = true, FlatStyle = FlatStyle.System };
    private readonly Panel _header = new() { Dock = DockStyle.Top, Padding = new Padding(14, 10, 14, 10), AutoSize = true, BackColor = Color.FromArgb(0xB4, 0x23, 0x18) };
    public event Action? HideClicked;

    public NoticeForm()
    {
        Text = "Opera Cloud Fiscal Solution - Kosovo";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        MaximizeBox = MinimizeBox = false;

        _header.Controls.Add(_title);
        var body = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(14, 10, 14, 10) };
        body.Controls.AddRange([_body, _details, _footer, _hide]);
        Controls.Add(body);
        Controls.Add(_header);
        _hide.Click += (_, _) => HideClicked?.Invoke();
    }

    /// <summary>Not closable while the problem lasts: closing hides it (it comes back later).</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideClicked?.Invoke(); return; }
        base.OnFormClosing(e);
    }

    /// <summary>Shown without taking the focus from OPERA.</summary>
    protected override bool ShowWithoutActivation => true;

    public void ShowNotice(string language, ClientStatus s)
    {
        string L(string key, params object[] args) => Texts.Get(language, key, args);
        static string Local(DateTime? utc) => utc is { } u ? u.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "–";
        switch (s.State)
        {
            case TrayState.AtkOffline:
                _title.Text = L("atk.title") + (s.Environment == "Test" ? " (TEST)" : "");
                _body.Text = L("atk.since", Local(s.OfflineSinceUtc)) + " " + L("atk.body");
                _details.Text = L("atk.pending", s.Pending) + (s.DeadlineUtc is { } d ? Environment.NewLine + L("atk.deadline", Local(d)) : "");
                _footer.Text = L("atk.footer");
                break;
            case TrayState.ServerUnreachable:
                _title.Text = L("server.title");
                _body.Text = L("server.body");
                _details.Text = L("server.since", Local(s.LastHeartbeatUtc));
                _footer.Text = L("notify");
                break;
            default:
                _title.Text = L("service.title");
                _body.Text = L("service.body");
                _details.Text = "";
                _footer.Text = L("notify");
                break;
        }
        _details.Visible = _details.Text.Length > 0;
        _hide.Text = L("hide");
        if (Visible) ShowAtCorner();
    }

    public void ShowAtCorner()
    {
        if (!Visible) Show();
        PerformLayout();
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
    }
}
