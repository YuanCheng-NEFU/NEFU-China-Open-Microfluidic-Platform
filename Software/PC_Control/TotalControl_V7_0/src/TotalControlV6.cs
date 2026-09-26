using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal sealed class TotalControlV6Form : Form
{
    private readonly V6AppSettings _settings;
    private readonly V6LogService _log;
    private readonly Ch297SyncStore _pmtStore;
    private Ch297SyncServer _pmtServer;
    private Process _ch297BridgeProcess;

    private V6CameraModuleControl _camera;
    private V6PmtModuleControl _pmt;
    private V6PynqModuleControl _pynq;
    private V6PumpModuleControl _pump;

    private readonly Dictionary<V6ModuleId, V6ModuleCard> _cards =
        new Dictionary<V6ModuleId, V6ModuleCard>();
    private readonly Dictionary<V6ModuleId, Button> _moduleButtons =
        new Dictionary<V6ModuleId, Button>();
    private readonly Dictionary<V6ModuleId, Form> _floating =
        new Dictionary<V6ModuleId, Form>();

    private TableLayoutPanel _shell;
    private RowStyle _logRowStyle;
    private TableLayoutPanel _workspace;
    private ComboBox _scene;
    private ComboBox _layoutMode;
    private TextBox _experimentId;
    private NumericUpDown _recordingFps;
    private CheckBox _useCamera;
    private CheckBox _usePmt;
    private CheckBox _usePynq;
    private CheckBox _usePump;
    private Label _footerStatus;
    private Panel _logDrawer;
    private V6LogPanel _logPanel;
    private System.Windows.Forms.Timer _uiTimer;

    private V6ModuleId? _focusedModule;
    private bool _experimentRunning;
    private bool _closing;
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private StreamWriter _perfAckCsv;
    private string _perfAckSession = "";
    private DateTime _lastAckFlush = DateTime.MinValue;

    public TotalControlV6Form()
    {
        _settings = V6AppSettings.Load();
        _log = new V6LogService();
        _pmtStore = new Ch297SyncStore();

        Text = "NEFU-China iDEC | Total Control V7.0 PERF";
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1180, 720);
        BackColor = V6Theme.Canvas;
        Font = new Font("Segoe UI", 9F);

        BuildShell();
        BuildModules();

        // V7.0 PERF: per-sample forwarding, ACK CSV, camera overlay PYNQ status
        _pmtStore.SampleReceived += OnPmtSampleReceived;
        _pynq.AckReceived += OnPynqAckReceived;
        _camera.PynqStatusProvider = delegate
        {
            return _pynq == null ? null : _pynq.LatestStatus;
        };

        StartPmtServer();
        ApplySettingsToUi();
        ApplyWorkspaceLayout();
        StartUiTimer();

        FormClosing += OnFormClosing;
        Shown += delegate { ApplyWorkspaceLayout(); };
        ResizeEnd += delegate { ApplyWorkspaceLayout(); };
        _log.Info(
            "SYSTEM",
            "Total Control V7.0 PERF started. PYNQ command acknowledgements and execution results are enabled.");
    }

    private void BuildShell()
    {
        _shell = new TableLayoutPanel();
        _shell.Dock = DockStyle.Fill;
        _shell.Margin = Padding.Empty;
        _shell.Padding = Padding.Empty;
        _shell.ColumnCount = 1;
        _shell.RowCount = 5;
        _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 68F));
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 86F));
        _shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _logRowStyle = new RowStyle(SizeType.Absolute, 0F);
        _shell.RowStyles.Add(_logRowStyle);
        _shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        Controls.Add(_shell);

        Panel header = new Panel();
        header.Dock = DockStyle.Fill;
        header.Margin = Padding.Empty;
        header.BackColor = V6Theme.GreenDark;
        _shell.Controls.Add(header, 0, 0);

        Label title = new Label();
        title.Text = "NEFU-China iDEC | Total Control V7.0 PERF";
        title.ForeColor = Color.White;
        title.Font = new Font("Segoe UI", 17F, FontStyle.Bold);
        title.AutoSize = true;
        title.Location = new Point(18, 9);
        header.Controls.Add(title);

        Label subtitle = new Label();
        subtitle.Text = "Camera | Photon counting | PYNQ-Z2 gate control | LSP02-3B pump";
        subtitle.ForeColor = Color.FromArgb(190, 222, 211);
        subtitle.AutoSize = true;
        subtitle.Location = new Point(21, 42);
        header.Controls.Add(subtitle);

        Button emergency = V6Theme.Button("SAFE STOP", V6Theme.Red);
        emergency.AutoSize = false;
        emergency.Size = new Size(138, 38);
        emergency.Dock = DockStyle.Right;
        emergency.Margin = new Padding(0);
        emergency.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        emergency.Click += delegate { StopIntegratedExperiment(false); };
        header.Controls.Add(emergency);

        Label identity = new Label();
        identity.Text = "Northeast Forestry University";
        identity.ForeColor = Color.FromArgb(224, 181, 77);
        identity.Dock = DockStyle.Right;
        identity.Width = 245;
        identity.TextAlign = ContentAlignment.MiddleCenter;
        header.Controls.Add(identity);

        TableLayoutPanel commandArea = new TableLayoutPanel();
        commandArea.Dock = DockStyle.Fill;
        commandArea.Margin = Padding.Empty;
        commandArea.Padding = Padding.Empty;
        commandArea.ColumnCount = 1;
        commandArea.RowCount = 2;
        commandArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        commandArea.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        commandArea.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        commandArea.BackColor = Color.White;
        _shell.Controls.Add(commandArea, 0, 1);

        FlowLayoutPanel workspaceCommands = new FlowLayoutPanel();
        workspaceCommands.Dock = DockStyle.Fill;
        workspaceCommands.Margin = Padding.Empty;
        workspaceCommands.Padding = new Padding(9, 5, 6, 2);
        workspaceCommands.WrapContents = false;
        workspaceCommands.AutoScroll = true;
        workspaceCommands.BackColor = Color.White;
        commandArea.Controls.Add(workspaceCommands, 0, 0);

        FlowLayoutPanel experimentCommands = new FlowLayoutPanel();
        experimentCommands.Dock = DockStyle.Fill;
        experimentCommands.Margin = Padding.Empty;
        experimentCommands.Padding = new Padding(9, 4, 6, 2);
        experimentCommands.WrapContents = false;
        experimentCommands.AutoScroll = true;
        experimentCommands.BackColor = Color.FromArgb(249, 251, 250);
        commandArea.Controls.Add(experimentCommands, 0, 1);

        workspaceCommands.Controls.Add(V6Theme.Caption("Workspace"));
        _scene = V6Theme.Combo();
        _scene.Width = 112;
        _scene.Items.AddRange(new object[] {
            "All modules", "Imaging", "Sorting setup", "Flow setup", "Custom"
        });
        _scene.SelectedIndexChanged += delegate
        {
            if (_scene.SelectedItem != null)
                ApplyScene(Convert.ToString(_scene.SelectedItem));
        };
        workspaceCommands.Controls.Add(_scene);

        workspaceCommands.Controls.Add(V6Theme.Caption("Layout"));
        _layoutMode = V6Theme.Combo();
        _layoutMode.Width = 105;
        _layoutMode.Items.AddRange(new object[] {
            "Auto", "Grid", "Columns", "Rows"
        });
        _layoutMode.SelectedIndexChanged += delegate
        {
            if (_layoutMode.SelectedItem == null)
                return;
            _settings.LayoutMode = Convert.ToString(_layoutMode.SelectedItem);
            ApplyWorkspaceLayout();
        };
        workspaceCommands.Controls.Add(_layoutMode);

        AddModuleToggle(workspaceCommands, V6ModuleId.Camera, "Camera");
        AddModuleToggle(workspaceCommands, V6ModuleId.Pmt, "PMT");
        AddModuleToggle(workspaceCommands, V6ModuleId.Pynq, "PYNQ");
        AddModuleToggle(workspaceCommands, V6ModuleId.Pump, "Pump");

        Button showLog = V6Theme.Button("Run log", V6Theme.Gray);
        showLog.Click += delegate
        {
            bool show = !_logDrawer.Visible;
            _logDrawer.Visible = show;
            _logRowStyle.Height = show ? 150F : 0F;
            showLog.Text = show ? "Hide log" : "Run log";
            _shell.PerformLayout();
        };
        workspaceCommands.Controls.Add(showLog);

        Label layoutHint = V6Theme.Caption("Scrollable workspace");
        layoutHint.ForeColor = V6Theme.Green;
        workspaceCommands.Controls.Add(layoutHint);

        experimentCommands.Controls.Add(V6Theme.Caption("Experiment ID"));
        _experimentId = new TextBox();
        _experimentId.Width = 224;
        _experimentId.Height = 27;
        _experimentId.Text = "NEFU_China_iDEC_" +
            DateTime.Now.ToString("yyyyMMdd_HHmmss");
        experimentCommands.Controls.Add(_experimentId);

        experimentCommands.Controls.Add(V6Theme.Caption("Recording FPS"));
        _recordingFps = V6Theme.Number(1, 400, _settings.RecordingFps, 0);
        _recordingFps.Width = 68;
        experimentCommands.Controls.Add(_recordingFps);

        _useCamera = AddUseCheck(experimentCommands, "Camera recording", _settings.ExperimentUsesCamera);
        _usePmt = AddUseCheck(experimentCommands, "PMT recording", _settings.ExperimentUsesPmt);
        _usePynq = AddUseCheck(experimentCommands, "PYNQ gate", _settings.ExperimentUsesPynq);
        _usePump = AddUseCheck(experimentCommands, "Pump", _settings.ExperimentUsesPump);

        Button start = V6Theme.Button("Start experiment", V6Theme.Green);
        start.Click += delegate { StartIntegratedExperiment(); };
        experimentCommands.Controls.Add(start);

        Button stop = V6Theme.Button("Stop experiment", V6Theme.Red);
        stop.Click += delegate { StopIntegratedExperiment(false); };
        experimentCommands.Controls.Add(stop);

        Panel footer = new Panel();
        footer.Dock = DockStyle.Fill;
        footer.Margin = Padding.Empty;
        footer.BackColor = Color.White;
        _shell.Controls.Add(footer, 0, 4);

        _footerStatus = new Label();
        _footerStatus.Dock = DockStyle.Fill;
        _footerStatus.Padding = new Padding(10, 0, 0, 0);
        _footerStatus.TextAlign = ContentAlignment.MiddleLeft;
        _footerStatus.ForeColor = V6Theme.Muted;
        footer.Controls.Add(_footerStatus);

        Label hint = new Label();
        hint.Dock = DockStyle.Right;
        hint.Width = 360;
        hint.Text = "Connections stay active | Safe stop covers all devices";
        hint.TextAlign = ContentAlignment.MiddleCenter;
        hint.ForeColor = V6Theme.Green;
        footer.Controls.Add(hint);

        _logDrawer = new Panel();
        _logDrawer.Dock = DockStyle.Fill;
        _logDrawer.Margin = Padding.Empty;
        _logDrawer.Padding = new Padding(8);
        _logDrawer.BackColor = Color.White;
        _logDrawer.Visible = false;
        _shell.Controls.Add(_logDrawer, 0, 3);

        _logPanel = new V6LogPanel(_log);
        _logPanel.Dock = DockStyle.Fill;
        _logDrawer.Controls.Add(_logPanel);

        _workspace = new TableLayoutPanel();
        _workspace.Dock = DockStyle.Fill;
        _workspace.BackColor = V6Theme.Canvas;
        _workspace.Padding = new Padding(6);
        _workspace.Margin = Padding.Empty;
        _workspace.AutoScroll = true;
        _shell.Controls.Add(_workspace, 0, 2);
    }

    private void BuildModules()
    {
        _camera = new V6CameraModuleControl(_pmtStore, _log);
        _pmt = new V6PmtModuleControl(
            _pmtStore,
            _settings,
            _log,
            StartCh297Bridge,
            StopCh297Bridge,
            ApplySharedThreshold);
        _pynq = new V6PynqModuleControl(_settings, _log);
        _pump = new V6PumpModuleControl(_settings, _log);

        AddCard(
            V6ModuleId.Camera,
            "Scientific camera",
            "MUS40M-G | Preview, exposure, snapshots, lossless recording",
            _camera);
        AddCard(
            V6ModuleId.Pmt,
            "Photon counting",
            "H10682 + CH297 | Dedicated 32-bit acquisition bridge",
            _pmt);
        AddCard(
            V6ModuleId.Pynq,
            "FPGA gate control",
            "PYNQ-Z2 | Threshold, hysteresis, pulse and timeout controls",
            _pynq);
        AddCard(
            V6ModuleId.Pump,
            "Syringe pump",
            "LSP02-3B | RS485 parameters and CH1 motion control",
            _pump);
    }

    private void AddCard(
        V6ModuleId id,
        string title,
        string subtitle,
        Control content)
    {
        V6ModuleCard card = new V6ModuleCard(id, title, subtitle, content);
        card.Dock = DockStyle.Fill;
        card.FocusRequested += ToggleFocus;
        card.PopoutRequested += TogglePopout;
        card.HideRequested += delegate(V6ModuleCard value)
        {
            SetModuleVisible(value.ModuleId, false, true);
        };
        _cards[id] = card;
    }

    private void AddModuleToggle(
        FlowLayoutPanel parent,
        V6ModuleId id,
        string text)
    {
        Button button = V6Theme.Button(text, V6Theme.Green);
        button.Tag = id;
        button.Click += delegate
        {
            bool visible = IsModuleVisible(id);
            SetModuleVisible(id, !visible, true);
        };
        _moduleButtons[id] = button;
        parent.Controls.Add(button);
    }

    private static CheckBox AddUseCheck(
        FlowLayoutPanel parent,
        string text,
        bool value)
    {
        CheckBox check = new CheckBox();
        check.Text = text;
        check.Checked = value;
        check.AutoSize = true;
        check.Margin = new Padding(7, 7, 3, 0);
        check.ForeColor = V6Theme.Ink;
        parent.Controls.Add(check);
        return check;
    }

    private void ApplySettingsToUi()
    {
        SelectText(_scene, _settings.LastScene);
        SelectText(_layoutMode, _settings.LayoutMode);
        _recordingFps.Value = Math.Max(
            _recordingFps.Minimum,
            Math.Min(_recordingFps.Maximum, _settings.RecordingFps));
        UpdateModuleButtons();
    }

    private void ApplyScene(string scene)
    {
        _settings.LastScene = scene;

        if (string.Equals(scene, "Imaging", StringComparison.Ordinal))
        {
            SetVisibilitySet(true, true, false, false);
            _useCamera.Checked = true;
            _usePmt.Checked = true;
            _usePynq.Checked = false;
            _usePump.Checked = false;
        }
        else if (string.Equals(scene, "Sorting setup", StringComparison.Ordinal))
        {
            SetVisibilitySet(true, true, true, false);
            _useCamera.Checked = true;
            _usePmt.Checked = true;
            _usePynq.Checked = true;
            _usePump.Checked = false;
        }
        else if (string.Equals(scene, "Flow setup", StringComparison.Ordinal))
        {
            SetVisibilitySet(true, false, false, true);
            _useCamera.Checked = true;
            _usePmt.Checked = false;
            _usePynq.Checked = false;
            _usePump.Checked = true;
        }
        else if (string.Equals(scene, "All modules", StringComparison.Ordinal))
        {
            SetVisibilitySet(true, true, true, true);
            _useCamera.Checked = true;
            _usePmt.Checked = true;
            _usePynq.Checked = true;
            _usePump.Checked = false;
        }
    }

    private void SetVisibilitySet(
        bool camera,
        bool pmt,
        bool pynq,
        bool pump)
    {
        _settings.CameraVisible = camera;
        _settings.PmtVisible = pmt;
        _settings.PynqVisible = pynq;
        _settings.PumpVisible = pump;
        _focusedModule = null;
        UpdateModuleButtons();
        ApplyWorkspaceLayout();
    }

    private void SetModuleVisible(
        V6ModuleId id,
        bool visible,
        bool markCustom)
    {
        if (id == V6ModuleId.Camera)
            _settings.CameraVisible = visible;
        else if (id == V6ModuleId.Pmt)
            _settings.PmtVisible = visible;
        else if (id == V6ModuleId.Pynq)
            _settings.PynqVisible = visible;
        else if (id == V6ModuleId.Pump)
            _settings.PumpVisible = visible;

        if (!visible && _focusedModule == id)
            _focusedModule = null;

        if (markCustom)
        {
            _settings.LastScene = "Custom";
            SelectText(_scene, "Custom");
        }

        UpdateModuleButtons();
        ApplyWorkspaceLayout();
        _log.Info(
            "WORKSPACE",
            id.ToString() + (visible ? " shown." : " hidden; device connection remains active."));
    }

    private bool IsModuleVisible(V6ModuleId id)
    {
        if (id == V6ModuleId.Camera)
            return _settings.CameraVisible;
        if (id == V6ModuleId.Pmt)
            return _settings.PmtVisible;
        if (id == V6ModuleId.Pynq)
            return _settings.PynqVisible;
        return _settings.PumpVisible;
    }

    private void UpdateModuleButtons()
    {
        foreach (KeyValuePair<V6ModuleId, Button> pair in _moduleButtons)
        {
            bool visible = IsModuleVisible(pair.Key);
            pair.Value.BackColor = visible ? V6Theme.Green : Color.FromArgb(137, 148, 144);
            pair.Value.Text = (visible ? "✓ " : "+ ") + ModuleName(pair.Key);
        }
    }

    private void ApplyWorkspaceLayout()
    {
        if (_workspace == null || _cards.Count == 0)
            return;

        _workspace.SuspendLayout();
        _workspace.Controls.Clear();
        _workspace.ColumnStyles.Clear();
        _workspace.RowStyles.Clear();

        List<V6ModuleCard> active = new List<V6ModuleCard>();
        foreach (V6ModuleId id in new[] {
            V6ModuleId.Camera, V6ModuleId.Pmt, V6ModuleId.Pynq, V6ModuleId.Pump
        })
        {
            if (!IsModuleVisible(id) || _floating.ContainsKey(id))
                continue;
            if (_focusedModule.HasValue && _focusedModule.Value != id)
                continue;
            active.Add(_cards[id]);
        }

        if (active.Count == 0)
        {
            _workspace.ColumnCount = 1;
            _workspace.RowCount = 1;
            _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            Label empty = new Label();
            empty.Text = "No modules displayed.\r\nSelect Camera, PMT, PYNQ or Pump above.";
            empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.Dock = DockStyle.Fill;
            empty.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
            empty.ForeColor = V6Theme.Muted;
            _workspace.Controls.Add(empty, 0, 0);
            _workspace.ResumeLayout(true);
            return;
        }

        int columns;
        int rows;
        string mode = _layoutMode == null || _layoutMode.SelectedItem == null
            ? _settings.LayoutMode
            : Convert.ToString(_layoutMode.SelectedItem);
        bool horizontalSplit = false;
        bool verticalSplit = false;

        if (_focusedModule.HasValue || active.Count == 1)
        {
            columns = 1;
            rows = 1;
        }
        else if (string.Equals(mode, "Columns", StringComparison.Ordinal))
        {
            columns = active.Count;
            rows = 1;
            horizontalSplit = true;
        }
        else if (string.Equals(mode, "Rows", StringComparison.Ordinal))
        {
            columns = 1;
            rows = active.Count;
            verticalSplit = true;
        }
        else if (string.Equals(mode, "Grid", StringComparison.Ordinal))
        {
            columns = 2;
            rows = (active.Count + 1) / 2;
        }
        else
        {
            columns = active.Count == 2 ? 2 : (active.Count >= 3 ? 2 : 1);
            rows = (active.Count + columns - 1) / columns;
        }

        _workspace.ColumnCount = columns;
        _workspace.RowCount = rows;
        if (horizontalSplit)
        {
            float preferredWidth = Math.Max(
                460F,
                Math.Max(1, _workspace.ClientSize.Width - 24) / (float)active.Count);
            for (int i = 0; i < columns; i++)
                _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, preferredWidth));
            _workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        }
        else if (verticalSplit)
        {
            _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            float preferredHeight = Math.Max(
                430F,
                Math.Max(1, _workspace.ClientSize.Height - 24) / (float)active.Count);
            for (int i = 0; i < rows; i++)
                _workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, preferredHeight));
        }
        else
        {
            for (int i = 0; i < columns; i++)
                _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
            for (int i = 0; i < rows; i++)
                _workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / rows));
        }

        for (int i = 0; i < active.Count; i++)
        {
            V6ModuleCard card = active[i];
            card.SetFocused(_focusedModule.HasValue && _focusedModule.Value == card.ModuleId);
            card.Dock = DockStyle.Fill;
            _workspace.Controls.Add(card, i % columns, i / columns);
        }

        bool cameraExpanded = active.Count == 1 && active[0].ModuleId == V6ModuleId.Camera;
        _camera.SetFocusMode(cameraExpanded);
        _workspace.ResumeLayout(true);
    }

    private void ToggleFocus(V6ModuleCard card)
    {
        if (_floating.ContainsKey(card.ModuleId))
            return;

        _focusedModule = _focusedModule.HasValue && _focusedModule.Value == card.ModuleId
            ? (V6ModuleId?)null
            : card.ModuleId;
        ApplyWorkspaceLayout();
    }

    private void TogglePopout(V6ModuleCard card)
    {
        if (_floating.ContainsKey(card.ModuleId))
        {
            ReattachCard(card.ModuleId);
            return;
        }

        _focusedModule = null;
        Form window = new Form();
        window.Text = "NEFU-China iDEC · " + ModuleName(card.ModuleId);
        window.StartPosition = FormStartPosition.CenterScreen;
        window.Size = new Size(980, 700);
        window.MinimumSize = new Size(620, 420);
        window.BackColor = V6Theme.Canvas;
        window.Font = Font;
        window.FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            if (_closing)
                return;
            e.Cancel = true;
            BeginInvoke(new MethodInvoker(delegate { ReattachCard(card.ModuleId); }));
        };

        _floating[card.ModuleId] = window;
        card.SetDetached(true);
        card.Dock = DockStyle.Fill;
        window.Controls.Add(card);
        window.Show(this);
        ApplyWorkspaceLayout();

        if (card.ModuleId == V6ModuleId.Camera)
            _camera.SetFocusMode(true);
    }

    private void ReattachCard(V6ModuleId id)
    {
        Form window;
        if (!_floating.TryGetValue(id, out window))
            return;

        V6ModuleCard card = _cards[id];
        window.Controls.Remove(card);
        _floating.Remove(id);
        card.SetDetached(false);
        window.Hide();
        window.Dispose();
        ApplyWorkspaceLayout();
    }

    private void StartPmtServer()
    {
        try
        {
            string directory = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "sync_logs");
            _pmtServer = new Ch297SyncServer(
                _pmtStore,
                delegate(string text)
                {
                    Ui(delegate { _footerStatus.Text = text; });
                });
            _pmtServer.Start(_settings.Ch297DataPort, directory);
            _log.Info(
                "PMT",
                "CH297 receiver listening on 127.0.0.1:" +
                _settings.Ch297DataPort.ToString() + ".");
        }
        catch (Exception ex)
        {
            _log.Error("PMT", "Data receiver startup failed: " + ex.Message);
            MessageBox.Show(
                ex.Message,
                "CH297 data service startup failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void StartCh297Bridge()
    {
        try
        {
            _pmt.PersistSettings();
            if (
                _ch297BridgeProcess != null &&
                !_ch297BridgeProcess.HasExited)
            {
                MessageBox.Show(
                    "CH297 bridge is already running.\r\nTo change the serial port, first select Stop in the PMT module.",
                    "CH297 running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(_pmt.ComPort))
                throw new InvalidOperationException("Enter the CH297 serial port, for example COM7.");
            if (string.IsNullOrWhiteSpace(_pmt.PortSetting))
                throw new InvalidOperationException("Enter the CH297 serial settings, for example 19200,n,8,1.");
            if (_pmt.GateMilliseconds < 10 || _pmt.GateMilliseconds % 10 != 0)
                throw new InvalidOperationException("CH297 gate time must be at least 10 ms and a multiple of 10 ms.");

            try
            {
                string existing = V6BridgeControl.Send(
                    _settings.Ch297ControlPort,
                    "get_status",
                    0,
                    0,
                    180);
                if (!string.IsNullOrEmpty(existing))
                {
                    MessageBox.Show(
                        "A running CH297 bridge was detected.\r\n" +
                        "Select Stop in the PMT module, then restart with the current settings.",
                        "CH297 bridge already running",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
            }
            catch
            {
                // No live control server: it is safe to start a new bridge.
            }

            string runtimeDirectory = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "runtime",
                "ch297");
            string bridgeExecutable = Path.Combine(
                runtimeDirectory,
                "NEFU_CH297_Bridge_V6_3_x86.exe");
            if (!File.Exists(bridgeExecutable))
                throw new FileNotFoundException(
                    "CH297 32-bit bridge not found. Run 00_BUILD_TOTAL_CONTROL_V7_0_PERF.cmd first.",
                    bridgeExecutable);

            Environment.SetEnvironmentVariable("NEFU_CH297_COM", _pmt.ComPort);
            Environment.SetEnvironmentVariable(
                "NEFU_CH297_PORT_SETTING",
                _pmt.PortSetting);
            Environment.SetEnvironmentVariable(
                "NEFU_CH297_GATE_MS",
                _pmt.GateMilliseconds.ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable(
                "NEFU_CH297_DATA_PORT",
                _settings.Ch297DataPort.ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable(
                "NEFU_CH297_CONTROL_PORT",
                _settings.Ch297ControlPort.ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable(
                "NEFU_CH297_THRESHOLD",
                _pmt.Threshold.ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable(
                "NEFU_CH297_HYSTERESIS",
                _pmt.Hysteresis.ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable(
                "NEFU_CH297_RUNTIME",
                runtimeDirectory);

            ProcessStartInfo start = new ProcessStartInfo(bridgeExecutable);
            start.WorkingDirectory = runtimeDirectory;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            long receivedBeforeStart = _pmtStore.ReceivedCount;
            _ch297BridgeProcess = Process.Start(start);

            // Do not report success merely because a process was created.
            // StartContinueCount may still fail (for example, -1 means that
            // the selected COM port received no reply from the CH297).  Wait
            // for the first real sample or for an early bridge exit so the UI
            // gives the operator one authoritative result.
            int waitMilliseconds = Math.Min(
                10000,
                Math.Max(3000, _pmt.GateMilliseconds * 3 + 1200));
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < waitMilliseconds)
            {
                Application.DoEvents();
                if (_pmtStore.ReceivedCount > receivedBeforeStart)
                    break;
                if (_ch297BridgeProcess.HasExited)
                    break;
                System.Threading.Thread.Sleep(50);
            }

            if (_pmtStore.ReceivedCount > receivedBeforeStart)
            {
                _log.Info(
                    "PMT",
                    "CH297 acquisition connected: " + _pmt.ComPort +
                    ", gate " + _pmt.GateMilliseconds.ToString() +
                    " ms; first count received.");
                _footerStatus.Text = "CH297 connected and counting: " + _pmt.ComPort;
                _footerStatus.ForeColor = V6Theme.Green;
                return;
            }

            bool exited = _ch297BridgeProcess.HasExited;
            if (!exited)
            {
                try
                {
                    V6BridgeControl.Send(
                        _settings.Ch297ControlPort,
                        "shutdown",
                        0,
                        0,
                        700);
                    _ch297BridgeProcess.WaitForExit(1500);
                }
                catch
                {
                }
            }
            _ch297BridgeProcess = null;
            throw new InvalidOperationException(BuildCh297StartupFailure());
        }
        catch (Exception ex)
        {
            ShowFailure("CH297 bridge startup failed", "PMT", ex);
        }
    }

    private string BuildCh297StartupFailure()
    {
        string logPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ch297_logs",
            "CH297_bridge_latest.log");
        string recent = ReadRecentText(logPath, 6000);
        if (recent.IndexOf(
                "StartContinueCount returned -1",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return
                "Serial port " + _pmt.ComPort +
                " received no CH297 response.\r\n\r\n" +
                "Close the CH297 vendor application and previous bridge consoles. Verify CH297 power, " +
                "then refresh and select the correct COM port in the PMT module and retry.\r\n" +
                "127.0.0.1:5102 is the internal control endpoint; keep this unchanged.";
        }
        if (recent.IndexOf(
                "StartContinueCount returned -4",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return
                "CH297 rejected the gate time. Start with 10 ms or 100 ms " +
                "and use a multiple of 10 ms.";
        }
        if (recent.IndexOf(
                "Control server error",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return
                "A CH297 bridge already owns the internal control port. Close previous control applications and bridge consoles before retrying.";
        }
        return
            "The CH297 bridge did not produce valid counts before the timeout. Check the COM port, " +
            "device power and USB connection, and close vendor software that holds the serial port.\r\n" +
            "Detailed log: " + logPath;
    }

    private static string ReadRecentText(string path, int maximumCharacters)
    {
        try
        {
            if (!File.Exists(path))
                return "";
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length <= maximumCharacters)
                return text;
            return text.Substring(text.Length - maximumCharacters);
        }
        catch
        {
            return "";
        }
    }

    private void OnPmtSampleReceived(Ch297Sample sample)
    {
        try
        {
            if (sample == null)
                return;
            if (_pynq != null && _pmt != null && _pynq.IsConnected)
                _pynq.EnqueueSample(
                    sample,
                    _pmt.Threshold,
                    _pmt.Hysteresis);
        }
        catch (Exception ex)
        {
            _log.Warning("PYNQ", "sample enqueue failed: " + ex.Message);
        }
    }

    private void OnPynqAckReceived(PynqAckRecord record)
    {
        try
        {
            if (record == null)
                return;
            if (_perfAckCsv == null ||
                !string.Equals(
                    _perfAckSession,
                    record.SessionId,
                    StringComparison.Ordinal))
            {
                ClosePerfAckCsv();
                _perfAckSession = string.IsNullOrEmpty(record.SessionId)
                    ? "LEGACY"
                    : record.SessionId;
                string directory = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "perf_logs");
                Directory.CreateDirectory(directory);
                _perfAckCsv = new StreamWriter(
                    Path.Combine(
                        directory,
                        "pynq_ack_" + _perfAckSession + ".csv"),
                    false,
                    new UTF8Encoding(true));
                _perfAckCsv.WriteLine(
                    "session_id,seq,rx_count,pynq_judge,gate," +
                    "rx_to_decide_us,decide_to_gpio_us,rx_to_gpio_us," +
                    "rx_to_ack_us,rtt_ms,result");
            }
            _perfAckCsv.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2:F3},{3},{4},{5:F3},{6:F3},{7:F3},{8:F3},{9:F3},{10}",
                record.SessionId,
                record.Seq,
                record.RxCount,
                record.Judge,
                record.Gate,
                record.RxToDecideUs,
                record.DecideToGpioUs,
                record.RxToGpioUs,
                record.RxToAckUs,
                record.RttMilliseconds,
                record.Result));
        }
        catch (Exception ex)
        {
            _log.Warning("PYNQ", "ack csv write failed: " + ex.Message);
        }
    }

    private void ClosePerfAckCsv()
    {
        if (_perfAckCsv != null)
        {
            try { _perfAckCsv.Flush(); }
            catch { }
            try { _perfAckCsv.Close(); }
            catch { }
            _perfAckCsv = null;
        }
    }

    private void WritePerfSummary()
    {
        try
        {
            if (_pynq == null || _pmt == null)
                return;

            PynqV6Status status = _pynq.LatestStatus;
            string session = _perfAckSession;
            if (string.IsNullOrEmpty(session))
            {
                Ch297Sample last = _pmt.LatestSample;
                session = last == null ? "UNKNOWN" : last.SessionId;
            }

            long source = _pmtStore.ReceivedCount;
            long valid = _pmtStore.ValidReceivedCount;
            double duration = 0.0;
            Ch297Sample newest = _pmt.LatestSample;
            if (newest != null && newest.IsValid)
                duration = newest.ElapsedSeconds;

            string directory = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "perf_logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                "communication_summary_" + session + ".txt");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("PYNQ DATA INTEGRITY REPORT");
            sb.AppendLine("Session: " + session);
            sb.AppendLine("Duration(s): " + duration.ToString("F1", CultureInfo.InvariantCulture));
            sb.AppendLine("Target Gate(ms): " + _pmt.GateMilliseconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Source Samples: " + source.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Valid Samples: " + valid.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Invalid CH297: " + (source - valid).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Packets Sent: " + status.SentCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Packets ACKed: " + status.AckedCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Missing: " + status.PynqMissing.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Duplicate: " + status.PynqDuplicates.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Out-of-order: " + status.PynqOutOfOrder.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Invalid(PYNQ): " + status.PynqInvalidSlots.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Parse errors: " + status.PynqParseErrors.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Protocol errors: " + status.PynqProtocolErrors.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("TCP reconnect count: " + status.Connects.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("PYNQ RX->GPIO(ms):");
            sb.AppendLine("  median=" + status.GpioMedianMs.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("  p95=" + status.GpioP95Ms.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("  p99=" + status.GpioP99Ms.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("  max=" + status.GpioMaxMs.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("PC Send->ACK RTT(ms):");
            sb.AppendLine("  median=" + status.RttMedianMs.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("  p95=" + status.RttP95Ms.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("  p99=" + status.RttP99Ms.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("  max=" + status.RttMaxMs.ToString("F3", CultureInfo.InvariantCulture));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            _log.Info("PYNQ", "perf summary written: " + path);
        }
        catch (Exception ex)
        {
            _log.Warning("PYNQ", "perf summary write failed: " + ex.Message);
        }
    }

    private void StopCh297Bridge()
    {
        WritePerfSummary();
        ClosePerfAckCsv();
        try
        {
            string response = V6BridgeControl.Send(
                _settings.Ch297ControlPort,
                "shutdown",
                0,
                0,
                700);
            _log.Info("PMT", "Requested CH297 bridge stop and serial port release: " + response);
            if (_ch297BridgeProcess != null)
            {
                try { _ch297BridgeProcess.WaitForExit(1500); }
                catch { }
            }
            _ch297BridgeProcess = null;
        }
        catch (Exception ex)
        {
            ShowFailure("CH297 bridge stop failed", "PMT", ex);
        }
    }

    private void ApplySharedThreshold(double threshold, double hysteresis)
    {
        if (hysteresis > threshold)
            throw new InvalidOperationException("Hysteresis must not exceed the threshold.");

        _settings.Threshold = threshold;
        _settings.Hysteresis = hysteresis;
        _pynq.SetThresholdFields(threshold, hysteresis);

        try
        {
            string response = V6BridgeControl.Send(
                _settings.Ch297ControlPort,
                "set_threshold",
                threshold,
                hysteresis,
                400);
            _log.Info("PMT", "CH297 threshold updated: " + response);
        }
        catch
        {
            _log.Warning("PMT", "Threshold saved for the next CH297 bridge start.");
        }

        try
        {
            _pynq.ApplyThreshold(threshold, hysteresis);
        }
        catch (Exception ex)
        {
            _log.Warning("PYNQ", "Threshold transmission pending: " + ex.Message);
        }
    }

    private void StartIntegratedExperiment()
    {
        if (_experimentRunning)
            return;

        bool useCamera = _useCamera.Checked;
        bool usePmt = _usePmt.Checked;
        bool usePynq = _usePynq.Checked;
        bool usePump = _usePump.Checked;

        if (!useCamera && !usePmt && !usePynq && !usePump)
        {
            MessageBox.Show("Select at least one experiment module.");
            return;
        }

        if (usePynq && !usePmt)
        {
            MessageBox.Show(
                "Automatic gating requires PMT count input.\r\nFor a PYNQ-only test, use the manual pulse in the PYNQ module.",
                "PMT input required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            string id = string.IsNullOrWhiteSpace(_experimentId.Text)
                ? "NEFU_China_iDEC_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : _experimentId.Text.Trim();

            Ch297Sample pmtReadySample = _pmt.LatestSample;
            if (
                usePmt &&
                (!_pmt.IsConnected ||
                 pmtReadySample == null ||
                 !pmtReadySample.IsValid))
                throw new InvalidOperationException("PMT is selected. Connect the CH297 bridge and establish valid acquisition first.");
            if (usePynq && !_pynq.IsConnected)
                throw new InvalidOperationException("PYNQ gating is selected. Connect PYNQ first.");
            if (usePump && !_pump.IsOpen)
                throw new InvalidOperationException("Pump is selected. Connect the pump first.");

            double threshold = _pmt.Threshold;
            double hysteresis = _pmt.Hysteresis;
            ApplySharedThreshold(threshold, hysteresis);

            if (usePynq)
                _pynq.ForceLow();

            if (useCamera)
            {
                _camera.ExperimentId = id;
                _camera.RecordingFps = Convert.ToInt32(_recordingFps.Value);
                if (!_camera.IsConnected)
                    _camera.ConnectAndPreview();
                if (!_camera.IsConnected)
                    throw new InvalidOperationException("Camera connection failed; experiment start cancelled.");
                _camera.StartRecording();
                if (!_camera.IsRecording)
                    throw new InvalidOperationException("Camera recording did not start.");
            }

            if (usePump)
            {
                if (!_pump.StartChannelOne(false))
                    throw new InvalidOperationException("Syringe-pump CH1 did not start.");
            }

            if (usePynq)
                _pynq.SetAutoMode();

            _experimentRunning = true;
            _footerStatus.Text = "RUNNING: " + id;
            _footerStatus.ForeColor = V6Theme.Red;
            _log.Warning(
                "EXPERIMENT",
                "Experiment started: " + id +
                " [Camera=" + useCamera +
                ", PMT=" + usePmt +
                ", PYNQ=" + usePynq +
                ", Pump=" + usePump + "]");
        }
        catch (Exception ex)
        {
            StopIntegratedExperiment(true);
            ShowFailure("Experiment could not start", "EXPERIMENT", ex);
        }
    }

    private void StopIntegratedExperiment(bool silent)
    {
        List<string> errors = new List<string>();

        try
        {
            if (_pynq != null && _pynq.IsConnected)
                _pynq.ForceLow();
        }
        catch (Exception ex)
        {
            errors.Add("PYNQ force-low failed: " + ex.Message);
        }

        try
        {
            if (_pump != null && _pump.IsOpen)
            {
                if (!_pump.StopAll(false))
                    errors.Add("Pump stop command could not be sent. Use the physical stop button on the pump.");
            }
        }
        catch (Exception ex)
        {
            errors.Add("Pump stop failed: " + ex.Message);
        }

        try
        {
            if (_camera != null && _camera.IsRecording)
                _camera.StopRecording();
        }
        catch (Exception ex)
        {
            errors.Add("Camera recording stop failed: " + ex.Message);
        }

        bool wasRunning = _experimentRunning;
        _experimentRunning = false;
        _footerStatus.Text = errors.Count == 0
            ? "Standby: PYNQ LOW requested, pump stopped, recording index saved."
            : "Safe stop requires attention. Open the run log and inspect the devices.";
        _footerStatus.ForeColor = errors.Count == 0 ? V6Theme.Green : V6Theme.Red;

        if (wasRunning || errors.Count > 0)
            _log.Info("EXPERIMENT", errors.Count == 0 ? "Experiment stopped safely." : "Safe stop requires attention.");

        for (int i = 0; i < errors.Count; i++)
            _log.Error("EXPERIMENT", errors[i]);

        if (!silent && errors.Count > 0)
        {
            MessageBox.Show(
                string.Join("\r\n", errors.ToArray()),
                "Safe-stop alert",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void StartUiTimer()
    {
        _uiTimer = new System.Windows.Forms.Timer();
        // PMT can deliver tens of valid samples per second at a 10 ms gate.
        // Refresh at 10 Hz and drain all queued samples so the count and chart
        // feel live without redrawing the entire workspace per hardware frame.
        _uiTimer.Interval = 100;
        _uiTimer.Tick += delegate { UpdateRuntime(); };
        _uiTimer.Start();
    }

    private void UpdateRuntime()
    {
        _camera.UpdateStatus();
        _pmt.UpdateSample();

        SetCardState(
            V6ModuleId.Camera,
            _camera.IsConnected
                ? (_camera.IsRecording ? "Recording" : "Connected")
                : "Disconnected",
            _camera.IsConnected
                ? (_camera.IsRecording ? V6Theme.Red : V6Theme.Green)
                : V6Theme.Gray);

        Ch297Sample sample = _pmt.LatestSample;
        Ch297Sample receivedSample = _pmt.LatestReceivedSample;
        SetCardState(
            V6ModuleId.Pmt,
            _pmt.IsConnected
                ? (sample == null
                    ? (receivedSample == null ? "Waiting for data" : "Data error")
                    : sample.Count.ToString("0"))
                : "Disconnected",
            _pmt.IsConnected
                ? (sample == null && receivedSample != null
                    ? V6Theme.Red
                    : V6Theme.Green)
                : V6Theme.Gray);

        PynqV6Status pynqStatus = _pynq.LatestStatus;
        SetCardState(
            V6ModuleId.Pynq,
            pynqStatus.Connected
                ? (pynqStatus.GateHigh ? "GATE HIGH" : "GATE LOW")
                : "Disconnected",
            pynqStatus.Connected
                ? (pynqStatus.GateHigh ? V6Theme.Red : V6Theme.Green)
                : V6Theme.Gray);

        SetCardState(
            V6ModuleId.Pump,
            _pump.IsOpen ? "Connected" : "Disconnected",
            _pump.IsOpen ? V6Theme.Green : V6Theme.Gray);

        if (_perfAckCsv != null &&
            DateTime.UtcNow - _lastAckFlush > TimeSpan.FromSeconds(0.5))
        {
            try { _perfAckCsv.Flush(); }
            catch { }
            _lastAckFlush = DateTime.UtcNow;
        }

        if (
            _pynq.IsConnected &&
            DateTime.Now - _lastHeartbeat > TimeSpan.FromSeconds(1))
        {
            try
            {
                _pynq.SendHeartbeat();
                _lastHeartbeat = DateTime.Now;
            }
            catch
            {
            }
        }

        if (!_experimentRunning)
        {
            _footerStatus.Text = string.Format(
                CultureInfo.InvariantCulture,
                "Standby | Camera {0} | PMT {1} | PYNQ {2} | Pump {3}",
                _camera.IsConnected ? "ON" : "OFF",
                _pmt.IsConnected
                    ? (sample != null && sample.IsValid ? "OK" : "ERR")
                    : "OFF",
                _pynq.IsConnected ? "ON" : "OFF",
                _pump.IsOpen ? "ON" : "OFF");
        }
    }

    private void SetCardState(V6ModuleId id, string text, Color color)
    {
        V6ModuleCard card;
        if (_cards.TryGetValue(id, out card))
            card.SetState(text, color);
    }

    private void PersistSettings()
    {
        _pmt.PersistSettings();
        _pynq.PersistSettings();
        _pump.PersistSettings();
        _settings.LayoutMode = Convert.ToString(_layoutMode.SelectedItem);
        _settings.LastScene = Convert.ToString(_scene.SelectedItem);
        _settings.RecordingFps = Convert.ToInt32(_recordingFps.Value);
        _settings.ExperimentUsesCamera = _useCamera.Checked;
        _settings.ExperimentUsesPmt = _usePmt.Checked;
        _settings.ExperimentUsesPynq = _usePynq.Checked;
        _settings.ExperimentUsesPump = _usePump.Checked;
        _settings.Save();
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_experimentRunning)
        {
            DialogResult answer = MessageBox.Show(
                "An experiment is running. Perform a safe stop and exit?",
                "Confirm exit",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _closing = true;
        StopIntegratedExperiment(true);

        try { PersistSettings(); }
        catch (Exception ex) { _log.Error("CONFIG", "Settings save failed: " + ex.Message); }

        if (_uiTimer != null)
        {
            _uiTimer.Stop();
            _uiTimer.Dispose();
            _uiTimer = null;
        }

        List<V6ModuleId> detached = new List<V6ModuleId>(_floating.Keys);
        for (int i = 0; i < detached.Count; i++)
            ReattachCard(detached[i]);

        WritePerfSummary();
        ClosePerfAckCsv();

        if (_pynq != null)
            _pynq.Shutdown();
        if (_pump != null)
            _pump.Shutdown();
        if (_camera != null)
            _camera.Shutdown();

        if (_pmtServer != null)
        {
            try
            {
                bool bridgeMayBeRunning = _pmtStore.IsConnected;
                try
                {
                    bridgeMayBeRunning = bridgeMayBeRunning ||
                        (_ch297BridgeProcess != null && !_ch297BridgeProcess.HasExited);
                }
                catch
                {
                }

                if (bridgeMayBeRunning)
                {
                    V6BridgeControl.Send(
                        _settings.Ch297ControlPort,
                        "shutdown",
                        0,
                        0,
                        500);
                }
            }
            catch
            {
            }

            _pmtServer.Stop();
            _pmtServer.Dispose();
            _pmtServer = null;
        }

        _pmtStore.Dispose();
    }

    private void ShowFailure(string title, string source, Exception ex)
    {
        _log.Error(source, title + ": " + ex.Message);
        MessageBox.Show(
            ex.Message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void Ui(MethodInvoker action)
    {
        if (action == null || IsDisposed || Disposing)
            return;
        try
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
        catch
        {
        }
    }

    private static string ModuleName(V6ModuleId id)
    {
        if (id == V6ModuleId.Camera)
            return "Camera";
        if (id == V6ModuleId.Pmt)
            return "PMT";
        if (id == V6ModuleId.Pynq)
            return "PYNQ";
        return "Pump";
    }

    private static void SelectText(ComboBox combo, string text)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (string.Equals(
                    Convert.ToString(combo.Items[i]),
                    text,
                    StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }
}

internal static class V6Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TotalControlV6Form());
        }
        catch (Exception ex)
        {
            string path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "NEFU_iDEC_TotalControl_V6_2_startup_error.log");
            try
            {
                File.WriteAllText(path, ex.ToString(), new UTF8Encoding(true));
            }
            catch
            {
            }

            MessageBox.Show(
                ex.ToString() + "\r\n\r\nStartup log: " + path,
                "Total Control V7.0 PERF startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
