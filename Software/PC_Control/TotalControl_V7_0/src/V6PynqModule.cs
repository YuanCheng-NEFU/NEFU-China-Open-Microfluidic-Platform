using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

internal sealed class V6PynqModuleControl : UserControl
{
    private readonly V6AppSettings _settings;
    private readonly V6LogService _log;
    private readonly PynqV6Client _client;

    private readonly TextBox _host;
    private readonly NumericUpDown _port;
    private readonly NumericUpDown _threshold;
    private readonly NumericUpDown _hysteresis;
    private readonly NumericUpDown _pulseWidth;
    private readonly Label _gate;
    private readonly Label _mode;
    private readonly Label _message;
    private readonly Label _commandStatus;
    private readonly V6SignalChart _chart;
    private long _lastChartSample;
    private string _lastAcknowledgedCommandId = "";
    private Label _perfRxCount;
    private Label _perfJudge;
    private Label _perfSeq;
    private Label _perfLoss;
    private Label _perfGpio;
    private Label _perfP99;

    public event Action<PynqAckRecord> AckReceived;

    public event Action<PynqV6Status> StatusChanged;

    public V6PynqModuleControl(
        V6AppSettings settings,
        V6LogService log)
    {
        _settings = settings;
        _log = log;
        BackColor = V6Theme.Surface;

        _client = new PynqV6Client(OnClientStatusChanged);
        _client.AckReceived += delegate(PynqAckRecord record)
        {
            Action<PynqAckRecord> handler = AckReceived;
            if (handler != null)
                handler(record);
        };
        _client.IntegrityMilestone += delegate(string message)
        {
            _log.Info("PYNQ", message);
        };

        TableLayoutPanel pynqShell = new TableLayoutPanel();
        pynqShell.Dock = DockStyle.Fill;
        pynqShell.Margin = Padding.Empty;
        pynqShell.Padding = Padding.Empty;
        pynqShell.ColumnCount = 1;
        pynqShell.RowCount = 5;
        pynqShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        pynqShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 43F));
        pynqShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 43F));
        pynqShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 43F));
        pynqShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 170F));
        pynqShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        Controls.Add(pynqShell);

        FlowLayoutPanel connection = new FlowLayoutPanel();
        connection.Dock = DockStyle.Fill;
        connection.Margin = Padding.Empty;
        connection.Padding = new Padding(7, 5, 4, 2);
        connection.WrapContents = false;
        connection.AutoScroll = true;
        connection.BackColor = Color.White;
        pynqShell.Controls.Add(connection, 0, 0);

        connection.Controls.Add(V6Theme.Caption("IP"));
        _host = new TextBox();
        _host.Text = settings.PynqHost;
        _host.Width = 118;
        _host.Height = 27;
        connection.Controls.Add(_host);

        connection.Controls.Add(V6Theme.Caption("Port"));
        _port = V6Theme.Number(1, 65535, settings.PynqPort, 0);
        _port.Width = 72;
        connection.Controls.Add(_port);

        Button connect = V6Theme.Button("Connect", V6Theme.Green);
        connect.Click += delegate { Connect(); };
        connection.Controls.Add(connect);

        Button disconnect = V6Theme.Button("Disconnect", V6Theme.Gray);
        disconnect.Click += delegate
        {
            try
            {
                if (_client.IsConnected)
                    _client.ForceLow();
            }
            catch
            {
            }
            _client.Disconnect();
            _log.Info("PYNQ", "PYNQ connection closed.");
        };
        connection.Controls.Add(disconnect);

        Button guide = V6Theme.Button("Instructions", V6Theme.Blue);
        guide.Click += delegate { ShowOperationGuide(); };
        connection.Controls.Add(guide);

        FlowLayoutPanel parameters = new FlowLayoutPanel();
        parameters.Dock = DockStyle.Fill;
        parameters.Margin = Padding.Empty;
        parameters.Padding = new Padding(7, 5, 4, 2);
        parameters.WrapContents = false;
        parameters.AutoScroll = true;
        parameters.BackColor = Color.FromArgb(249, 251, 250);
        pynqShell.Controls.Add(parameters, 0, 1);

        parameters.Controls.Add(V6Theme.Caption("Threshold"));
        _threshold = V6Theme.Number(0, 1000000000M, (decimal)settings.Threshold, 0);
        parameters.Controls.Add(_threshold);

        parameters.Controls.Add(V6Theme.Caption("Hysteresis"));
        _hysteresis = V6Theme.Number(0, 1000000000M, (decimal)settings.Hysteresis, 0);
        parameters.Controls.Add(_hysteresis);

        Button apply = V6Theme.Button("Apply", V6Theme.Amber);
        apply.Click += delegate
        {
            try
            {
                ApplyThreshold(Threshold, Hysteresis);
            }
            catch (Exception ex)
            {
                ShowFailure("PYNQ threshold update failed", ex);
            }
        };
        parameters.Controls.Add(apply);

        FlowLayoutPanel actions = new FlowLayoutPanel();
        actions.Dock = DockStyle.Fill;
        actions.Margin = Padding.Empty;
        actions.Padding = new Padding(7, 5, 4, 2);
        actions.WrapContents = false;
        actions.AutoScroll = true;
        actions.BackColor = Color.FromArgb(249, 251, 250);
        pynqShell.Controls.Add(actions, 0, 2);

        Button automatic = V6Theme.Button("Auto gate", V6Theme.Green);
        automatic.Click += delegate
        {
            Execute(
                delegate { _client.SetAutoMode(); },
                "Automatic threshold gating requested.");
        };
        actions.Controls.Add(automatic);

        Button low = V6Theme.Button("Force LOW", V6Theme.Red);
        low.Click += delegate
        {
            Execute(
                delegate { _client.ForceLow(); },
                "Force LOW requested.");
        };
        actions.Controls.Add(low);

        actions.Controls.Add(V6Theme.Caption("Test width / ms"));
        _pulseWidth = V6Theme.Number(1, 5000, settings.TestPulseMilliseconds, 0);
        _pulseWidth.Width = 72;
        actions.Controls.Add(_pulseWidth);

        Button pulse = V6Theme.Button("Test pulse", V6Theme.Amber);
        pulse.Click += delegate
        {
            int width = Convert.ToInt32(_pulseWidth.Value);
            Execute(
                delegate { _client.ManualPulse(width); },
                "Sent " + width.ToString() + " ms test pulse.");
        };
        actions.Controls.Add(pulse);

        Panel status = new Panel();
        status.Dock = DockStyle.Fill;
        status.Margin = Padding.Empty;
        status.BackColor = Color.White;
        pynqShell.Controls.Add(status, 0, 3);

        _gate = new Label();
        _gate.Text = "GATE LOW";
        _gate.Font = new Font("Segoe UI", 18F, FontStyle.Bold);
        _gate.ForeColor = V6Theme.Green;
        _gate.Location = new Point(12, 8);
        _gate.AutoSize = true;
        status.Controls.Add(_gate);

        _mode = new Label();
        _mode.Text = "Disconnected";
        _mode.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        _mode.ForeColor = V6Theme.Muted;
        _mode.Location = new Point(178, 15);
        _mode.AutoSize = true;
        status.Controls.Add(_mode);

        _message = new Label();
        _message.Text = "Start the board service, then connect. See Instructions.";
        _message.ForeColor = V6Theme.Muted;
        _message.Location = new Point(15, 46);
        _message.Size = new Size(700, 20);
        _message.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        status.Controls.Add(_message);

        _commandStatus = new Label();
        _commandStatus.Text = "Last command: awaiting board acknowledgement";
        _commandStatus.ForeColor = V6Theme.Blue;
        _commandStatus.Location = new Point(15, 72);
        _commandStatus.Size = new Size(930, 22);
        _commandStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        status.Controls.Add(_commandStatus);

        FlowLayoutPanel perfMetrics = new FlowLayoutPanel();
        perfMetrics.Dock = DockStyle.Bottom;
        perfMetrics.Height = 78;
        perfMetrics.WrapContents = false;
        perfMetrics.AutoScroll = true;
        perfMetrics.Padding = new Padding(8, 4, 8, 2);
        perfMetrics.BackColor = Color.White;
        status.Controls.Add(perfMetrics);

        Label perfRxValue;
        perfMetrics.Controls.Add(CreatePerfCard("RX COUNT", "--", 120, out perfRxValue));
        _perfRxCount = perfRxValue;
        Label perfJudgeValue;
        perfMetrics.Controls.Add(CreatePerfCard("JUDGE", "--", 100, out perfJudgeValue));
        _perfJudge = perfJudgeValue;
        Label perfSeqValue;
        perfMetrics.Controls.Add(CreatePerfCard("SEQ", "--", 130, out perfSeqValue));
        _perfSeq = perfSeqValue;
        Label perfLossValue;
        perfMetrics.Controls.Add(CreatePerfCard("LOSS", "--", 100, out perfLossValue));
        _perfLoss = perfLossValue;
        Label perfGpioValue;
        perfMetrics.Controls.Add(CreatePerfCard("RX->GPIO", "--", 120, out perfGpioValue));
        _perfGpio = perfGpioValue;
        Label perfP99Value;
        perfMetrics.Controls.Add(CreatePerfCard("P99", "--", 110, out perfP99Value));
        _perfP99 = perfP99Value;

        _chart = new V6SignalChart();
        _chart.Dock = DockStyle.Fill;
        _chart.ForeColor = V6Theme.Blue;
        _chart.Threshold = settings.Threshold;
        pynqShell.Controls.Add(_chart, 0, 4);
    }

    public bool IsConnected
    {
        get { return _client.IsConnected; }
    }

    public PynqV6Status LatestStatus
    {
        get { return _client.LatestStatus; }
    }

    public double Threshold
    {
        get { return Convert.ToDouble(_threshold.Value); }
    }

    public double Hysteresis
    {
        get { return Convert.ToDouble(_hysteresis.Value); }
    }

    public void SetThresholdFields(double threshold, double hysteresis)
    {
        decimal on = (decimal)Math.Max(0.0, Math.Min(1000000000.0, threshold));
        decimal gap = (decimal)Math.Max(0.0, Math.Min((double)on, hysteresis));
        _threshold.Value = on;
        _hysteresis.Value = gap;
        _chart.Threshold = threshold;
    }

    public void Connect()
    {
        try
        {
            if (_client.IsConnected)
            {
                _log.Info(
                    "PYNQ",
                    "PYNQ is already connected. Disconnect before changing the IP address or port.");
                return;
            }

            PersistSettings();
            _client.Connect(
                _host.Text.Trim(),
                Convert.ToInt32(_port.Value),
                1800);
            _client.SetParameters(Threshold, Hysteresis);
            _client.ForceLow();
            _log.Info(
                "PYNQ",
                "Connected " + _host.Text.Trim() + ":" + _port.Value.ToString() +
                "; initial gate state is LOW.");
        }
        catch (Exception ex)
        {
            ShowFailure("PYNQ connection failed", ex);
        }
    }

    public void ApplyThreshold(double threshold, double hysteresis)
    {
        if (hysteresis > threshold)
            throw new InvalidOperationException("Hysteresis must not exceed the threshold.");

        SetThresholdFields(threshold, hysteresis);
        PersistSettings();

        if (_client.IsConnected)
            _client.SetParameters(threshold, hysteresis);

        _log.Info(
            "PYNQ",
            string.Format(
                CultureInfo.InvariantCulture,
                "Threshold updated: ON={0:0}, hysteresis={1:0}.",
                threshold,
                hysteresis));
    }

    public void SendSample(Ch297Sample sample, double threshold, double hysteresis)
    {
        _client.SendSample(sample, threshold, hysteresis);
    }

    public void SendHeartbeat()
    {
        _client.SendHeartbeat();
    }

    public void ForceLow()
    {
        _client.ForceLow();
    }

    public void SetAutoMode()
    {
        _client.SetAutoMode();
    }

    public void Shutdown()
    {
        try
        {
            if (_client.IsConnected)
                _client.ForceLow();
        }
        catch
        {
        }
        _client.Dispose();
    }

    public void PersistSettings()
    {
        _settings.PynqHost = _host.Text.Trim();
        _settings.PynqPort = Convert.ToInt32(_port.Value);
        _settings.TestPulseMilliseconds = Convert.ToInt32(_pulseWidth.Value);
        _settings.Threshold = Threshold;
        _settings.Hysteresis = Hysteresis;
    }

    private void Execute(MethodInvoker action, string success)
    {
        try
        {
            if (!_client.IsConnected)
                throw new InvalidOperationException("PYNQ is not connected.");
            action();
            _log.Info("PYNQ", success);
        }
        catch (Exception ex)
        {
            ShowFailure("PYNQ command failed", ex);
        }
    }

    private void OnClientStatusChanged(PynqV6Status status)
    {
        if (IsDisposed || Disposing)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action<PynqV6Status>(OnClientStatusChanged), status);
            return;
        }

        _gate.Text = status.GateHigh ? "GATE HIGH" : "GATE LOW";
        _gate.ForeColor = status.GateHigh ? V6Theme.Red : V6Theme.Green;
        _mode.Text = status.Connected
            ? status.Mode + " | Sample #" + status.SampleIndex.ToString()
            : "Disconnected";
        _message.Text = status.Message;

        if (string.IsNullOrEmpty(status.LastCommand))
        {
            _commandStatus.Text = "Last command: awaiting board acknowledgement";
        }
        else
        {
            string commandName = TranslateCommand(status.LastCommand);
            string pulse = status.LastCommand == "pulse"
                ? " " + status.LastPulseWidthMilliseconds.ToString() + " ms"
                : "";
            _commandStatus.Text =
                "Last command: " + commandName + pulse +
                " · " + status.LastCommandResult +
                " | Gate transitions: " + status.GateTransitions.ToString() + "";

            if (
                !string.IsNullOrEmpty(status.LastCommandId) &&
                !string.Equals(
                    status.LastCommandId,
                    _lastAcknowledgedCommandId,
                    StringComparison.Ordinal))
            {
                _lastAcknowledgedCommandId = status.LastCommandId;
                _log.Info(
                    "PYNQ",
                    "Board ACK [" + status.LastCommandId + "]: " +
                    commandName + pulse + ", " + status.LastCommandResult);
            }
        }

        _perfRxCount.Text = status.Count.ToString("0", CultureInfo.InvariantCulture);
        _perfJudge.Text = string.IsNullOrEmpty(status.LastAckJudge)
            ? "N/A"
            : status.LastAckJudge;
        _perfSeq.Text = status.AckedCount.ToString(CultureInfo.InvariantCulture) +
            " / " + status.SentCount.ToString(CultureInfo.InvariantCulture);
        _perfLoss.Text = status.PynqMissing.ToString(CultureInfo.InvariantCulture);
        _perfLoss.ForeColor = status.PynqMissing > 0
            ? V6Theme.Red
            : V6Theme.Green;
        _perfGpio.Text = status.GpioMedianMs.ToString("0.000", CultureInfo.InvariantCulture) + " ms";
        _perfP99.Text = status.GpioP99Ms.ToString("0.000", CultureInfo.InvariantCulture) + " ms";

        if (status.SampleIndex > 0 && status.SampleIndex != _lastChartSample)
        {
            _lastChartSample = status.SampleIndex;
            _chart.AddValue(status.Count);
        }

        Action<PynqV6Status> handler = StatusChanged;
        if (handler != null)
            handler(status);
    }

    private static string TranslateCommand(string command)
    {
        if (command == "pulse")
            return "Test pulse";
        if (command == "force_low")
            return "Force LOW";
        if (command == "auto")
            return "Auto gate";
        if (command == "set_params")
            return "Apply threshold";
        if (command == "hello")
            return "Handshake";
        return command;
    }

    private void ShowFailure(string title, Exception ex)
    {
        _log.Error("PYNQ", title + ": " + ex.Message);
        MessageBox.Show(
            ex.Message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void ShowOperationGuide()
    {
        string instructions =
            "NEFU-China iDEC | PYNQ-Z2 Operating Instructions\r\n" +
            "============================================================\r\n\r\n" +
            "1. Upload the board service\r\n" +
            "Open http://BOARD_IP:9090 in a browser to access Jupyter.\r\n" +
            "Create /home/xilinx/jupyter_notebooks/NEFU_iDEC.\r\n" +
            "Upload Software/PYNQ/pynq_v6_perf_server.py from this distribution.\r\n\r\n" +
            "2. Start in Jupyter Terminal or SSH\r\n" +
            "cd /home/xilinx/jupyter_notebooks/NEFU_iDEC\r\n" +
            "python3 pynq_v6_perf_server.py\r\n\r\n" +
            "If GPIO/Overlay permissions are required, use:\r\n" +
            "sudo -E python3 pynq_v6_perf_server.py\r\n\r\n" +
            "3. Confirm the board status\r\n" +
            "hostname -I\r\n" +
            "ss -ltn | grep 5000\r\n" +
            "After Listening on 0.0.0.0:5000 appears, enter the board IP in the controller and connect.\r\n\r\n" +
            "4. Commission the gate output\r\n" +
            "1. Keep the high-voltage amplifier disconnected. Connect a scope to PMOD B Pin 1 and GND.\r\n" +
            "2. Connect and confirm GATE LOW.\r\n" +
            "3. Select Test pulse and verify the configured 3.3 V pulse width.\r\n" +
            "4. Verify RX, GATE HIGH/LOW, ACK and PULSE complete in the board console.\r\n" +
            "5. Verify the board result is retained under Last command.\r\n" +
            "6. Select Force LOW and verify that the output returns to 0 V.\r\n" +
            "7. Establish continuous CH297 counts before selecting Auto gate.\r\n\r\n" +
            "To stop the board service, press Ctrl+C in its terminal.\r\n" +
            "Service stop, network loss, data timeout and controller heartbeat timeout request LOW.";

        Form dialog = new Form();
        dialog.Text = "PYNQ-Z2 instructions";
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.Size = new Size(780, 620);
        dialog.MinimumSize = new Size(620, 460);
        dialog.Font = Font;
        dialog.BackColor = Color.White;

        TableLayoutPanel guideShell = new TableLayoutPanel();
        guideShell.Dock = DockStyle.Fill;
        guideShell.Margin = Padding.Empty;
        guideShell.Padding = Padding.Empty;
        guideShell.ColumnCount = 1;
        guideShell.RowCount = 2;
        guideShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        guideShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        guideShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
        dialog.Controls.Add(guideShell);

        TextBox text = new TextBox();
        text.Dock = DockStyle.Fill;
        text.Multiline = true;
        text.ReadOnly = true;
        text.ScrollBars = ScrollBars.Both;
        text.WordWrap = false;
        text.Font = new Font("Consolas", 10F);
        text.Text = instructions;
        text.Select(0, 0);
        guideShell.Controls.Add(text, 0, 0);

        FlowLayoutPanel actions = new FlowLayoutPanel();
        actions.Dock = DockStyle.Fill;
        actions.Margin = Padding.Empty;
        actions.FlowDirection = FlowDirection.RightToLeft;
        actions.Padding = new Padding(6);
        actions.BackColor = Color.FromArgb(249, 251, 250);
        guideShell.Controls.Add(actions, 0, 1);

        Button close = V6Theme.Button("Close", V6Theme.Gray);
        close.Click += delegate { dialog.Close(); };
        actions.Controls.Add(close);

        Button copy = V6Theme.Button("Copy board command", V6Theme.Green);
        copy.Click += delegate
        {
            try
            {
                Clipboard.SetText(
                    "cd /home/xilinx/jupyter_notebooks/NEFU_iDEC\r\n" +
                    "python3 pynq_v6_perf_server.py");
                _log.Info("PYNQ", "PYNQ service command copied.");
            }
            catch (Exception ex)
            {
                ShowFailure("Copy PYNQ command failed", ex);
            }
        };
        actions.Controls.Add(copy);

        dialog.ShowDialog(FindForm());
        dialog.Dispose();
    }

    public void EnqueueSample(
        Ch297Sample sample,
        double threshold,
        double hysteresis)
    {
        _client.EnqueueSample(sample, threshold, hysteresis);
    }

    private static Panel CreatePerfCard(
        string title,
        string initialValue,
        int width,
        out Label value)
    {
        Panel card = new Panel();
        card.Size = new Size(width, 64);
        card.Margin = new Padding(0, 0, 7, 0);
        card.BackColor = Color.White;
        card.BorderStyle = BorderStyle.FixedSingle;

        Label caption = new Label();
        caption.Text = title;
        caption.ForeColor = V6Theme.Muted;
        caption.Font = new Font("Segoe UI", 8.5F);
        caption.AutoSize = true;
        caption.Location = new Point(9, 4);
        card.Controls.Add(caption);

        value = new Label();
        value.Text = initialValue;
        value.ForeColor = V6Theme.Green;
        value.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        value.AutoEllipsis = true;
        value.Location = new Point(8, 24);
        value.Size = new Size(width - 16, 34);
        card.Controls.Add(value);

        return card;
    }
}
