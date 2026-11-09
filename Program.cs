// MonitorOff — Windows 11 tray app that turns off all monitors.
// Left-click the tray icon (or use the menu) to turn monitors off.
// Wake options (toggle in the right-click menu): key press, mouse movement, mouse click.
// Input that isn't an enabled wake trigger is swallowed while the monitors are off,
// so e.g. a bumped mouse won't wake them when only "Wake on key press" is checked.
// Phone remote: a small web page served on port 8765 (same Wi-Fi only, PIN-protected).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using QRCoder;

namespace MonitorOff
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            using var mutex = new Mutex(true, "MonitorOffTray_SingleInstance", out bool created);
            if (!created) return; // already running
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayContext());
        }
    }

    sealed class TrayContext : ApplicationContext
    {
        const string SettingsKey = @"Software\MonitorOffTray";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "MonitorOffTray";
        // Movement must be continuous: a pause longer than this resets the running total,
        // so small wiggles spread out over time never add up to a wake.
        const int MoveResetMs = 600;

        static readonly (string Label, int Pixels)[] MoveLevels =
        {
            ("Tiny wiggle (25 px)", 25),
            ("Short move (150 px)", 150),
            ("Medium move (400 px)", 400),
            ("Long move (800 px)", 800),
            ("Very long move (1500 px)", 1500),
        };

        readonly NotifyIcon _tray;
        readonly ToolStripMenuItem _wakeKey, _wakeMove, _wakeClick, _startup, _mute, _lockOnOff;
        volatile bool _useHttps; // phone remote over https (off by default: plain http)

        // Lock-the-PC option
        bool _lockedByUs;          // we locked Windows as part of this turn-off
        bool _lockPending;         // locked, waiting a moment for the lock screen before turning off
        uint _lockInputBaseline;   // Windows' last-input time when the monitors went off
        readonly System.Windows.Forms.Timer _lockDelay = new System.Windows.Forms.Timer { Interval = 1200 };
        readonly System.Windows.Forms.Timer _lockWatch = new System.Windows.Forms.Timer { Interval = 300 };
        bool _mutedByUs;     // only restore on wake if we were the ones who silenced it
        float _savedVolume;  // speaker volume before turning off
        readonly System.Windows.Forms.Timer _delay = new System.Windows.Forms.Timer { Interval = 800 };
        readonly SynchronizationContext _ui;

        // Keep delegates alive so the GC doesn't collect them while hooked.
        readonly Native.HookProc _kbProc, _msProc;
        IntPtr _kbHook, _msHook;

        bool _off;
        Point _offCursor;
        int _moveAccum;
        int _moveThreshold;
        uint _lastMoveTick;
        volatile bool _wakeRequested;
        bool _hookWakeKey, _hookWakeMove, _hookWakeClick;

        // Hidden window that receives the "monitors off" command and display on/off notices.
        readonly PowerWindow _powerWnd = new PowerWindow();
        // If the screens pop back on by themselves, turn them off again up to this many times
        // (setting in the tray menu; -1 = unlimited). Retries only happen within a time window
        // that grows with the count, so a much later wake (e.g. Ctrl+Alt+Del) is respected.
        const int DefaultReasserts = 5;
        int _maxReasserts;
        int ReassertWindowMs => 15000 + Math.Max(0, _maxReasserts - DefaultReasserts) * 3000;
        long _offTick;
        int _reasserts;
        volatile int _offSession;   // bumps on every off/wake; stops stale background loops
        bool _useDdc;               // turn-off method: true = DDC/CI direct to monitors
        volatile bool _ddcActive;   // monitors currently powered down via DDC/CI
        volatile bool _ddcWarned;   // already showed the "a monitor didn't respond" tip
        readonly System.Windows.Forms.Timer _reassertTimer = new System.Windows.Forms.Timer { Interval = 600 };

        // Phone remote
        readonly RemoteServer _server;
        readonly ToolStripMenuItem _remoteEnabled;
        string _pin;
        Form _remoteForm;

        public TrayContext()
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            Theme.Mode = ReadInt("Theme", 0);
            _kbProc = KeyboardHook;
            _msProc = MouseHook;
            _powerWnd.CreateHandle(new CreateParams { Caption = "MonitorOffPower" });
            _powerWnd.DisplayStateChanged += OnDisplayState;
            _powerWnd.RawInputReceived += (dev, type) =>
            {
                if (!_off) return;
                string name = PowerWindow.DeviceName(dev, type);
                WakeLog.AddThrottled("raw:" + name, "Input from " + name);
            };
            _powerWnd.DisplayLayoutChanged += () =>
            {
                if (_off) WakeLog.Add("Windows: display layout changed (a monitor connected, disconnected or changed mode)", "layout");
            };
            _powerWnd.DevicesChanged += () =>
            {
                if (_off) WakeLog.AddThrottled("devchange", "Windows: a device was connected or disconnected (USB/hardware)", 1000);
            };
            _powerWnd.ListenForDisplayState();
            _reassertTimer.Tick += (_, _) =>
            {
                _reassertTimer.Stop();
                if (_off && !_wakeRequested) SendOff();
            };

            _wakeKey = MakeToggle("Wake on key press", "WakeKey", true);
            _wakeMove = MakeToggle("Wake on mouse movement", "WakeMove", true);
            _wakeClick = MakeToggle("Wake on mouse click", "WakeClick", true);

            // "Mouse movement needed" submenu: how far the mouse must travel to wake.
            _moveThreshold = ReadInt("MoveThreshold", 400);
            var moveMenu = new ToolStripMenuItem("Mouse movement needed");
            foreach (var (label, px) in MoveLevels)
            {
                var item = new ToolStripMenuItem(label) { Checked = px == _moveThreshold, Tag = px };
                item.Click += (_, _) =>
                {
                    _moveThreshold = (int)item.Tag;
                    WriteInt("MoveThreshold", _moveThreshold);
                    foreach (ToolStripMenuItem other in moveMenu.DropDownItems)
                        other.Checked = other == item;
                };
                moveMenu.DropDownItems.Add(item);
            }
            moveMenu.Enabled = _wakeMove.Checked;
            _wakeMove.CheckedChanged += (_, _) => moveMenu.Enabled = _wakeMove.Checked;

            // "If screens wake by themselves" submenu: how many times to turn them off again
            _maxReasserts = ReadInt("MaxReasserts", DefaultReasserts);
            var reassertMenu = new ToolStripMenuItem("If screens wake by themselves");
            (string Label, int Value)[] reassertChoices =
            {
                ("Leave them on", 0),
                ("Turn off again 1 time", 1),
                ("Turn off again 3 times", 3),
                ("Turn off again 5 times (default)", 5),
                ("Turn off again 10 times", 10),
                ("Turn off again 20 times", 20),
                ("Keep turning them off (until you wake them)", -1),
            };
            foreach (var (label, value) in reassertChoices)
            {
                var item = new ToolStripMenuItem(label) { Checked = value == _maxReasserts, Tag = value };
                item.Click += (_, _) =>
                {
                    _maxReasserts = (int)item.Tag;
                    WriteInt("MaxReasserts", _maxReasserts);
                    foreach (ToolStripMenuItem other in reassertMenu.DropDownItems) other.Checked = other == item;
                };
                reassertMenu.DropDownItems.Add(item);
            }
            // A saved value that isn't one of the choices (shouldn't happen) falls back to the default.
            if (Array.FindIndex(reassertChoices, c => c.Value == _maxReasserts) < 0)
            {
                _maxReasserts = DefaultReasserts;
                foreach (ToolStripMenuItem it in reassertMenu.DropDownItems) it.Checked = (int)it.Tag == DefaultReasserts;
            }

            // "Turn-off method" submenu
            _useDdc = ReadInt("OffMethod", 0) == 1;
            var methodMenu = new ToolStripMenuItem("Turn-off method");
            var methodWindows = new ToolStripMenuItem("Windows display sleep (standard)") { Checked = !_useDdc };
            var methodDdc = new ToolStripMenuItem("Direct to monitors (DDC/CI) — if screens wake right back up") { Checked = _useDdc };
            methodWindows.Click += (_, _) => { _useDdc = false; WriteInt("OffMethod", 0); methodWindows.Checked = true; methodDdc.Checked = false; };
            methodDdc.Click += (_, _) => { _useDdc = true; _ddcWarned = false; WriteInt("OffMethod", 1); methodWindows.Checked = false; methodDdc.Checked = true; };
            methodMenu.DropDownItems.Add(methodWindows);
            methodMenu.DropDownItems.Add(methodDdc);

            _lockOnOff = new ToolStripMenuItem("Lock the PC when turning off") { CheckOnClick = true, Checked = ReadBool("LockOnOff", false) };
            _lockOnOff.CheckedChanged += (_, _) => WriteBool("LockOnOff", _lockOnOff.Checked);

            _mute = new ToolStripMenuItem("Mute sound while off") { CheckOnClick = true, Checked = ReadBool("MuteWhileOff", false) };
            _mute.CheckedChanged += (_, _) => WriteBool("MuteWhileOff", _mute.Checked);

            _startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = IsStartupEnabled() };
            // Keep the startup entry pointing at this .exe (e.g. after it was renamed or moved).
            if (_startup.Checked) SetStartup(true);
            _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);

            // Phone remote submenu
            _pin = ReadString("RemotePin");
            if (string.IsNullOrEmpty(_pin)) { _pin = NewPin(); WriteString("RemotePin", _pin); }
            _useHttps = ReadBool("RemoteHttps", false);
            _server = new RemoteServer(() => _pin, RemoteCommand, () => _useHttps, () => LocalCerts.Server(LocalIps()), IconPng(192));

            var remoteMenu = new ToolStripMenuItem("Phone remote");
            _remoteEnabled = new ToolStripMenuItem("Enabled") { CheckOnClick = true, Checked = ReadBool("RemoteEnabled", true) };
            _remoteEnabled.CheckedChanged += (_, _) => { WriteBool("RemoteEnabled", _remoteEnabled.Checked); ApplyRemote(); };
            remoteMenu.DropDownItems.Add(_remoteEnabled);
            var httpsItem = new ToolStripMenuItem("Use HTTPS (secure connection)") { CheckOnClick = true, Checked = _useHttps };
            httpsItem.CheckedChanged += (_, _) =>
            {
                _useHttps = httpsItem.Checked;
                WriteBool("RemoteHttps", _useHttps);
                ApplyRemote();
                if (_remoteForm != null) ShowRemoteInfo(); // show the matching link/QR codes
            };
            remoteMenu.DropDownItems.Add(httpsItem);
            remoteMenu.DropDownItems.Add("Connect a phone (QR code)…", null, (_, _) => ShowRemoteInfo());
            remoteMenu.DropDownItems.Add("Change PIN", null, (_, _) =>
            {
                _pin = NewPin();
                WriteString("RemotePin", _pin);
                ShowRemoteInfo();
            });

            var offItem = new ToolStripMenuItem("Turn monitors off", null, (_, _) => ScheduleOff());
            offItem.Font = new Font(offItem.Font, FontStyle.Bold);

            // "Appearance" submenu: Follow Windows / Light / Dark
            var appearanceMenu = new ToolStripMenuItem("Appearance");
            (string Label, int Value)[] themeChoices = { ("Follow Windows", 0), ("Light", 1), ("Dark", 2) };
            foreach (var (label, value) in themeChoices)
            {
                var item = new ToolStripMenuItem(label) { Checked = value == Theme.Mode, Tag = value };
                item.Click += (_, _) =>
                {
                    Theme.Mode = (int)item.Tag;
                    WriteInt("Theme", Theme.Mode);
                    foreach (ToolStripMenuItem other in appearanceMenu.DropDownItems) other.Checked = other == item;
                    ApplyThemeEverywhere();
                };
                appearanceMenu.DropDownItems.Add(item);
            }

            var menu = new ContextMenuStrip();
            _menu = menu;
            menu.Items.Add(offItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_wakeKey);
            menu.Items.Add(_wakeMove);
            menu.Items.Add(moveMenu);
            menu.Items.Add(_wakeClick);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Monitors…", null, (_, _) => ShowMonitors());
            menu.Items.Add("Wake log…", null, (_, _) => ShowWakeLog());
            menu.Items.Add(methodMenu);
            menu.Items.Add(reassertMenu);
            menu.Items.Add(_lockOnOff);
            menu.Items.Add(_mute);
            menu.Items.Add(remoteMenu);
            menu.Items.Add(appearanceMenu);
            menu.Items.Add(_startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("About…", null, (_, _) => ShowAbout());
            menu.Items.Add("Exit", null, (_, _) => Quit());

            _tray = new NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "Simple Monitor Off — click to turn displays off",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ScheduleOff(); };

            _delay.Tick += (_, _) => { _delay.Stop(); TurnOff(_lockOnOff.Checked); };

            // After locking, give the lock screen a moment to appear, then turn the monitors off.
            _lockDelay.Tick += (_, _) =>
            {
                _lockDelay.Stop();
                if (!_lockPending) return;
                _lockPending = false;
                TurnOff(false);
            };

            // While locked, this app can't see keyboard/mouse input (Windows keeps the lock
            // screen separate), so watch Windows' "last input" time instead and wake on any input.
            _lockWatch.Tick += (_, _) =>
            {
                if (!_off || !_lockedByUs) { _lockWatch.Stop(); return; }
                if (Native.LastInputTick() != _lockInputBaseline)
                {
                    WakeLog.Add("Woken by input on the lock screen");
                    Wake();
                }
            };
            SystemEvents.SessionSwitch += (_, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionUnlock)
                    _ui.Post(_ =>
                    {
                        if (_off) { WakeLog.Add("Woken: you signed back in"); Wake(); }
                        _lockedByUs = false;
                    }, null);
            };

            Theme.ApplyMenu(menu);
            // "Follow Windows": switch along with Windows' light/dark setting.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Theme.Mode == 0)
                    _ui.Post(_ => ApplyThemeEverywhere(), null);
            };

            ApplyRemote();
        }

        ContextMenuStrip _menu;

        // Re-style the tray menu and every open window after the theme changes.
        void ApplyThemeEverywhere()
        {
            Theme.ApplyMenu(_menu);
            foreach (Form f in Application.OpenForms)
                if (f is not BlackCover) Theme.Apply(f);
        }

        // ---------- Phone remote ----------

        void ApplyRemote()
        {
            if (!_remoteEnabled.Checked) { _server.Stop(); return; }
            // Create the HTTPS certificates ahead of time (takes a second the first time).
            if (_useHttps) Task.Run(() =>
            {
                try { LocalCerts.Server(LocalIps()); }
                catch (Exception ex) { WakeLog.Add("Phone remote: couldn't create the HTTPS certificate — " + ex.Message); }
            });
            try { _server.Start(); }
            catch (SocketException)
            {
                _tray.ShowBalloonTip(4000, "Monitor Off",
                    $"Phone remote couldn't start: port {RemoteServer.Port} is already in use.", ToolTipIcon.Warning);
            }
        }

        // Called from the server's background thread; runs the command on the UI thread.
        string RemoteCommand(string cmd)
        {
            string json = null;
            _ui.Send(_ =>
            {
                switch (cmd)
                {
                    case "off": if (!_off) WakeLog.Add("Turn-off requested from the phone remote"); _delay.Stop(); TurnOff(false); break;
                    case "off-lock": if (!_off) WakeLog.Add("Turn-off and lock requested from the phone remote"); _delay.Stop(); TurnOff(true); break;
                    case "wake": if (_off) WakeLog.Add("Woken from the phone remote"); Wake(); break;
                    case "mute-on": _mute.Checked = true; break;
                    case "mute-off": _mute.Checked = false; break;
                }
                json = $"{{\"off\":{(_off ? "true" : "false")},\"mute\":{(_mute.Checked ? "true" : "false")}}}";
            }, null);
            return json;
        }

        // ---------- Wake log window ----------

        Form _logForm;

        void ShowWakeLog()
        {
            if (_logForm != null) { _logForm.Activate(); return; }

            var form = new Form
            {
                Text = "Simple Monitor Off — Wake log",
                StartPosition = FormStartPosition.CenterScreen,
                Size = new Size(940, 720),
                Icon = _tray.Icon,
                Font = new Font("Segoe UI", 10f)
            };

            // ----- top: the log -----
            var box = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10f),
                IntegralHeight = false,
                HorizontalScrollbar = true,
                SelectionMode = SelectionMode.One,
                FormattingEnabled = true
            };
            var help = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(8, 6, 8, 0),
                Text = "Turn the monitors off, let them wake up on their own, then look here. The lines just before " +
                       "\"came back on\" show the cause: an input device, injected software input, a monitor reconnecting, or a program."
            };
            var logButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(6, 4, 6, 0) };
            var hide = new Button { Text = "Hide this input", AutoSize = true, Enabled = false };
            var hidden = new Button { Text = "Hidden inputs…", AutoSize = true };
            var copy = new Button { Text = "Copy log", AutoSize = true };
            var clear = new Button { Text = "Clear log", AutoSize = true };
            logButtons.Controls.AddRange(new Control[] { hide, hidden, copy, clear });

            var logPanel = new Panel { Dock = DockStyle.Fill };
            logPanel.Controls.Add(box);
            logPanel.Controls.Add(help);
            logPanel.Controls.Add(logButtons);

            // ----- bottom: programs keeping the display awake -----
            var progTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(8, 8, 8, 0),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Text = "Programs keeping the display awake"
            };
            var progs = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            progs.Columns.Add("Type", 90);
            progs.Columns.Add("Name", 360);
            progs.Columns.Add("Status", 360);
            progs.Items.Add(new ListViewItem(new[] { "", "Click \"Check programs\" to find out." }));

            var progButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(6, 4, 6, 0) };
            var check = new Button { Text = "Check programs (admin)", AutoSize = true };
            var ignore = new Button { Text = "Ignore selected", AutoSize = true, Enabled = false };
            var unignore = new Button { Text = "Stop ignoring selected", AutoSize = true, Enabled = false };
            progButtons.Controls.AddRange(new Control[] { check, ignore, unignore });

            var progNote = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                Padding = new Padding(8, 2, 8, 0),
                ForeColor = Color.DimGray,
                Text = "Ignoring tells Windows to disregard that program's \"keep the display on\" request. If a program wakes " +
                       "the screens some other way (e.g. injected input), the log above will show it."
            };

            var progPanel = new Panel { Dock = DockStyle.Bottom, Height = 250 };
            progPanel.Controls.Add(progs);
            progPanel.Controls.Add(progTitle);
            progPanel.Controls.Add(progNote);
            progPanel.Controls.Add(progButtons);

            // ----- behaviour -----
            bool pending = false;
            void RefreshLog()
            {
                pending = false;
                if (form.IsDisposed) return;

                var entries = WakeLog.Snapshot();
                long selectedId = (box.SelectedItem as WakeLog.Entry)?.Id ?? -1;
                bool atBottom = box.Items.Count == 0 ||
                                box.TopIndex + Math.Max(1, box.ClientSize.Height / Math.Max(1, box.ItemHeight)) >= box.Items.Count - 1;

                box.BeginUpdate();
                box.Items.Clear();
                foreach (var e in entries) box.Items.Add(e);
                box.EndUpdate();

                // Keep the selected line selected while new lines arrive; otherwise follow the newest.
                int keep = Array.FindIndex(entries, e => e.Id == selectedId);
                if (keep >= 0) box.SelectedIndex = keep;
                else if (atBottom && box.Items.Count > 0) box.TopIndex = box.Items.Count - 1;
                hide.Enabled = box.SelectedItem is WakeLog.Entry sel && sel.Key != null;
            }
            box.Format += (_, e) => e.Value = (e.ListItem as WakeLog.Entry)?.Text ?? "";
            box.SelectedIndexChanged += (_, _) =>
                hide.Enabled = box.SelectedItem is WakeLog.Entry sel && sel.Key != null;
            Action onChanged = () =>
            {
                if (pending || form.IsDisposed || !form.IsHandleCreated) return;
                pending = true;
                form.BeginInvoke((Action)RefreshLog);
            };
            WakeLog.Changed += onChanged;

            AwakeEntry Selected() => progs.SelectedItems.Count == 1 ? progs.SelectedItems[0].Tag as AwakeEntry : null;
            void UpdateButtons()
            {
                var e = Selected();
                ignore.Enabled = e != null && !e.Ignored && e.CanIgnore;
                unignore.Enabled = e != null && e.Ignored;
            }
            progs.SelectedIndexChanged += (_, _) => UpdateButtons();

            void ShowEntries(List<AwakeEntry> entries)
            {
                progs.Items.Clear();
                foreach (var e in entries)
                {
                    string status = e.Ignored && e.Requesting ? "Ignored (still asks, Windows disregards it)"
                                  : e.Ignored ? "Ignored"
                                  : e.CanIgnore ? "Keeping the display on"
                                  : "Keeping the display on (can't be ignored from here)";
                    progs.Items.Add(new ListViewItem(new[] { e.TypeLabel, e.Name, status }) { Tag = e });
                }
                if (entries.Count == 0)
                    progs.Items.Add(new ListViewItem(new[] { "", "Nothing is keeping the display awake right now." }));
                UpdateButtons();
            }

            async Task Run(string command)
            {
                check.Enabled = ignore.Enabled = unignore.Enabled = false;
                var entries = await CheckPowerRequests(command);
                if (form.IsDisposed) return;
                if (entries != null) ShowEntries(entries);
                check.Enabled = true;
                UpdateButtons();
            }

            check.Click += async (_, _) => await Run(null);
            ignore.Click += async (_, _) =>
            {
                var e = Selected();
                if (e == null) return;
                WakeLog.Add($"Ignoring {e.Name}'s request to keep the display on");
                await Run(e.IgnoreCommand());
            };
            unignore.Click += async (_, _) =>
            {
                var e = Selected();
                if (e == null) return;
                WakeLog.Add($"No longer ignoring {e.Name}");
                await Run(e.UnignoreCommand());
            };
            copy.Click += (_, _) =>
            {
                var lines = Array.ConvertAll(WakeLog.Snapshot(), e => e.Text);
                if (lines.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines));
            };
            clear.Click += (_, _) => WakeLog.Clear();
            hide.Click += (_, _) =>
            {
                if (box.SelectedItem is WakeLog.Entry e && e.Key != null)
                {
                    box.ClearSelected();
                    WakeLog.Hide(e.Key);
                }
            };
            hidden.Click += (_, _) => ShowHiddenInputs(form);

            form.Controls.Add(logPanel);
            form.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 5 });
            form.Controls.Add(progPanel);
            form.FormClosed += (_, _) => { WakeLog.Changed -= onChanged; _logForm = null; };
            _logForm = form;
            Theme.Apply(form);
            form.Show();
            form.Activate();
            RefreshLog();
        }

        // Lists the sources hidden from the wake log, so they can be shown again.
        static void ShowHiddenInputs(Form owner)
        {
            var dlg = new Form
            {
                Text = "Hidden inputs",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(620, 360),
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                Font = owner.Font
            };
            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended, FormattingEnabled = true };
            var note = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(8, 6, 8, 0),
                Text = "These aren't written to the wake log. Hiding only affects the log — it doesn't change what wakes the monitors."
            };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(6, 4, 6, 0) };
            var show = new Button { Text = "Show again", AutoSize = true, Enabled = false };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
            buttons.Controls.AddRange(new Control[] { show, close });

            void Fill()
            {
                list.Items.Clear();
                foreach (var key in WakeLog.HiddenKeys()) list.Items.Add(key);
                if (list.Items.Count == 0) list.Items.Add("(nothing hidden)");
                show.Enabled = false;
            }
            list.Format += (_, e) => e.Value = e.ListItem is string k && k != "(nothing hidden)" ? WakeLog.Describe(k) : e.ListItem;
            list.SelectedIndexChanged += (_, _) =>
                show.Enabled = list.SelectedItems.Count > 0 && !list.SelectedItems.Contains("(nothing hidden)");
            show.Click += (_, _) =>
            {
                var picked = new List<string>();
                foreach (var item in list.SelectedItems) if (item is string k) picked.Add(k);
                foreach (var k in picked) WakeLog.Unhide(k);
                Fill();
            };

            dlg.Controls.Add(list);
            dlg.Controls.Add(note);
            dlg.Controls.Add(buttons);
            dlg.AcceptButton = close;
            dlg.CancelButton = close;
            Fill();
            Theme.Apply(dlg);
            dlg.ShowDialog(owner);
        }

        // Something asking Windows to keep the display on (from "powercfg /requests"),
        // or a program already being ignored (from "powercfg /requestsoverride").
        sealed class AwakeEntry
        {
            public string Type;      // PROCESS, SERVICE or DRIVER (what powercfg calls it)
            public string Name;      // name to give powercfg: e.g. parsecd.exe
            public string OtherRequests = ""; // other request types already overridden for this name
            public bool Requesting;  // currently listed under DISPLAY
            public bool Ignored;     // DISPLAY is overridden

            public string TypeLabel => Type switch { "PROCESS" => "Program", "SERVICE" => "Service", "DRIVER" => "Driver", _ => Type };
            public bool CanIgnore => Type is "PROCESS" or "SERVICE" or "DRIVER";

            public string IgnoreCommand() =>
                $"powercfg /requestsoverride {Type} \"{Name}\" {(OtherRequests + " DISPLAY").Trim()}";

            // Keep any other overrides (e.g. SYSTEM) and drop only DISPLAY.
            public string UnignoreCommand() =>
                $"powercfg /requestsoverride {Type} \"{Name}\" {OtherRequests}".TrimEnd();
        }

        // Runs powercfg with admin rights (Windows asks each time). Optionally runs one
        // command first (an ignore/un-ignore), then lists who's keeping the display awake
        // and which overrides are in place. Results also go to the wake log.
        static async Task<List<AwakeEntry>> CheckPowerRequests(string firstCommand)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "SimpleMonitorOff-requests.txt");
            try { File.Delete(tmp); } catch { }
            string pre = firstCommand != null ? $"{firstCommand} > \"{tmp}.cmd\" 2>&1 & " : "";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c {pre}powercfg /requests > \"{tmp}\" 2>&1 & echo OVERRIDES: >> \"{tmp}\" & powercfg /requestsoverride >> \"{tmp}\" 2>&1")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null) await Task.Run(() => proc.WaitForExit(15000));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                WakeLog.Add("Program check cancelled (admin permission is needed for this).");
                return null;
            }

            if (firstCommand != null)
            {
                try
                {
                    string result = File.ReadAllText(tmp + ".cmd").Trim();
                    if (result.Length > 0) WakeLog.Add("  powercfg: " + result.Replace("\r", " ").Replace("\n", " "));
                    File.Delete(tmp + ".cmd");
                }
                catch { }
            }

            string text;
            try { text = File.ReadAllText(tmp); }
            catch { WakeLog.Add("Program check failed: no result from powercfg."); return null; }

            var entries = new List<AwakeEntry>();
            AwakeEntry Find(string type, string name) =>
                entries.Find(e => e.Type == type && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

            WakeLog.Add("── Programs and drivers asking Windows to stay awake ──");
            string section = "", overrideType = "";
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line == "None.") continue;

                if (line.EndsWith(":") && !line.StartsWith("[")) { section = line.TrimEnd(':'); continue; }

                if (section == "OVERRIDES")
                {
                    // Format: "[PROCESS]" header lines, then "name REQUEST REQUEST..."
                    if (line.StartsWith("[") && line.EndsWith("]")) { overrideType = line.Trim('[', ']'); continue; }
                    var words = new List<string>(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    var requests = new List<string>();
                    while (words.Count > 1 && IsRequestType(words[^1])) { requests.Insert(0, words[^1].ToUpperInvariant()); words.RemoveAt(words.Count - 1); }
                    string name = string.Join(" ", words).Trim('"');
                    bool display = requests.Remove("DISPLAY");
                    if (display)
                    {
                        var e = Find(overrideType, name) ?? new AwakeEntry { Type = overrideType, Name = name };
                        if (!entries.Contains(e)) entries.Add(e);
                        e.Ignored = true;
                        e.OtherRequests = string.Join(" ", requests);
                        WakeLog.Add($"  ignored (override in place) → {name}");
                    }
                    else if (requests.Count > 0)
                    {
                        var e = Find(overrideType, name);
                        if (e != null) e.OtherRequests = string.Join(" ", requests);
                    }
                    continue;
                }

                if (section != "DISPLAY")
                {
                    if (!line.StartsWith("[")) continue; // description lines under other sections
                    WakeLog.Add($"  {section.ToLowerInvariant()}: {line}");
                    continue;
                }

                // DISPLAY entries look like:
                //   [PROCESS] \Device\HarddiskVolume3\Program Files\Parsec\parsecd.exe
                //   [SERVICE] \Device\...\svchost.exe (ServiceName)
                //   [DRIVER] Device Name (USB\VID_...)
                if (!line.StartsWith("[")) continue;
                int close = line.IndexOf(']');
                if (close < 2) continue;
                string type = line[1..close].Trim().ToUpperInvariant();
                string rest = line[(close + 1)..].Trim();
                string nm = type switch
                {
                    "PROCESS" => Path.GetFileName(rest),
                    "SERVICE" when rest.EndsWith(")") && rest.LastIndexOf('(') >= 0 =>
                        rest[(rest.LastIndexOf('(') + 1)..^1],
                    "DRIVER" when rest.IndexOf(" (") > 0 => rest[..rest.IndexOf(" (")],
                    _ => rest
                };
                var entry = Find(type, nm) ?? new AwakeEntry { Type = type, Name = nm };
                if (!entries.Contains(entry)) entries.Add(entry);
                entry.Requesting = true;
                WakeLog.Add($"  KEEPS DISPLAY ON → {line}");
            }
            WakeLog.Add("── end of program check ──");
            return entries;
        }

        static bool IsRequestType(string w) =>
            w.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) || w.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
            w.Equals("AWAYMODE", StringComparison.OrdinalIgnoreCase) || w.Equals("EXECUTION", StringComparison.OrdinalIgnoreCase);

        // ---------- About window ----------

        Form _aboutForm;

        void ShowAbout()
        {
            if (_aboutForm != null) { _aboutForm.Activate(); return; }

            var form = new Form
            {
                Text = "About",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(20),
                Icon = _tray.Icon,
                TopMost = true,
                BackColor = Color.White
            };

            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
            var logo = new PictureBox
            {
                Image = RenderIcon(64, Color.Transparent),
                SizeMode = PictureBoxSizeMode.AutoSize,
                Margin = new Padding(0, 0, 16, 0)
            };
            var col = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown };

            col.Controls.Add(new Label
            {
                Text = "Simple Monitor Off",
                AutoSize = true,
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 2)
            });
            // Version comes from <Version> in MonitorOff.csproj
            var ver = typeof(Program).Assembly.GetName().Version ?? new Version(1, 0);
            col.Controls.Add(new Label
            {
                Text = $"Version {ver.Major}.{ver.Minor}" + (ver.Build > 0 ? $".{ver.Build}" : ""),
                AutoSize = true,
                Font = new Font("Segoe UI", 10f),
                ForeColor = Color.DimGray,
                Margin = new Padding(0, 0, 0, 10)
            });
            col.Controls.Add(new Label
            {
                Text = "By Patchs",
                AutoSize = true,
                Font = new Font("Segoe UI", 11f),
                Margin = new Padding(0, 0, 0, 6)
            });

            LinkLabel Link(string text, string target)
            {
                var l = new LinkLabel { Text = text, AutoSize = true, Font = new Font("Segoe UI", 11f), Margin = new Padding(0, 0, 0, 4) };
                l.LinkClicked += (_, _) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
                    catch { /* no browser or mail app set up */ }
                };
                return l;
            }
            col.Controls.Add(Link("mooredev3d@gmail.com", "mailto:mooredev3d@gmail.com"));

            var ok = new Button { Text = "OK", AutoSize = true, Margin = new Padding(0, 14, 0, 0), DialogResult = DialogResult.OK };
            ok.Click += (_, _) => form.Close();
            col.Controls.Add(ok);
            form.AcceptButton = ok;
            form.CancelButton = ok;

            row.Controls.Add(logo);
            row.Controls.Add(col);
            form.Controls.Add(row);
            form.FormClosed += (_, _) => _aboutForm = null;
            _aboutForm = form;
            Theme.Apply(form);
            form.Show();
            form.Activate();
        }

        // ---------- Monitors window ----------

        Form _monitorsForm;

        void ShowMonitors()
        {
            if (_monitorsForm != null) { _monitorsForm.Activate(); return; }

            var form = new Form
            {
                Text = "Monitor Off — Monitors",
                StartPosition = FormStartPosition.CenterScreen,
                Size = new Size(1080, 320),
                MinimizeBox = false,
                Icon = _tray.Icon,
                Font = new Font("Segoe UI", 10f)
            };
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            list.Columns.Add("#", 40);
            list.Columns.Add("Monitor", 210);
            list.Columns.Add("Connection", 150);
            list.Columns.Add("Resolution", 120);
            list.Columns.Add("DDC/CI", 90);
            list.Columns.Add("Power now", 90);
            list.Columns.Add("Power modes supported", 190);
            list.Columns.Add("", 90);

            var note = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 56,
                Padding = new Padding(8, 4, 8, 4),
                Text = "DDC/CI \"Active\" = the monitor answered just now. \"Not active\" = turn DDC/CI on in its menu " +
                       "(pen displays often don't support it). Power modes come from the monitor's own capability list: " +
                       "Standby is tried first by the Direct method, then Power off for monitors that wake themselves back up."
            };
            var refresh = new Button { Text = "Refresh", Dock = DockStyle.Bottom, Height = 34 };

            async void Load()
            {
                refresh.Enabled = false;
                list.Items.Clear();
                list.Items.Add(new ListViewItem(new[] { "", "Checking monitors…" }));
                var monitors = await Task.Run(() => Ddc.ListMonitors()); // DDC queries are slow; keep the UI responsive
                if (form.IsDisposed) return;
                list.Items.Clear();
                foreach (var m in monitors)
                    list.Items.Add(new ListViewItem(new[]
                        { m.WindowsNumber, m.Name, m.Connection, m.Resolution, m.Ddc, m.PowerNow, m.PowerModes,
                          m.Primary ? "Main display" : "" }));
                if (monitors.Count == 0)
                    list.Items.Add(new ListViewItem(new[] { "", "Couldn't read the display list." }));
                refresh.Enabled = true;
            }

            refresh.Click += (_, _) => Load();
            form.Controls.Add(list);
            form.Controls.Add(note);
            form.Controls.Add(refresh);
            form.FormClosed += (_, _) => _monitorsForm = null;
            _monitorsForm = form;
            Theme.Apply(form);
            form.Show();
            form.Activate();
            Load();
        }

        static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        // Picks the PC's address on the local network (no traffic is actually sent).
        // The PC's addresses on the home network, best guess first.
        // Real Wi-Fi/Ethernet adapters with a router come first; VPN and virtual
        // adapters (Hyper-V, WSL, VMware, VirtualBox…) go last since phones can't reach them.
        static List<string> LocalIps()
        {
            var scored = new List<(int Score, string Ip)>();
            string[] virtualWords = { "virtual", "hyper-v", "vethernet", "wsl", "vmware", "virtualbox",
                                      "vpn", "tap", "tun", "tailscale", "zerotier", "hamachi", "loopback", "bluetooth" };
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var props = ni.GetIPProperties();
                string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                bool isVirtual = Array.Exists(virtualWords, w => desc.Contains(w));
                bool hasGateway = false;
                foreach (var g in props.GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) hasGateway = true;

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // no-DHCP address, never reachable
                    bool isPrivate = b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
                    int score = (hasGateway ? 4 : 0) + (isVirtual ? 0 : 2) + (isPrivate ? 1 : 0);
                    scored.Add((score, ua.Address.ToString()));
                }
            }
            scored.Sort((a, b) => b.Score.CompareTo(a.Score));
            var list = scored.ConvertAll(s => s.Ip);
            if (list.Count == 0) list.Add(Dns.GetHostName());
            return list;
        }

        void ShowRemoteInfo()
        {
            _remoteForm?.Close();

            var ips = LocalIps();
            string scheme = _useHttps ? "https" : "http";
            string baseUrl = $"{scheme}://{ips[0]}:{RemoteServer.Port}/";
            string url = $"{baseUrl}#pin={_pin}";
            string certUrl = $"http://{ips[0]}:{RemoteServer.Port}/cert";

            static Image Qr(string text)
            {
                using var gen = new QRCodeGenerator();
                using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
                return Image.FromStream(new MemoryStream(new PngByteQRCode(data).GetGraphic(6)));
            }

            var form = new Form
            {
                Text = "Simple Monitor Off — Phone remote",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(16),
                TopMost = true,
                Icon = _tray.Icon,
                BackColor = Color.White
            };
            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            Label MakeLabel(string t, bool bold = false, int width = 600) => new Label
            {
                Text = t, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, 0, 0, 10),
                Font = new Font("Segoe UI", bold ? 12f : 10f, bold ? FontStyle.Bold : FontStyle.Regular)
            };

            panel.Controls.Add(MakeLabel("Connect your phone", true));
            panel.Controls.Add(MakeLabel("Your phone must be on the same Wi-Fi as this PC. Scan with the phone's camera."));

            // HTTPS: two steps side by side (trust the certificate once, then open the remote).
            // HTTP: just the remote.
            var steps = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
            FlowLayoutPanel Step(string title, string text, string link)
            {
                var col = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
                    Margin = new Padding(0, 0, 24, 0)
                };
                col.Controls.Add(MakeLabel(title, true, 280));
                col.Controls.Add(new PictureBox
                {
                    Image = Qr(link),
                    SizeMode = PictureBoxSizeMode.AutoSize,
                    Margin = new Padding(0, 0, 0, 8)
                });
                col.Controls.Add(MakeLabel(text, false, 280));
                return col;
            }
            if (_useHttps)
            {
                steps.Controls.Add(Step("1. Trust this PC (once)",
                    "Shows how to install this PC's certificate, so the phone doesn't warn \"not private\".\n" + certUrl, certUrl));
                steps.Controls.Add(Step("2. Open the remote",
                    $"Opens the secure remote with your PIN filled in.\n{baseUrl}\nPIN: {_pin}", url));
            }
            else
            {
                steps.Controls.Add(Step("Open the remote",
                    $"Opens the remote with your PIN filled in.\n{baseUrl}\nPIN: {_pin}", url));
            }
            panel.Controls.Add(steps);

            if (_useHttps)
                panel.Controls.Add(MakeLabel("Skipped step 1? The remote still works: on the warning page tap Advanced → Proceed."));
            else
                panel.Controls.Add(MakeLabel("Want an encrypted connection? Tray menu → Phone remote → Use HTTPS."));
            if (ips.Count > 1)
                panel.Controls.Add(MakeLabel("If the phone can't connect, try this PC's other addresses:\n" +
                    string.Join("\n", ips.GetRange(1, ips.Count - 1).ConvertAll(ip => $"  {scheme}://{ip}:{RemoteServer.Port}/"))));
            panel.Controls.Add(MakeLabel($"Test on this PC: {scheme}://localhost:{RemoteServer.Port}/ " +
                                         "— if that works but the phone can't connect, run allow-phone-remote.bat."));
            panel.Controls.Add(MakeLabel("Then add it to your home screen: Chrome ⋮ menu → Add to Home screen."));
            if (!_remoteEnabled.Checked)
                panel.Controls.Add(MakeLabel("Phone remote is currently turned OFF in the tray menu.", true));

            form.Controls.Add(panel);
            form.FormClosed += (_, _) => { if (_remoteForm == form) _remoteForm = null; };
            _remoteForm = form;
            Theme.Apply(form);
            form.Show();
            form.Activate();
        }

        // ---------- Settings ----------

        ToolStripMenuItem MakeToggle(string text, string name, bool def)
        {
            var item = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = ReadBool(name, def) };
            item.CheckedChanged += (_, _) =>
            {
                // Always keep at least one way to wake the monitors.
                if (!_wakeKey.Checked && !_wakeMove.Checked && !_wakeClick.Checked)
                {
                    item.Checked = true;
                    _tray.ShowBalloonTip(3000, "Monitor Off",
                        "At least one wake option must stay on.", ToolTipIcon.Info);
                    return;
                }
                WriteBool(name, item.Checked);
            };
            return item;
        }

        static bool ReadBool(string name, bool def)
        {
            using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return k?.GetValue(name) is int v ? v != 0 : def;
        }

        static void WriteBool(string name, bool value)
        {
            using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
            k.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }

        static int ReadInt(string name, int def)
        {
            using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return k?.GetValue(name) is int v ? v : def;
        }

        static void WriteInt(string name, int value)
        {
            using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
            k.SetValue(name, value, RegistryValueKind.DWord);
        }

        static string ReadString(string name)
        {
            using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return k?.GetValue(name) as string;
        }

        static void WriteString(string name, string value)
        {
            using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
            k.SetValue(name, value, RegistryValueKind.String);
        }

        static bool IsStartupEnabled()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) != null;
        }

        static void SetStartup(bool on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
            else k.DeleteValue(RunValue, false);
        }

        // ---------- Off / On ----------

        // Short delay so the click/release that triggered it doesn't immediately wake the screens.
        void ScheduleOff()
        {
            if (_off) return;
            _delay.Stop();
            _delay.Start();
        }

        void TurnOff(bool lockPc = false)
        {
            if (_off || _lockPending) return;

            if (lockPc)
            {
                if (Native.LockWorkStation())
                {
                    _lockedByUs = true;
                    _lockPending = true;
                    WakeLog.Add("Locked the PC");
                    _lockDelay.Stop();
                    _lockDelay.Start(); // continues in a moment, once the lock screen is up
                    return;
                }
                WakeLog.Add("Couldn't lock the PC — turning the monitors off without locking");
            }

            _off = true;
            _wakeRequested = false;
            _moveAccum = 0;
            Native.GetCursorPos(out var p);
            _offCursor = new Point(p.X, p.Y);

            // Snapshot the settings into plain fields for the hooks to read.
            _hookWakeKey = _wakeKey.Checked;
            _hookWakeMove = _wakeMove.Checked;
            _hookWakeClick = _wakeClick.Checked;

            IntPtr hMod = Native.GetModuleHandle(null);
            _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, hMod, 0);
            _msHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _msProc, hMod, 0);

            // Mute only if sound isn't already muted, so waking restores exactly what you had.
            // Volume to 0 (not mute, so a headset's mic stays on); remember the level to restore.
            if (_mute.Checked && Audio.GetVolume() is float vol && vol > 0f && Audio.SetVolume(0f))
            {
                _savedVolume = vol;
                _mutedByUs = true;
            }

            _offTick = Environment.TickCount64;
            _reasserts = 0;
            int session = ++_offSession;
            _lockInputBaseline = Native.LastInputTick();
            if (_lockedByUs) _lockWatch.Start();

            WakeLog.Add($"── Monitors turned off ({(_useDdc ? "Direct to monitors (DDC/CI)" : "Windows display sleep")}) ──");
            _powerWnd.StartRawInput();

            if (_useDdc)
            {
                // Tell each monitor directly to power down. Windows keeps thinking the
                // displays are on, so a DisplayPort monitor dropping off can't trigger a wake.
                _ddcActive = true;
                Task.Run(() =>
                {
                    var (ok, total, notOff) = Ddc.SetPower(false);
                    _ = WatchDdcMonitors(session, notOff);
                    if (notOff.Count == 0) return;
                    _ui.Post(_ =>
                    {
                        // Monitors that can't be switched off this way (e.g. the Cintiq)
                        // get a full-screen black cover instead.
                        if (_off && _ddcActive) ShowCovers(notOff);
                        if (!_ddcWarned)
                        {
                            _ddcWarned = true;
                            _tray.ShowBalloonTip(5000, "Monitor Off",
                                $"{notOff.Count} of {total} monitors can't be switched off directly (no DDC/CI), " +
                                "so they're covered with black instead.", ToolTipIcon.Info);
                        }
                    }, null);
                });
            }
            else
            {
                SendOff();
            }
        }

        // ---------- Black covers for monitors without DDC/CI ----------

        readonly List<BlackCover> _covers = new List<BlackCover>();

        void ShowCovers(List<Rectangle> areas)
        {
            HideCovers();
            foreach (var area in areas)
            {
                var cover = new BlackCover(area);
                cover.Show();
                _covers.Add(cover);
            }
        }

        void HideCovers()
        {
            foreach (var c in _covers) c.Close();
            _covers.Clear();
        }

        // Send the "monitors off" command to our own hidden window only.
        // (Broadcasting it to every window made slow apps re-send it late, which
        // caused lag and turned the screens back off after they had woken.)
        void SendOff() =>
            Native.PostMessage(_powerWnd.Handle, Native.WM_SYSCOMMAND,
                (IntPtr)Native.SC_MONITORPOWER, (IntPtr)2); // 2 = off

        // Windows tells us whenever the displays turn on or off.
        void OnDisplayState(int state) // 0 = off, 1 = on, 2 = dimmed
        {
            if (_off)
                WakeLog.Add("Windows: displays turned " + (state == 0 ? "OFF" : state == 1 ? "ON" : "dim"));
            if (!_off || _wakeRequested || state != 1 || _ddcActive) return;

            // Locked and someone pressed a key or moved the mouse on the lock screen:
            // that's a real wake, not a monitor waking itself, so don't turn them off again.
            if (_lockedByUs && Native.LastInputTick() != _lockInputBaseline)
            {
                WakeLog.Add("Woken by input on the lock screen");
                Wake();
                return;
            }

            // The screens came back on without a wake from us. Many monitors (especially
            // DisplayPort) briefly disconnect as they go to sleep, and Windows turns the
            // displays back on when it notices. Turn them off again for the first
            // few seconds; give up after a few tries so we never fight the monitor forever.
            bool unlimited = _maxReasserts < 0;
            if (unlimited || (Environment.TickCount64 - _offTick < ReassertWindowMs && _reasserts < _maxReasserts))
            {
                _reasserts++;
                WakeLog.Add($"Displays came back on without a wake from this app — turning them off again (try {_reasserts} of {(unlimited ? "unlimited" : _maxReasserts.ToString())})");
                _reassertTimer.Stop();
                _reassertTimer.Start(); // short pause lets the monitor finish reconnecting
            }
            else
            {
                // Woken some other way (e.g. Ctrl+Alt+Del): stop blocking input and restore sound.
                WakeLog.Add("Displays came back on without a wake from this app — giving up and treating it as a wake. " +
                            "Look at the lines just above for the cause.");
                Wake();
            }
        }

        void Wake()
        {
            if (_lockPending)
            {
                // Woken (e.g. from the phone) before the monitors even went off.
                _lockPending = false;
                _lockDelay.Stop();
                return;
            }
            if (!_off) return;
            _lockWatch.Stop();
            _off = false;
            _wakeRequested = false;
            _offSession++; // stops the Direct-mode watch loop
            _reassertTimer.Stop();
            _powerWnd.StopRawInput();
            WakeLog.Add("── Monitors woken ──");
            Unhook(); // stop swallowing input first, so the nudge below gets through
            RestoreSound();

            // Windows reliably turns displays back on for real input, not for a
            // "monitor on" message, so tell it the display is needed and inject
            // a 1-pixel mouse nudge (moved right back).
            Native.SetThreadExecutionState(Native.ES_DISPLAY_REQUIRED);
            Native.NudgeMouse();

            HideCovers();
            if (_ddcActive)
            {
                _ddcActive = false;
                // Power the monitors back on directly. Try again shortly after, in case a
                // monitor was still reconnecting and missed the first command.
                Task.Run(async () =>
                {
                    Ddc.SetPower(true);
                    await Task.Delay(1500);
                    Ddc.SetPower(true);
                });
            }
        }

        void RestoreSound()
        {
            if (_mutedByUs) { Audio.SetVolume(_savedVolume); _mutedByUs = false; }
        }

        void Unhook()
        {
            if (_kbHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
            if (_msHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_msHook); _msHook = IntPtr.Zero; }
        }

        // Queue the wake once; the hooks can fire hundreds of times while the mouse moves.
        void RequestWake(string reason)
        {
            if (_wakeRequested) return;
            _wakeRequested = true;
            WakeLog.Add("Woken by " + reason);
            _ui.Post(_ => Wake(), null);
        }

        // Direct mode: every few seconds, check whether any monitor switched itself back on
        // (some do when they still see a video signal), log it, and switch it off again.
        async Task WatchDdcMonitors(int session, List<Rectangle> covered)
        {
            var names = new Dictionary<Rectangle, string>();
            try { foreach (var m in Ddc.ListMonitors(probeDdc: false)) names[m.Area] = m.Name; } catch { }
            var tries = new Dictionary<Rectangle, int>();
            var skip = new List<Rectangle>(covered); // covered monitors, plus ones we've given up on

            while (true)
            {
                await Task.Delay(3000).ConfigureAwait(false);
                if (session != _offSession || !_off || !_ddcActive) return;
                if (_maxReasserts == 0) return; // "Leave them on"

                foreach (var area in Ddc.TurnOffAnyBackOn(skip))
                {
                    if (session != _offSession) return;
                    string name = names.TryGetValue(area, out var n) ? n : "A monitor";
                    int tryNo = tries[area] = tries.TryGetValue(area, out int t) ? t + 1 : 1;
                    bool unlimited = _maxReasserts < 0;
                    WakeLog.AddThrottled("ddcback:" + name,
                        $"{name} turned itself back on — switching it off again " +
                        $"(try {tryNo} of {(unlimited ? "unlimited" : _maxReasserts.ToString())})", 1000);
                    if (!unlimited && tryNo >= _maxReasserts)
                    {
                        skip.Add(area);
                        WakeLog.Add($"{name} keeps turning itself back on — leaving it on. Turning off DP deep sleep / " +
                                    "auto input switching in its menu may fix this.");
                    }
                }
            }
        }

        // ---------- Low-level hooks (only installed while monitors are off) ----------
        // These run for every key and mouse event system-wide, so they only read
        // plain fields and return immediately; anything slow here lags the whole PC.

        IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _off)
            {
                int msg = (int)wParam;
                bool keyDown = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                if (keyDown)
                {
                    // KBDLLHOOKSTRUCT: vkCode @0, flags @8 (0x10 = injected by software)
                    int vk = Marshal.ReadInt32(lParam);
                    string inj = (Marshal.ReadInt32(lParam, 8) & 0x10) != 0 ? " (injected by software)" : "";
                    if (_hookWakeKey) RequestWake($"key press: {(Keys)vk}{inj}");
                    else WakeLog.AddThrottled($"key:{(Keys)vk}{inj}", $"Blocked key press: {(Keys)vk}{inj}");
                }
                return (IntPtr)1; // swallow so the wake key isn't typed into an app
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _off)
            {
                int msg = (int)wParam;
                // MSLLHOOKSTRUCT flags @12: 0x01 = injected by software
                string inj = (Marshal.ReadInt32(lParam, 12) & 0x01) != 0 ? " (injected by software)" : "";
                if (msg == Native.WM_MOUSEMOVE)
                {
                    WakeLog.AddThrottled("move" + inj, "Mouse movement" + inj + (_hookWakeMove ? "" : " — blocked"));
                    if (_hookWakeMove)
                    {
                        int x = Marshal.ReadInt32(lParam);
                        int y = Marshal.ReadInt32(lParam, 4);
                        uint now = (uint)Environment.TickCount;
                        if (now - _lastMoveTick > MoveResetMs) _moveAccum = 0; // paused: start over
                        _lastMoveTick = now;
                        _moveAccum += Math.Abs(x - _offCursor.X) + Math.Abs(y - _offCursor.Y);
                        if (_moveAccum >= _moveThreshold) RequestWake("mouse movement" + inj);
                    }
                }
                else if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN ||
                         msg == Native.WM_MBUTTONDOWN || msg == Native.WM_XBUTTONDOWN)
                {
                    if (_hookWakeClick) RequestWake("mouse click" + inj);
                    else WakeLog.AddThrottled("click" + inj, "Blocked mouse click" + inj);
                }
                return (IntPtr)1; // swallow all mouse input while off
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // ---------- Misc ----------

        void Quit()
        {
            _server.Stop();
            Unhook();
            _powerWnd.DestroyHandle();
            RestoreSound();
            _tray.Visible = false;
            _tray.Dispose();
            ExitThread();
        }

        // Tray icon, drawn in code so no .ico file is needed.
        static Icon MakeIcon()
        {
            using var bmp = RenderIcon(32, Color.Transparent);
            return Icon.FromHandle(bmp.GetHicon());
        }

        // PNG of the same icon for the phone's home screen.
        static byte[] IconPng(int size)
        {
            using var bmp = RenderIcon(size, Color.FromArgb(27, 27, 27));
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        // Draws the monitor-with-rainbow at any size (designed on a 32×32 grid).
        static Bitmap RenderIcon(int size, Color background)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(background);
                g.ScaleTransform(size / 32f, size / 32f);
                using var frame = new SolidBrush(Color.FromArgb(210, 210, 210));
                using var screen = new SolidBrush(Color.FromArgb(4, 4, 6));
                g.FillRectangle(frame, 2, 4, 28, 19);   // bezel
                g.FillRectangle(screen, 4, 6, 24, 15);  // screen
                g.FillRectangle(frame, 13, 23, 6, 4);   // neck
                g.FillRectangle(frame, 8, 27, 16, 3);   // base

                // Grayscale rainbow arching up from the bottom of the screen (light outside → dark inside)
                Color[] bands =
                {
                    Color.FromArgb(60, 60, 60),
                    Color.FromArgb(50, 50, 50),
                    Color.FromArgb(41, 41, 41),
                    Color.FromArgb(33, 33, 33),
                    Color.FromArgb(25, 25, 25),
                    Color.FromArgb(18, 18, 18),
                };
                g.SetClip(new Rectangle(4, 6, 24, 15));
                const float cx = 16f, cy = 21f, outer = 13f, width = 2f;
                for (int i = 0; i < bands.Length; i++)
                {
                    float r = outer - i * width - width / 2f;
                    using var pen = new Pen(bands[i], width + 0.4f); // slight overlap, no gaps
                    g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 180, 180);
                }
                g.ResetClip();
            }
            return bmp;
        }
    }

    // Light / dark styling for the app's windows and tray menu.
    // Mode: 0 = follow Windows' app setting, 1 = light, 2 = dark.
    static class Theme
    {
        public static int Mode;

        public static bool IsDark => Mode switch
        {
            1 => false,
            2 => true,
            _ => WindowsUsesDark()
        };

        // Dark palette
        public static readonly Color Back = Color.FromArgb(32, 32, 36);
        public static readonly Color Surface = Color.FromArgb(43, 43, 48);
        public static readonly Color Border = Color.FromArgb(70, 70, 78);
        public static readonly Color Text = Color.FromArgb(236, 236, 240);
        public static readonly Color Dim = Color.FromArgb(165, 165, 175);
        public static readonly Color Link = Color.FromArgb(130, 180, 255);
        public static readonly Color Hover = Color.FromArgb(60, 60, 68);

        static bool WindowsUsesDark()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        // ----- windows -----

        public static void Apply(Form form)
        {
            bool dark = IsDark;
            SetDarkTitleBar(form, dark);

            // Remember each control's original colors once, so switching back to light restores them.
            Walk(form, c =>
            {
                if (c.Tag is not ThemeMemo) c.Tag = new ThemeMemo(c.BackColor, c.ForeColor, c.Tag);
            });
            Walk(form, c => Style(c, dark));
            form.Invalidate(true);
        }

        sealed class ThemeMemo
        {
            public readonly Color Back, Fore;
            public readonly object OriginalTag;
            public ThemeMemo(Color back, Color fore, object tag) { Back = back; Fore = fore; OriginalTag = tag; }
        }

        static void Walk(Control c, Action<Control> action)
        {
            action(c);
            foreach (Control child in c.Controls) Walk(child, action);
        }

        static void Style(Control c, bool dark)
        {
            var memo = (ThemeMemo)c.Tag;
            switch (c)
            {
                case PictureBox:
                    return; // logos and QR codes keep their own colors (a QR code needs its white border)

                case Button b:
                    if (dark)
                    {
                        b.FlatStyle = FlatStyle.Flat;
                        b.FlatAppearance.BorderColor = Border;
                        b.FlatAppearance.MouseOverBackColor = Hover;
                        b.BackColor = Surface;
                        b.ForeColor = Text;
                    }
                    else
                    {
                        b.FlatStyle = FlatStyle.Standard;
                        b.UseVisualStyleBackColor = true;
                        b.ForeColor = memo.Fore;
                    }
                    return;

                case LinkLabel l:
                    l.LinkColor = dark ? Link : Color.FromArgb(0, 102, 204);
                    l.ActiveLinkColor = dark ? Color.White : Color.Red;
                    l.BackColor = dark ? Color.Transparent : memo.Back;
                    return;

                case ListView lv:
                    lv.BackColor = dark ? Surface : memo.Back;
                    lv.ForeColor = dark ? Text : memo.Fore;
                    SetControlTheme(lv.Handle, dark ? "DarkMode_Explorer" : "Explorer");
                    IntPtr header = SendMessage(lv.Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
                    if (header != IntPtr.Zero) SetControlTheme(header, dark ? "DarkMode_ItemsView" : "ItemsView");
                    return;

                case TextBoxBase or ListBox:
                    c.BackColor = dark ? Surface : memo.Back;
                    c.ForeColor = dark ? Text : memo.Fore;
                    if (c.IsHandleCreated) SetControlTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer");
                    else c.HandleCreated += (_, _) => SetControlTheme(c.Handle, IsDark ? "DarkMode_Explorer" : "Explorer");
                    return;

                case Splitter:
                    c.BackColor = dark ? Border : memo.Back;
                    return;

                case Label:
                    // Labels that were set to a grey "note" color stay dimmer than normal text.
                    c.ForeColor = dark ? (memo.Fore == Color.DimGray ? Dim : Text) : memo.Fore;
                    c.BackColor = dark ? Color.Transparent : memo.Back;
                    return;

                case Form:
                    c.BackColor = dark ? Back : memo.Back;
                    c.ForeColor = dark ? Text : memo.Fore;
                    return;

                default: // panels and layout containers take the window's background
                    c.BackColor = dark ? Back : memo.Back;
                    c.ForeColor = dark ? Text : memo.Fore;
                    return;
            }
        }

        static void SetDarkTitleBar(Form form, bool dark)
        {
            void Set()
            {
                int on = dark ? 1 : 0;
                DwmSetWindowAttribute(form.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
            }
            if (form.IsHandleCreated) Set();
            else form.HandleCreated += (_, _) => Set();
        }

        static void SetControlTheme(IntPtr handle, string theme)
        {
            try { SetWindowTheme(handle, theme, null); } catch { }
        }

        // ----- tray menu -----

        public static void ApplyMenu(ToolStrip menu)
        {
            if (menu == null) return;
            bool dark = IsDark;
            menu.Renderer = dark ? new DarkMenuRenderer() : new ToolStripProfessionalRenderer();
            void Colorize(ToolStripItemCollection items)
            {
                foreach (ToolStripItem item in items)
                {
                    item.ForeColor = dark ? Text : SystemColors.ControlText;
                    if (item is ToolStripMenuItem mi && mi.HasDropDownItems)
                    {
                        mi.DropDown.Renderer = menu.Renderer;
                        mi.DropDown.BackColor = dark ? Surface : SystemColors.Menu;
                        Colorize(mi.DropDownItems);
                    }
                }
            }
            menu.BackColor = dark ? Surface : SystemColors.Menu;
            Colorize(menu.Items);
        }

        sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Text : Color.FromArgb(110, 110, 118);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Text;
                base.OnRenderArrow(e);
            }

            // Draw our own light check mark; the default one is black.
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var r = e.ImageRectangle;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var bg = new SolidBrush(Hover);
                g.FillRectangle(bg, r);
                using var pen = new Pen(Text, Math.Max(1.6f, r.Height / 9f));
                float x = r.X, y = r.Y, w = r.Width, h = r.Height;
                g.DrawLines(pen, new[]
                {
                    new PointF(x + w * 0.25f, y + h * 0.52f),
                    new PointF(x + w * 0.43f, y + h * 0.70f),
                    new PointF(x + w * 0.76f, y + h * 0.32f)
                });
            }
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Surface;
            public override Color ImageMarginGradientBegin => Surface;
            public override Color ImageMarginGradientMiddle => Surface;
            public override Color ImageMarginGradientEnd => Surface;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
            public override Color CheckBackground => Hover;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    // Borderless, always-on-top black window covering one monitor. It never takes
    // focus and doesn't show in the taskbar or Alt+Tab.
    sealed class BlackCover : Form
    {
        readonly Rectangle _area;

        public BlackCover(Rectangle area)
        {
            _area = area;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.Black;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = area;
            TopMost = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000008 /* WS_EX_TOPMOST */;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Place it in exact screen pixels (moving between monitors with different
            // scaling can make Windows resize a window).
            SetWindowPos(Handle, (IntPtr)(-1) /* HWND_TOPMOST */, _area.X, _area.Y, _area.Width, _area.Height,
                         0x0010 /* SWP_NOACTIVATE */ | 0x0040 /* SWP_SHOWWINDOW */);
        }

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }

    // Talks to monitors directly over DDC/CI (the control channel in the video cable).
    // VCP code 0xD6 is "power mode": 1 = on, 4 = off/standby.
    static class Ddc
    {
        static readonly object Gate = new object(); // don't overlap on/off commands

        // Returns how many physical monitors accepted the command, out of how many found.
        // Power mode values for VCP 0xD6
        const uint PowerOn = 1, Standby = 4, PowerOff = 5;

        // NotOff = screen areas of monitors that didn't accept the power-off (e.g. a Cintiq).
        public static (int Ok, int Total, List<Rectangle> NotOff) SetPower(bool on)
        {
            lock (Gate)
            {
                int ok = 0, total = 0;
                var notOff = new List<Rectangle>();

                if (on)
                {
                    ForEachMonitor((m, _) =>
                    {
                        total++;
                        // Skip monitors that already report "on": some (e.g. BenQ)
                        // blank and re-sync whenever they're sent the power-on command.
                        if (ReadPower(m) == PowerOn || Send(m, PowerOn)) ok++;
                    });
                    return (ok, total, notOff);
                }

                // Off, step 1: standby. Works for most monitors (e.g. the ASUS).
                ForEachMonitor(
                    (m, area) =>
                    {
                        total++;
                        // A monitor that doesn't answer DDC/CI (like the Cintiq) may still claim the
                        // command went through, so judge it by whether it answered first.
                        bool answers = ReadPower(m) != 0;
                        bool sent = Send(m, Standby);
                        if (answers && sent) ok++;
                        else if (!notOff.Contains(area)) notOff.Add(area);
                    },
                    area => { total++; notOff.Add(area); }); // no DDC/CI handle at all

                // Step 2: some monitors (e.g. BenQ) leave standby by themselves because the
                // video signal is still there. Check each one after a moment, and send the
                // stronger "power off" (like pressing its power button) to any that came back.
                Thread.Sleep(2000);
                ForEachMonitor((m, area) => { if (!notOff.Contains(area) && ReadPower(m) == PowerOn) Send(m, PowerOff); });

                return (ok, total, notOff);
            }
        }

        // Direct mode: any monitor that reports "on" again (outside the covered ones) gets
        // "power off" again. Returns the screen areas of monitors that had turned back on.
        public static List<Rectangle> TurnOffAnyBackOn(ICollection<Rectangle> skip)
        {
            var back = new List<Rectangle>();
            lock (Gate)
            {
                ForEachMonitor((m, area) =>
                {
                    if (skip.Contains(area) || back.Contains(area)) return;
                    if (ReadPower(m) == PowerOn) { back.Add(area); Send(m, PowerOff); }
                });
            }
            return back;
        }

        static bool Send(IntPtr monitor, uint mode)
        {
            if (SetVCPFeature(monitor, 0xD6, mode)) return true;
            Thread.Sleep(100); // DDC/CI is slow and sometimes drops a command; retry once
            return SetVCPFeature(monitor, 0xD6, mode);
        }

        // Current power mode, or 0 if the monitor didn't answer (often the case when it's off).
        static uint ReadPower(IntPtr monitor) =>
            GetVCPFeatureAndVCPFeatureReply(monitor, 0xD6, IntPtr.Zero, out uint current, out _) ? current : 0;

        // Calls action(physical monitor handle, its screen area) for every monitor;
        // noDdc(area) for screens Windows can't give a DDC/CI handle for.
        static void ForEachMonitor(Action<IntPtr, Rectangle> action, Action<Rectangle> noDdc = null)
        {
            var monitors = new List<(IntPtr Handle, Rectangle Area)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr data) =>
            {
                monitors.Add((h, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)));
                return true;
            }, IntPtr.Zero);

            foreach (var (hMon, area) in monitors)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0) { noDdc?.Invoke(area); continue; }
                var phys = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, phys)) { noDdc?.Invoke(area); continue; }
                try { foreach (var p in phys) action(p.hPhysicalMonitor, area); }
                finally { DestroyPhysicalMonitors(count, phys); }
            }
        }

        // ---------- Monitor list (for the "Monitors…" window) ----------

        public sealed class MonitorInfo
        {
            public string Name, Connection, Resolution, Ddc, WindowsNumber;
            public string PowerNow = "", PowerModes = "";
            public bool Primary;
            public Rectangle Area;
        }

        // Lists active monitors with their connection type (from Windows' display
        // configuration) and whether they answer DDC/CI power queries.
        public static List<MonitorInfo> ListMonitors(bool probeDdc = true)
        {
            var result = new List<MonitorInfo>();

            // GDI device name (\\.\DISPLAY1) → monitor handle, size, primary flag
            var gdiToMon = new Dictionary<string, (IntPtr Handle, RECT Rect, bool Primary)>(StringComparer.OrdinalIgnoreCase);
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT rc, IntPtr d) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(h, ref mi)) gdiToMon[mi.szDevice] = (h, mi.rcMonitor, (mi.dwFlags & 1) != 0);
                return true;
            }, IntPtr.Zero);

            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            const int PathSize = 72, ModeSize = 64;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes) != 0) return result;
            IntPtr paths = Marshal.AllocHGlobal((int)numPaths * PathSize);
            IntPtr modes = Marshal.AllocHGlobal((int)Math.Max(1, numModes) * ModeSize);
            try
            {
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0)
                    return result;

                for (int i = 0; i < numPaths; i++)
                {
                    IntPtr p = paths + i * PathSize;
                    int srcLo = Marshal.ReadInt32(p, 0), srcHi = Marshal.ReadInt32(p, 4), srcId = Marshal.ReadInt32(p, 8);
                    int tgtLo = Marshal.ReadInt32(p, 20), tgtHi = Marshal.ReadInt32(p, 24), tgtId = Marshal.ReadInt32(p, 28);
                    uint tech = (uint)Marshal.ReadInt32(p, 36);

                    // Monitor's own name (from its EDID) and connection type
                    // DISPLAYCONFIG_TARGET_DEVICE_NAME layout: header (20 bytes), flags @20,
                    // outputTechnology @24, EDID maker/product @28, connectorInstance @32, name @36
                    string name = "";
                    int connector = -1;
                    string target = DeviceInfo(2 /* GET_TARGET_NAME */, 420, tgtLo, tgtHi, tgtId, pk =>
                    {
                        tech = (uint)Marshal.ReadInt32(pk, 24);
                        connector = Marshal.ReadInt32(pk, 32);
                        return Marshal.PtrToStringUni(pk + 36); // monitorFriendlyDeviceName
                    });
                    if (!string.IsNullOrWhiteSpace(target)) name = target;

                    // Windows' device name for the source, to match up with the monitor handle
                    string gdi = DeviceInfo(1 /* GET_SOURCE_NAME */, 84, srcLo, srcHi, srcId,
                                            pk => Marshal.PtrToStringUni(pk + 20)) ?? "";

                    var info = new MonitorInfo
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? "(unnamed monitor)" : name,
                        // e.g. "DisplayPort (port 2)": which port of that type on the graphics card.
                        // Windows numbers them from 1, and uses 0 when the card has only one of that type.
                        Connection = ConnectionName(tech) + (connector > 0 && IsPhysicalPort(tech) ? $" (port {connector})" : ""),
                        WindowsNumber = new string(Array.FindAll(gdi.ToCharArray(), char.IsDigit)),
                        Resolution = "",
                        Ddc = "—"
                    };
                    if (gdiToMon.TryGetValue(gdi, out var mon))
                    {
                        info.Resolution = $"{mon.Rect.Right - mon.Rect.Left} × {mon.Rect.Bottom - mon.Rect.Top}";
                        info.Primary = mon.Primary;
                        info.Area = Rectangle.FromLTRB(mon.Rect.Left, mon.Rect.Top, mon.Rect.Right, mon.Rect.Bottom);
                        if (probeDdc)
                        {
                            var (ddcOn, powerNow, powerModes) = ProbeDdc(mon.Handle);
                            info.Ddc = ddcOn ? "Active" : "Not active";
                            info.PowerNow = powerNow;
                            info.PowerModes = powerModes;
                        }
                    }
                    result.Add(info);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(paths);
                Marshal.FreeHGlobal(modes);
            }
            return result;
        }

        // Fills a DISPLAYCONFIG_DEVICE_INFO_HEADER packet, asks Windows, and reads the reply.
        static string DeviceInfo(int type, int size, int adapterLo, int adapterHi, int id, Func<IntPtr, string> read)
        {
            IntPtr pk = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, pk, size);
                Marshal.WriteInt32(pk, 0, type);
                Marshal.WriteInt32(pk, 4, size);
                Marshal.WriteInt32(pk, 8, adapterLo);
                Marshal.WriteInt32(pk, 12, adapterHi);
                Marshal.WriteInt32(pk, 16, id);
                return DisplayConfigGetDeviceInfo(pk) == 0 ? read(pk) : null;
            }
            finally { Marshal.FreeHGlobal(pk); }
        }

        static bool IsPhysicalPort(uint tech) => tech is 4 or 5 or 10 or 18;

        static string ConnectionName(uint tech) => tech switch
        {
            0 => "VGA",
            1 => "S-Video",
            2 => "Composite",
            3 => "Component",
            4 => "DVI",
            5 => "HDMI",
            6 => "Built-in (LVDS)",
            8 => "D-Jpn",
            9 => "SDI",
            10 => "DisplayPort",
            11 => "Built-in (eDP)",
            12 => "UDI",
            13 => "Built-in (UDI)",
            14 => "SDTV dongle",
            15 => "Miracast (wireless)",
            16 => "Indirect (wired)",
            17 => "Virtual display",
            18 => "USB-C (DisplayPort)",
            0x80000000 => "Built-in",
            _ => "Other"
        };

        // True if the monitor answers a DDC/CI power-mode query.
        // Asks the monitor over DDC/CI: is DDC/CI answering, what power state is it in,
        // and which power commands does it say it supports (from its capabilities list).
        static (bool Active, string PowerNow, string Modes) ProbeDdc(IntPtr hMon)
        {
            lock (Gate)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0) return (false, "", "");
                var phys = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, phys)) return (false, "", "");
                try
                {
                    var h = phys[0].hPhysicalMonitor;
                    bool active = GetVCPFeatureAndVCPFeatureReply(h, 0xD6, IntPtr.Zero, out uint current, out _);

                    string caps = null;
                    if (GetCapabilitiesStringLength(h, out uint len) && len > 0)
                    {
                        var sb = new StringBuilder((int)len);
                        if (CapabilitiesRequestAndCapabilitiesReply(h, sb, len)) caps = sb.ToString();
                    }
                    if (!active && caps == null) return (false, "", "");

                    return (true, active ? PowerName(current) : "?", PowerModesFromCaps(caps));
                }
                finally { DestroyPhysicalMonitors(count, phys); }
            }
        }

        static string PowerName(uint v) => v switch
        {
            1 => "On",
            2 or 3 or 4 => "Standby",
            5 => "Off",
            _ => $"Unknown ({v})"
        };

        // Capabilities strings list supported VCP codes like "... D6(01 04 05) ...".
        static string PowerModesFromCaps(string caps)
        {
            if (caps == null) return "Didn't say";
            var m = System.Text.RegularExpressions.Regex.Match(caps, @"\bD6\s*\(([^)]*)\)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success)
                return System.Text.RegularExpressions.Regex.IsMatch(caps, @"\bD6\b",
                           System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    ? "Power control (modes not listed)" : "No power control listed";

            var names = new List<string>();
            foreach (var tok in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!uint.TryParse(tok, System.Globalization.NumberStyles.HexNumber, null, out uint v)) continue;
                string n = v switch { 1 => null, 2 or 3 or 4 => "Standby", 5 => "Power off", _ => null };
                if (n != null && !names.Contains(n)) names.Add(n);
            }
            return names.Count > 0 ? string.Join(", ", names) : "On only";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("user32.dll")]
        static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")]
        static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, IntPtr topology);
        [DllImport("user32.dll")]
        static extern int DisplayConfigGetDeviceInfo(IntPtr packet);

        // ---------- shared interop ----------

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint newValue);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint length);
        [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, StringBuilder caps, uint length);
        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcpCode, IntPtr codeType,
                                                            out uint currentValue, out uint maximumValue);
    }

    // Timestamped record of what happens while the monitors are off, so you can see
    // what woke them. Thread-safe; keeps the last 500 lines. Lines can carry a "source key"
    // (a device, mouse movement, …) so noisy sources can be hidden; hidden sources are saved.
    static class WakeLog
    {
        const string SettingsKey = @"Software\MonitorOffTray";

        public sealed class Entry
        {
            public long Id;
            public string Text;
            public string Key; // null = always shown (e.g. what woke the monitors)
            public override string ToString() => Text;
        }

        static readonly object Lock = new object();
        static readonly List<Entry> Lines = new List<Entry>();
        static readonly Dictionary<string, long> LastByKey = new Dictionary<string, long>();
        static readonly HashSet<string> Hidden = LoadHidden();
        static long _nextId;

        public static event Action Changed; // may fire on any thread

        public static void Add(string text, string key = null)
        {
            lock (Lock)
            {
                if (key != null && Hidden.Contains(key)) return;
                Lines.Add(new Entry { Id = ++_nextId, Text = $"{DateTime.Now:HH:mm:ss.f}  {text}", Key = key });
                if (Lines.Count > 500) Lines.RemoveRange(0, Lines.Count - 500);
            }
            Changed?.Invoke();
        }

        // For things that repeat rapidly (mouse movement): log at most once per key per interval.
        public static void AddThrottled(string key, string text, int intervalMs = 2000)
        {
            long now = Environment.TickCount64;
            lock (Lock)
            {
                if (Hidden.Contains(key)) return;
                if (LastByKey.TryGetValue(key, out long last) && now - last < intervalMs) return;
                LastByKey[key] = now;
            }
            Add(text, key);
        }

        public static Entry[] Snapshot() { lock (Lock) return Lines.ToArray(); }

        public static void Clear()
        {
            lock (Lock) { Lines.Clear(); LastByKey.Clear(); }
            Changed?.Invoke();
        }

        // ----- hidden sources -----

        public static void Hide(string key)
        {
            if (key == null) return;
            lock (Lock)
            {
                Hidden.Add(key);
                Lines.RemoveAll(e => e.Key == key);
            }
            SaveHidden();
            Changed?.Invoke();
        }

        public static void Unhide(string key)
        {
            lock (Lock) Hidden.Remove(key);
            SaveHidden();
            Changed?.Invoke();
        }

        public static List<string> HiddenKeys()
        {
            List<string> list;
            lock (Lock) list = new List<string>(Hidden);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // Readable name for a source key, for the "Hidden inputs" list.
        public static string Describe(string key)
        {
            if (key.StartsWith("raw:")) return "Input from " + key[4..];
            if (key.StartsWith("key:")) return "Key press: " + key[4..];
            if (key.StartsWith("move")) return "Mouse movement" + key[4..];
            if (key.StartsWith("click")) return "Mouse clicks" + key[5..];
            if (key.StartsWith("ddcback:")) return key[8..] + " turning itself back on";
            return key switch
            {
                "devchange" => "Device connected/disconnected notices",
                "layout" => "Display layout changes",
                _ => key
            };
        }

        static HashSet<string> LoadHidden()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
                if (k?.GetValue("HiddenInputs") is string[] saved) set.UnionWith(saved);
            }
            catch { }
            return set;
        }

        static void SaveHidden()
        {
            try
            {
                string[] keys;
                lock (Lock) keys = new List<string>(Hidden).ToArray();
                using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
                k.SetValue("HiddenInputs", keys, RegistryValueKind.MultiString);
            }
            catch { }
        }
    }

    // Hidden window: target for the monitor-off command, and receiver of Windows'
    // "display turned on/off" notifications.
    sealed class PowerWindow : NativeWindow
    {
        const int WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
        static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
        IntPtr _notify;

        public event Action<int> DisplayStateChanged;
        public event Action<IntPtr, int> RawInputReceived; // device handle (0 = injected by software), type
        public event Action DisplayLayoutChanged;          // a monitor was connected/disconnected/reconfigured
        public event Action DevicesChanged;                // a USB/hardware device appeared or disappeared

        const int WM_INPUT = 0x00FF, WM_DISPLAYCHANGE = 0x007E, WM_DEVICECHANGE = 0x0219, DBT_DEVNODES_CHANGED = 0x0007;
        const uint RIDEV_REMOVE = 0x1, RIDEV_INPUTSINK = 0x100, RID_HEADER = 0x10000005;

        // Usage page/usage pairs: mouse, keyboard, joystick, gamepad, media keys, pen, touch screen, touchpad
        static readonly (ushort Page, ushort Usage)[] RawTypes =
            { (0x01, 0x02), (0x01, 0x06), (0x01, 0x04), (0x01, 0x05), (0x0C, 0x01), (0x0D, 0x02), (0x0D, 0x04), (0x0D, 0x05) };

        // Watch all input devices (only while the monitors are off). Each type is registered
        // separately so one unsupported type doesn't stop the rest.
        public void StartRawInput()
        {
            foreach (var (page, usage) in RawTypes)
            {
                var dev = new[] { new RAWINPUTDEVICE { usUsagePage = page, usUsage = usage, dwFlags = RIDEV_INPUTSINK, hwndTarget = Handle } };
                RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
        }

        public void StopRawInput()
        {
            foreach (var (page, usage) in RawTypes)
            {
                var dev = new[] { new RAWINPUTDEVICE { usUsagePage = page, usUsage = usage, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero } };
                RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
        }

        public void ListenForDisplayState()
        {
            var g = GUID_CONSOLE_DISPLAY_STATE;
            _notify = RegisterPowerSettingNotification(Handle, ref g, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                // POWERBROADCAST_SETTING: Guid (16 bytes), DataLength (4), Data
                var guid = Marshal.PtrToStructure<Guid>(m.LParam);
                if (guid == GUID_CONSOLE_DISPLAY_STATE)
                    DisplayStateChanged?.Invoke(Marshal.ReadInt32(m.LParam, 20));
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_INPUT)
            {
                // RAWINPUTHEADER: dwType (4), dwSize (4), hDevice (pointer), wParam (pointer)
                int headerSize = 8 + 2 * IntPtr.Size;
                IntPtr header = Marshal.AllocHGlobal(headerSize);
                try
                {
                    uint size = (uint)headerSize;
                    if (GetRawInputData(m.LParam, RID_HEADER, header, ref size, (uint)headerSize) != unchecked((uint)-1))
                        RawInputReceived?.Invoke(Marshal.ReadIntPtr(header, 8), Marshal.ReadInt32(header, 0));
                }
                finally { Marshal.FreeHGlobal(header); }
            }
            else if (m.Msg == WM_DISPLAYCHANGE) DisplayLayoutChanged?.Invoke();
            else if (m.Msg == WM_DEVICECHANGE && (int)m.WParam == DBT_DEVNODES_CHANGED) DevicesChanged?.Invoke();
            base.WndProc(ref m);
        }

        // ----- naming input devices for the log -----

        static readonly Dictionary<IntPtr, string> NameCache = new Dictionary<IntPtr, string>();

        public static string DeviceName(IntPtr hDevice, int type)
        {
            if (hDevice == IntPtr.Zero) return "software (injected input, no physical device)";
            if (NameCache.TryGetValue(hDevice, out var cached)) return cached;

            string kind = type switch { 0 => "mouse", 1 => "keyboard", _ => "HID device" };
            string path = null;
            uint chars = 0;
            GetRawInputDeviceInfo(hDevice, 0x20000007 /* RIDI_DEVICENAME */, null, ref chars);
            if (chars > 0)
            {
                var sb = new StringBuilder((int)chars + 1);
                if (GetRawInputDeviceInfo(hDevice, 0x20000007, sb, ref chars) != unchecked((uint)-1)) path = sb.ToString();
            }

            string product = path != null ? ProductName(path) : null;
            string ids = "";
            if (path != null)
            {
                var vid = System.Text.RegularExpressions.Regex.Match(path, @"VID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var pid = System.Text.RegularExpressions.Regex.Match(path, @"PID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (vid.Success)
                {
                    string maker = vid.Groups[1].Value.ToUpperInvariant() switch
                    {
                        "056A" => "Wacom", "1532" => "Razer", "046D" => "Logitech", "045E" => "Microsoft",
                        "1038" => "SteelSeries", "1B1C" => "Corsair", "054C" => "Sony", "057E" => "Nintendo",
                        "3434" => "Keychron", "0B05" => "ASUS", "05AC" => "Apple", _ => null
                    };
                    ids = $"{maker ?? "VID " + vid.Groups[1].Value}{(pid.Success ? " PID " + pid.Groups[1].Value : "")}";
                }
                else if (path.Contains("ParsecVUSB", StringComparison.OrdinalIgnoreCase) || path.Contains("parsec", StringComparison.OrdinalIgnoreCase))
                    ids = "Parsec virtual device";
            }

            string name = !string.IsNullOrWhiteSpace(product) ? $"{product} ({kind}, {ids})"
                        : !string.IsNullOrEmpty(ids) ? $"{ids} {kind}"
                        : $"unknown {kind}";
            NameCache[hDevice] = name;
            return name;
        }

        static string ProductName(string path)
        {
            var h = CreateFile(path, 0, 3 /* share read|write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == (IntPtr)(-1)) return null;
            try
            {
                var buf = new byte[256];
                return HidD_GetProductString(h, buf, buf.Length) ? Encoding.Unicode.GetString(buf).TrimEnd('\0') : null;
            }
            finally { CloseHandle(h); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, StringBuilder data, ref uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("hid.dll")]
        static extern bool HidD_GetProductString(IntPtr device, byte[] buffer, int length);

        public override void DestroyHandle()
        {
            if (_notify != IntPtr.Zero) { UnregisterPowerSettingNotification(_notify); _notify = IntPtr.Zero; }
            base.DestroyHandle();
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);
        [DllImport("user32.dll")]
        static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    }

    // A stream that first returns bytes we've already read (the start of a TLS handshake),
    // then carries on reading from the network. Lets one port serve both http and https.
    sealed class PrefixedStream : Stream
    {
        readonly byte[] _prefix;
        int _pos;
        readonly int _len;
        readonly Stream _inner;

        public PrefixedStream(byte[] prefix, int length, Stream inner) { _prefix = prefix; _len = length; _inner = inner; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_pos < _len)
            {
                int n = Math.Min(buffer.Length, _len - _pos);
                _prefix.AsSpan(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            return _inner.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < _len)
            {
                int n = Math.Min(buffer.Length, _len - _pos);
                _prefix.AsSpan(_pos, n).CopyTo(buffer.Span);
                _pos += n;
                return new ValueTask<int>(n);
            }
            return _inner.ReadAsync(buffer, ct);
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // The app's own certificates for HTTPS on the home network, kept in the Windows
    // user certificate store (Personal), where Windows protects the private keys:
    //  • a local certificate authority, limited by "name constraints" to private network
    //    addresses and localhost, so even if copied it can't vouch for real websites;
    //  • a server certificate for this PC's current addresses, signed by it and
    //    re-issued automatically when the addresses change or it nears expiry.
    static class LocalCerts
    {
        const string CaName = "CN=Simple Monitor Off Local CA, O=Simple Monitor Off";
        const string ServerName = "CN=Simple Monitor Off, O=Simple Monitor Off";
        static readonly object Gate = new object();
        static X509Certificate2 _ca, _server;
        static string _serverKey;

        public static byte[] CaDer()
        {
            lock (Gate) return EnsureCa().RawData;
        }

        public static X509Certificate2 Server(IEnumerable<string> ips)
        {
            var list = new List<string>();
            foreach (var ip in ips) if (IPAddress.TryParse(ip, out _) && !list.Contains(ip)) list.Add(ip);
            if (!list.Contains("127.0.0.1")) list.Add("127.0.0.1");
            list.Sort(StringComparer.Ordinal);
            string key = string.Join(",", list);

            lock (Gate)
            {
                if (_server != null && _serverKey == key && _server.NotAfter > DateTime.Now.AddDays(30)) return _server;
                var ca = EnsureCa();

                // Reuse a stored certificate that already covers these addresses.
                using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    X509Certificate2 match = null;
                    var stale = new List<X509Certificate2>();
                    foreach (var c in store.Certificates)
                    {
                        if (!SameName(c.Subject, ServerName)) continue;
                        if (c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30) && c.Issuer == ca.Subject && SanKey(c) == key)
                            match ??= c;
                        else stale.Add(c);
                    }
                    if (match == null)
                    {
                        match = Persist(store, IssueServer(ca, list), "Simple Monitor Off (phone remote)");
                    }
                    foreach (var c in stale) { try { store.Remove(c); } catch { } }
                    _server = match;
                    _serverKey = key;
                }
                return _server;
            }
        }

        static bool SameName(string a, string b) =>
            string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

        static X509Certificate2 EnsureCa()
        {
            if (_ca != null) return _ca;
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (var c in store.Certificates)
                if (SameName(c.Subject, CaName) && c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30))
                    return _ca = c;
            return _ca = Persist(store, CreateCa(), "Simple Monitor Off local CA");
        }

        // Saves a new certificate and its private key in the user's store. Loading it back
        // from PFX (instead of using the in-memory key) is what Windows' TLS needs.
        static X509Certificate2 Persist(X509Store store, X509Certificate2 cert, string friendlyName)
        {
            var pfx = cert.Export(X509ContentType.Pfx);
            var stored = new X509Certificate2(pfx, (string)null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet) { FriendlyName = friendlyName };
            store.Add(stored);
            CryptographicOperations.ZeroMemory(pfx);
            cert.Dispose();
            return stored;
        }

        static X509Certificate2 CreateCa()
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest(CaName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            req.CertificateExtensions.Add(NameConstraints());
            var now = DateTimeOffset.UtcNow;
            return req.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        }

        static X509Certificate2 IssueServer(X509Certificate2 ca, List<string> ips)
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest(ServerName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            foreach (var ip in ips) san.AddIpAddress(IPAddress.Parse(ip));
            san.AddDnsName("localhost");
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") /* server authentication */ }, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F; // keep it positive
            var now = DateTimeOffset.UtcNow;
            using var signed = req.Create(ca, now.AddDays(-1), now.AddDays(397), serial); // ≤398 days: what browsers accept
            return signed.CopyWithPrivateKey(key);
        }

        // NameConstraints: the CA may only vouch for localhost and private IPv4 ranges.
        static X509Extension NameConstraints()
        {
            var w = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
            using (w.PushSequence())
            using (w.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 0)))
            {
                using (w.PushSequence())
                    w.WriteCharacterString(System.Formats.Asn1.UniversalTagNumber.IA5String, "localhost",
                        new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 2));
                void Range(byte[] net, byte[] mask)
                {
                    using (w.PushSequence())
                    {
                        var both = new byte[8];
                        Buffer.BlockCopy(net, 0, both, 0, 4);
                        Buffer.BlockCopy(mask, 0, both, 4, 4);
                        w.WriteOctetString(both, new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 7));
                    }
                }
                Range(new byte[] { 10, 0, 0, 0 }, new byte[] { 255, 0, 0, 0 });
                Range(new byte[] { 172, 16, 0, 0 }, new byte[] { 255, 240, 0, 0 });
                Range(new byte[] { 192, 168, 0, 0 }, new byte[] { 255, 255, 0, 0 });
                Range(new byte[] { 169, 254, 0, 0 }, new byte[] { 255, 255, 0, 0 });
                Range(new byte[] { 100, 64, 0, 0 }, new byte[] { 255, 192, 0, 0 });
                Range(new byte[] { 127, 0, 0, 0 }, new byte[] { 255, 0, 0, 0 });
            }
            return new X509Extension("2.5.29.30", w.Encode(), true);
        }

        // The sorted list of IP addresses a certificate covers, to compare with what we need.
        static string SanKey(X509Certificate2 cert)
        {
            var ips = new List<string>();
            foreach (var ext in cert.Extensions)
                if (ext.Oid?.Value == "2.5.29.17")
                    foreach (var ip in new X509SubjectAlternativeNameExtension(ext.RawData).EnumerateIPAddresses())
                        ips.Add(ip.ToString());
            ips.Sort(StringComparer.Ordinal);
            return string.Join(",", ips);
        }
    }

    // Small web server for the phone remote. Serves the control page and a PIN-protected API
    // over HTTPS (with the app's own local certificate). Plain http:// on the same port only
    // serves the certificate setup page and redirects everything else to https://.
    sealed class RemoteServer
    {
        public const int Port = 8765;

        readonly Func<string> _getPin;
        readonly Func<string, string> _command; // "status" | "off" | "wake" | "mute-on" | "mute-off" → JSON
        readonly Func<X509Certificate2> _getCert;
        readonly Func<bool> _useHttps;
        readonly byte[] _iconPng;
        TcpListener _listener;

        public RemoteServer(Func<string> getPin, Func<string, string> command, Func<bool> useHttps,
                            Func<X509Certificate2> getCert, byte[] iconPng)
        {
            _getPin = getPin;
            _command = command;
            _getCert = getCert;
            _useHttps = useHttps;
            _iconPng = iconPng;
        }

        public void Start()
        {
            if (_listener != null) return;
            var l = new TcpListener(IPAddress.Any, Port);
            l.Start(); // Windows asks once to allow this through the firewall
            _listener = l;
            _ = AcceptLoop(l);
        }

        public void Stop()
        {
            _listener?.Stop();
            _listener = null;
        }

        async Task AcceptLoop(TcpListener l)
        {
            while (true)
            {
                TcpClient client;
                try { client = await l.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { return; } // listener stopped
                _ = Task.Run(() => Handle(client));
            }
        }

        async Task Handle(TcpClient client)
        {
            using (client)
            {
                SslStream ssl = null;
                try
                {
                    var net = client.GetStream();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                    // First bytes tell us which it is: a TLS handshake (0x16) means https://.
                    var first = new byte[16384];
                    int firstLen = await net.ReadAsync(first, cts.Token);
                    if (firstLen == 0) return; // client closed (e.g. a browser's unused pre-connect)
                    bool tls = first[0] == 0x16;
                    bool httpsMode = _useHttps();

                    // HTTPS turned off: reject a browser's https:// attempt right away so it falls
                    // back to plain http:// instead of retrying (ERR_TOO_MANY_RETRIES).
                    if (tls && !httpsMode) { client.Client.Close(0); return; }

                    Stream stream;
                    var buf = new byte[16384];
                    int len = 0;
                    if (tls)
                    {
                        X509Certificate2 cert;
                        try { cert = _getCert(); }
                        catch (Exception ex) { WakeLog.Add("Phone remote: couldn't load the HTTPS certificate — " + ex.Message); return; }
                        ssl = new SslStream(new PrefixedStream(first, firstLen, net), false);
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = cert,
                            ClientCertificateRequired = false
                        }, cts.Token);
                        stream = ssl;
                    }
                    else
                    {
                        stream = net;
                        Buffer.BlockCopy(first, 0, buf, 0, firstLen);
                        len = firstLen;
                    }

                    // Read the request headers (up to the blank line), max 16 KB.
                    int headerEnd = FindHeaderEnd(buf, 0, len);
                    while (headerEnd < 0 && len < buf.Length)
                    {
                        int n = await stream.ReadAsync(buf.AsMemory(len), cts.Token);
                        if (n == 0) return;
                        int from = Math.Max(0, len - 3);
                        len += n;
                        headerEnd = FindHeaderEnd(buf, from, len);
                    }
                    if (headerEnd < 0) return;

                    var lines = Encoding.ASCII.GetString(buf, 0, headerEnd).Split("\r\n");
                    var parts = lines[0].Split(' ');
                    if (parts.Length < 2) return;
                    string method = parts[0];
                    string target = parts[1];
                    string path = target.Split('?', '#')[0];

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int li = 1; li < lines.Length; li++)
                    {
                        int i = lines[li].IndexOf(':');
                        if (i > 0) headers[lines[li][..i].Trim()] = lines[li][(i + 1)..].Trim();
                    }

                    // ----- HTTPS mode, plain http request: certificate setup only, everything else goes to https -----
                    if (!tls && httpsMode)
                    {
                        if (method == "GET" && path == "/cert")
                        { await Respond(stream, client.Client, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(CertPage)); return; }
                        if (method == "GET" && path == "/ca.crt")
                        {
                            await Respond(stream, client.Client, 200, "application/x-x509-ca-cert", LocalCerts.CaDer(),
                                          "Content-Disposition: attachment; filename=\"SimpleMonitorOff-CA.crt\"\r\n");
                            return;
                        }
                        if (method == "GET" && path == "/icon.png")
                        { await Respond(stream, client.Client, 200, "image/png", _iconPng); return; }

                        headers.TryGetValue("Host", out var host);
                        if (string.IsNullOrEmpty(host)) host = $"localhost:{Port}";
                        await Respond(stream, client.Client, 301, "text/plain", Encoding.ASCII.GetBytes("Moved to HTTPS"),
                                      $"Location: https://{host}{target}\r\n");
                        return;
                    }

                    // ----- the remote itself (https in HTTPS mode, plain http otherwise) -----
                    if (method == "GET" && path == "/") { await Respond(stream, client.Client, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page)); return; }
                    if (method == "GET" && path == "/manifest.json") { await Respond(stream, client.Client, 200, "application/manifest+json", Encoding.UTF8.GetBytes(Manifest)); return; }
                    if (method == "GET" && path == "/icon.png") { await Respond(stream, client.Client, 200, "image/png", _iconPng); return; }
                    if (method == "GET" && path == "/cert") { await Respond(stream, client.Client, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(CertPage)); return; }
                    if (method == "GET" && path == "/ca.crt")
                    {
                        await Respond(stream, client.Client, 200, "application/x-x509-ca-cert", LocalCerts.CaDer(),
                                      "Content-Disposition: attachment; filename=\"SimpleMonitorOff-CA.crt\"\r\n");
                        return;
                    }

                    if (!path.StartsWith("/api/")) { await Respond(stream, client.Client, 404, "text/plain", Encoding.ASCII.GetBytes("Not found")); return; }

                    // API: PIN required
                    headers.TryGetValue("X-Pin", out var pin);
                    if (!CryptographicOperations.FixedTimeEquals(
                            Encoding.ASCII.GetBytes(pin ?? ""), Encoding.ASCII.GetBytes(_getPin())))
                    {
                        await Task.Delay(1000); // slow down guessing
                        await Respond(stream, client.Client, 401, "text/plain", Encoding.ASCII.GetBytes("Bad PIN"));
                        return;
                    }

                    string cmd = path[5..];
                    bool ok = (method == "GET" && cmd == "status") ||
                              (method == "POST" && (cmd == "off" || cmd == "off-lock" || cmd == "wake" || cmd == "mute-on" || cmd == "mute-off"));
                    if (!ok) { await Respond(stream, client.Client, 404, "text/plain", Encoding.ASCII.GetBytes("Not found")); return; }

                    string json = _command(cmd);
                    await Respond(stream, client.Client, 200, "application/json", Encoding.UTF8.GetBytes(json));
                }
                catch { /* client went away, timed out, or rejected the certificate */ }
                finally { ssl?.Dispose(); }
            }
        }

        static int FindHeaderEnd(byte[] buf, int from, int len)
        {
            for (int i = from; i + 3 < len; i++)
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n') return i;
            return -1;
        }

        static async Task Respond(Stream s, Socket socket, int code, string type, byte[] body, string extraHeaders = "")
        {
            string reason = code switch { 200 => "OK", 301 => "Moved Permanently", 401 => "Unauthorized", 404 => "Not Found", _ => "Error" };
            string head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                          extraHeaders + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
            var all = new byte[head.Length + body.Length];
            Encoding.ASCII.GetBytes(head, 0, head.Length, all, 0);
            Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
            await s.WriteAsync(all);
            await s.FlushAsync();

            // Close politely: tell the browser we're done sending, then give it a moment
            // to read everything and hang up first. An abrupt close can make the browser
            // treat the response as lost and retry.
            try
            {
                if (s is SslStream ssl) await ssl.ShutdownAsync();
                socket.Shutdown(SocketShutdown.Send);
                using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var scratch = new byte[1024];
                while (await s.ReadAsync(scratch, drain.Token) > 0) { }
            }
            catch { /* timed out or already closed: fine */ }
        }

        // Shown over plain http:// so a phone can trust the app's certificate before using https://.
        const string CertPage = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="theme-color" content="#111111">
        <title>Monitor Off — trust this PC</title>
        <link rel="icon" href="/icon.png">
        <style>
          :root { color-scheme: dark; }
          body { margin: 0; background: #111; color: #eee; font: 17px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; padding: 24px; }
          main { max-width: 520px; margin: 0 auto; }
          h1 { font-size: 24px; margin: 0 0 8px; }
          p, li { color: #cfcfcf; }
          ol { padding-left: 22px; }
          li { margin-bottom: 10px; }
          b { color: #fff; }
          .btn { display: block; text-align: center; padding: 18px; border-radius: 16px; font-weight: 600; font-size: 19px;
                 text-decoration: none; margin: 18px 0; }
          .primary { background: #eee; color: #111; }
          .secondary { background: #262626; color: #eee; }
          .note { font-size: 14px; color: #9a9aa2; }
        </style>
        </head>
        <body>
        <main>
          <h1>Trust this PC</h1>
          <p>Your phone talks to Simple Monitor Off over an encrypted connection. Installing this PC's certificate once removes the "not private" warning.</p>
          <a class="btn primary" href="/ca.crt">1. Download certificate</a>
          <ol start="2">
            <li>Open <b>Settings</b> and search for <b>"CA certificate"</b>.<br>
                <span class="note">Or: Security &amp; privacy → More security settings → Encryption &amp; credentials → Install a certificate → CA certificate.</span></li>
            <li>Tap <b>Install anyway</b>, then pick <b>SimpleMonitorOff-CA.crt</b> from Downloads.</li>
            <li>Come back and open the remote:</li>
          </ol>
          <a class="btn secondary" id="go" href="/">Open the remote (https)</a>
          <p class="note">This certificate can only vouch for addresses on your home network (192.168.x.x, 10.x.x.x and similar), so it can't be used to impersonate websites. You can remove it any time in the same Settings screen under "User credentials".</p>
        </main>
        <script>
          document.getElementById('go').href = 'https://' + location.host + '/';
        </script>
        </body>
        </html>
        """;

        const string Manifest = """
        {
          "name": "Monitor Off",
          "short_name": "Monitor Off",
          "start_url": "/",
          "display": "standalone",
          "background_color": "#111111",
          "theme_color": "#111111",
          "icons": [ { "src": "/icon.png", "sizes": "192x192", "type": "image/png" } ]
        }
        """;

        const string Page = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
        <meta name="apple-mobile-web-app-capable" content="yes">
        <meta name="mobile-web-app-capable" content="yes">
        <meta name="apple-mobile-web-app-status-bar-style" content="black">
        <meta name="apple-mobile-web-app-title" content="Monitor Off">
        <meta name="theme-color" content="#111111">
        <title>Monitor Off</title>
        <link rel="manifest" href="/manifest.json">
        <link rel="apple-touch-icon" href="/icon.png">
        <link rel="icon" href="/icon.png">
        <style>
          :root { color-scheme: dark; }
          * { box-sizing: border-box; }
          body { margin: 0; min-height: 100vh; background: #111; color: #eee;
                 font: 16px system-ui, -apple-system, "Segoe UI", sans-serif;
                 display: flex; flex-direction: column; align-items: center; justify-content: center;
                 gap: 18px; padding: 24px; -webkit-user-select: none; user-select: none; }
          .wrap { width: 100%; max-width: 360px; display: flex; flex-direction: column; gap: 18px; }
          #status { display: flex; align-items: center; justify-content: center; gap: 10px; color: #aaa; font-size: 15px; }
          .dot { width: 10px; height: 10px; border-radius: 50%; background: #555; }
          body.on .dot { background: #4ade80; }
          body.off .dot { background: #777; }
          button { width: 100%; border: 0; border-radius: 20px; padding: 30px 0; font-size: 22px; font-weight: 600;
                   font-family: inherit; color: #111; background: #eee; -webkit-tap-highlight-color: transparent;
                   transition: transform .08s; }
          button:active { transform: scale(.97); }
          button.secondary { background: #262626; color: #eee; }
          button:disabled { opacity: .5; }
          .row { display: flex; justify-content: space-between; align-items: center; padding: 14px 6px;
                 border-top: 1px solid #262626; font-size: 17px; }
          .row input { width: 26px; height: 26px; accent-color: #eee; }
          #pinbox { display: none; }
          #pin { font-size: 28px; font-weight: 600; font-family: inherit; text-align: center; letter-spacing: 8px; padding: 16px;
                 border-radius: 14px; border: 1px solid #444; background: #1b1b1b; color: #eee; width: 100%; }
          .hint { color: #aaa; text-align: center; }
          .err { color: #f87171; text-align: center; min-height: 20px; }
        </style>
        </head>
        <body>
          <div class="wrap" id="main">
            <div id="status"><span class="dot"></span><span id="stext">Connecting…</span></div>
            <button id="off">Turn monitors off</button>
            <button id="wake" class="secondary">Wake monitors</button>
            <label class="row">Lock the PC when turning off <input type="checkbox" id="lock"></label>
            <label class="row">Mute sound while off <input type="checkbox" id="mute"></label>
          </div>

          <div class="wrap" id="pinbox">
            <div class="hint">Enter the 6-digit PIN shown on your PC<br>(tray icon → Phone remote)</div>
            <input id="pin" inputmode="numeric" pattern="[0-9]*" maxlength="6" autocomplete="off">
            <button id="connect">Connect</button>
            <div class="err" id="err"></div>
          </div>

        <script>
          const $ = id => document.getElementById(id);
          const store = {
            get() { try { return localStorage.getItem('pin') || ''; } catch { return ''; } },
            set(v) { try { localStorage.setItem('pin', v); } catch {} }
          };

          // A PIN in the QR-code link (#pin=123456) is saved, then removed from the address bar.
          const fromLink = new URLSearchParams(location.hash.slice(1)).get('pin');
          if (fromLink) { store.set(fromLink); history.replaceState(null, '', location.pathname); }
          let pin = store.get();

          function showPin(msg) {
            $('main').style.display = 'none';
            $('pinbox').style.display = 'flex';
            $('err').textContent = msg || '';
          }
          function showMain() {
            $('pinbox').style.display = 'none';
            $('main').style.display = 'flex';
          }

          async function api(path, method = 'GET') {
            const r = await fetch('/api/' + path, { method, headers: { 'X-Pin': pin }, cache: 'no-store' });
            if (r.status === 401) { showPin(pin ? 'That PIN didn\'t work.' : ''); throw 'auth'; }
            if (!r.ok) throw 'http';
            return r.json();
          }

          function render(s) {
            showMain();
            document.body.className = s.off ? 'off' : 'on';
            $('stext').textContent = s.off ? 'Monitors are off' : 'Monitors are on';
            $('mute').checked = s.mute;
          }

          function offline() {
            document.body.className = '';
            $('stext').textContent = 'Can\'t reach your PC — same Wi-Fi? App running?';
          }

          async function refresh() {
            if (!pin) { showPin(); return; }
            try { render(await api('status')); }
            catch (e) { if (e !== 'auth') offline(); }
          }

          async function act(path, btn) {
            if (btn) btn.disabled = true;
            try { render(await api(path, 'POST')); }
            catch (e) { if (e !== 'auth') offline(); }
            finally { if (btn) btn.disabled = false; }
          }

          $('off').onclick = () => act($('lock').checked ? 'off-lock' : 'off', $('off'));
          $('wake').onclick = () => act('wake', $('wake'));
          $('mute').onchange = () => act($('mute').checked ? 'mute-on' : 'mute-off');
          $('connect').onclick = () => { pin = $('pin').value.trim(); store.set(pin); refresh(); };
          $('pin').onkeydown = e => { if (e.key === 'Enter') $('connect').click(); };

          document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
          setInterval(() => { if (!document.hidden) refresh(); }, 3000);
          refresh();
        </script>
        </body>
        </html>
        """;
    }

    // Silences the default playback device (speakers/headphones) via Windows Core Audio.
    // It turns the volume to 0 rather than using the mute switch: on some USB headsets
    // (e.g. Razer) the hardware mute is shared, so muting the headphones also mutes the mic.
    static class Audio
    {
        // Current speaker volume 0–1, or null if it can't be read (e.g. no audio device).
        public static float? GetVolume()
        {
            try { return Endpoint().GetMasterVolumeLevelScalar(); }
            catch { return null; }
        }

        // Returns true if the change succeeded.
        public static bool SetVolume(float level)
        {
            try { Endpoint().SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), IntPtr.Zero); return true; }
            catch { return false; }
        }

        static IAudioEndpointVolume Endpoint()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out object o);
            return (IAudioEndpointVolume)o;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(); // unused slot
            void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                          [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            // Unused slots, declared only to keep the vtable order correct.
            void RegisterControlChangeNotify();
            void UnregisterControlChangeNotify();
            void GetChannelCount();
            void SetMasterVolumeLevel();
            void SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
            void GetMasterVolumeLevel();
            float GetMasterVolumeLevelScalar();
            void SetChannelVolumeLevel();
            void SetChannelVolumeLevelScalar();
            void GetChannelVolumeLevel();
            void GetChannelVolumeLevelScalar();
        }
    }

    static class Native
    {
        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        public const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204,
                         WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;
        public const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170;
        public const uint MOUSEEVENTF_MOVE = 0x0001;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool LockWorkStation();

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        // Tick count of the last keyboard/mouse input anywhere in this session, lock screen included.
        public static uint LastInputTick()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? info.dwTime : 0;
        }

        public const uint ES_DISPLAY_REQUIRED = 0x00000002;
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public MOUSEINPUT mi; } // mouse is the largest union member
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);

        // Moves the mouse 1 px right and back: counts as real input, so Windows wakes the displays.
        public static void NudgeMouse()
        {
            var inputs = new[]
            {
                new INPUT { type = 0, mi = new MOUSEINPUT { dx = 1, dwFlags = MOUSEEVENTF_MOVE } },
                new INPUT { type = 0, mi = new MOUSEINPUT { dx = -1, dwFlags = MOUSEEVENTF_MOVE } },
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
    }
}
