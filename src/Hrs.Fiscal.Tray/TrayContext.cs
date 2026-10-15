using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Hrs.Fiscal.Tray;

/// <summary>Tray icon (green / orange / red / grey) and the notice window while fiscalization at this workstation is affected.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan RemindAfter = TimeSpan.FromMinutes(10);
    /// <summary>The service may start after the user logs on: report it as down only after this long.</summary>
    private static readonly TimeSpan ServiceGrace = TimeSpan.FromSeconds(30);

    private readonly StatusReader _reader;
    private readonly NotifyIcon _icon;
    private readonly NoticeForm _form = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly Icon _green = Dot(Color.FromArgb(0x12, 0x8A, 0x4B)), _orange = Dot(Color.FromArgb(0xE0, 0x8A, 0x00)),
        _red = Dot(Color.FromArgb(0xC0, 0x26, 0x1C)), _grey = Dot(Color.FromArgb(0x8A, 0x94, 0x9E));
    private ClientStatus _last = new() { Reached = true };
    private TrayState? _shownState;
    private DateTime? _hiddenAt;
    private DateTime? _unreachableSince;
    private string _language = "sq";
    private bool _busy;

    public TrayContext(int port)
    {
        _reader = new StatusReader(port);
        _icon = new NotifyIcon { Icon = _grey, Visible = true, Text = "Fiscal Client" };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatus(); };
        _icon.ContextMenuStrip = new ContextMenuStrip();
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _form.HideClicked += () => { _hiddenAt = DateTime.UtcNow; _form.Hide(); };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        _ = TickAsync();
    }

    private void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        menu.Items.Add(Texts.Get(_language, "menu.show"), null, (_, _) => ShowStatus());
        menu.Items.Add(Texts.Get(_language, "menu.page"), null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo($"http://localhost:{_reader.Port}/") { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { }
        });
    }

    private async System.Threading.Tasks.Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var status = await _reader.ReadAsync();
            if (!status.Reached)
            {
                _unreachableSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _unreachableSince < ServiceGrace) return;   // keep showing the last state for now
            }
            else _unreachableSince = null;
            if (status.Language is { Length: > 0 } lang) _language = lang;
            Apply(status);
        }
        finally { _busy = false; }
    }

    private void Apply(ClientStatus status)
    {
        var previous = _last.State;
        var state = status.State;
        _last = status;

        _icon.Icon = state switch
        {
            TrayState.Online => _green,
            TrayState.Pending => _orange,
            TrayState.ServiceDown or TrayState.ServerUnreachable or TrayState.AtkOffline => _red,
            _ => _grey,
        };
        _icon.Text = Clip(TrayText(status), 63);

        if (ClientStatus.NeedsNotice(state))
        {
            var changed = _shownState != state;
            var remind = _hiddenAt is { } h && DateTime.UtcNow - h > RemindAfter;
            if (changed || remind || _form.Visible)
            {
                _form.ShowNotice(_language, status);
                if (changed || remind) { _hiddenAt = null; _form.ShowAtCorner(); }
            }
            _shownState = state;
        }
        else
        {
            if (_shownState is not null && _form.Visible) _form.Hide();
            if (_shownState is TrayState.AtkOffline || previous is TrayState.AtkOffline)
                _icon.ShowBalloonTip(8000, Texts.Get(_language, "back.title"), Texts.Get(_language, "back.body"), ToolTipIcon.Info);
            else if (_shownState is TrayState.ServerUnreachable or TrayState.ServiceDown)
                _icon.ShowBalloonTip(8000, "Fiscal Client", Texts.Get(_language, "back.server"), ToolTipIcon.Info);
            _shownState = null;
            _hiddenAt = null;
        }
    }

    private string TrayText(ClientStatus s) => s.State switch
    {
        TrayState.Online => Texts.Get(_language, "online"),
        TrayState.Pending => Texts.Get(_language, "pending", s.Pending),
        TrayState.AtkOffline => Texts.Get(_language, "offline"),
        TrayState.ServerUnreachable => Texts.Get(_language, "server"),
        TrayState.ServiceDown => Texts.Get(_language, "service"),
        TrayState.NotReady => Texts.Get(_language, "notready"),
        _ => Texts.Get(_language, "checking"),
    } + (s.PosId > 0 ? $" · POS {s.PosId}" : "");

    /// <summary>Left click / menu: the notice when there is one, otherwise a short balloon with the status.</summary>
    private void ShowStatus()
    {
        if (ClientStatus.NeedsNotice(_last.State)) { _hiddenAt = null; _form.ShowNotice(_language, _last); _form.ShowAtCorner(); return; }
        var text = _last.State switch
        {
            TrayState.Online => Texts.Get(_language, "status.ok"),
            TrayState.Pending => Texts.Get(_language, "status.pending", _last.Pending),
            TrayState.NotReady => Texts.Get(_language, "status.notready"),
            _ => Texts.Get(_language, "status.checking"),
        };
        _icon.ShowBalloonTip(6000, "Fiscal Client" + (_last.PosId > 0 ? $" · POS {_last.PosId}" : ""), text, ToolTipIcon.Info);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private static Icon Dot(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            using var ring = new Pen(Color.White, 3);
            g.FillEllipse(fill, 2, 2, 28, 28);
            g.DrawEllipse(ring, 2, 2, 28, 28);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _icon.Visible = false; _icon.Dispose(); _form.Dispose(); }
        base.Dispose(disposing);
    }
}
