using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class V6CameraModuleControl : UserControl
{
    private readonly V6LogService _log;
    private readonly PreviewForm _preview;
    private readonly Button _recordButton;
    private readonly NumericUpDown _exposure;
    private readonly Label _summary;
    private bool _advanced;

    public V6CameraModuleControl(
        Ch297SyncStore sharedStore,
        V6LogService log)
    {
        _log = log;
        BackColor = Color.Black;

        TableLayoutPanel cameraShell = new TableLayoutPanel();
        cameraShell.Dock = DockStyle.Fill;
        cameraShell.Margin = Padding.Empty;
        cameraShell.Padding = Padding.Empty;
        cameraShell.ColumnCount = 1;
        cameraShell.RowCount = 2;
        cameraShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        cameraShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
        cameraShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        Controls.Add(cameraShell);

        FlowLayoutPanel toolbar = new FlowLayoutPanel();
        toolbar.Dock = DockStyle.Fill;
        toolbar.Margin = Padding.Empty;
        toolbar.Padding = new Padding(6, 5, 4, 3);
        toolbar.WrapContents = false;
        toolbar.AutoScroll = true;
        toolbar.BackColor = Color.White;
        cameraShell.Controls.Add(toolbar, 0, 0);

        Button connect = V6Theme.Button("Connect / Preview", V6Theme.Green);
        connect.Click += delegate
        {
            try
            {
                _preview.IntegratedConnectAndPreview();
                _log.Info("CAMERA", "Camera connection and preview requested.");
            }
            catch (Exception ex)
            {
                ShowFailure("Camera connection failed", ex);
            }
        };
        toolbar.Controls.Add(connect);

        Button disconnect = V6Theme.Button("Disconnect", V6Theme.Gray);
        disconnect.Click += delegate
        {
            try
            {
                if (_preview.IntegratedRecording)
                    _preview.IntegratedStopRecording();
                _preview.IntegratedDisconnectCamera();
                UpdateStatus();
                _log.Info("CAMERA", "Camera recording and preview stopped; device disconnected.");
            }
            catch (Exception ex)
            {
                ShowFailure("Camera disconnect failed", ex);
            }
        };
        toolbar.Controls.Add(disconnect);

        _recordButton = V6Theme.Button("Record", Color.FromArgb(163, 42, 76));
        _recordButton.Click += delegate
        {
            try
            {
                if (_preview.IntegratedRecording)
                {
                    _preview.IntegratedStopRecording();
                    _log.Info("CAMERA", "Recording stopped and index saved.");
                }
                else
                {
                    _preview.IntegratedStartRecording(ExperimentId, RecordingFps);
                    if (_preview.IntegratedRecording)
                        _log.Info("CAMERA", "Recording started: " + ExperimentId);
                }
                UpdateStatus();
            }
            catch (Exception ex)
            {
                ShowFailure("Recording operation failed", ex);
            }
        };
        toolbar.Controls.Add(_recordButton);

        Button snapshot = V6Theme.Button("Snapshot", V6Theme.Blue);
        snapshot.Click += delegate
        {
            try
            {
                _preview.IntegratedSaveSnapshot();
                _log.Info("CAMERA", "Current camera snapshot saved.");
            }
            catch (Exception ex)
            {
                ShowFailure("Snapshot save failed", ex);
            }
        };
        toolbar.Controls.Add(snapshot);

        toolbar.Controls.Add(V6Theme.Caption("Exposure / ms"));
        _exposure = V6Theme.Number(0.001M, 10000M, 1.0M, 3);
        toolbar.Controls.Add(_exposure);

        Button applyExposure = V6Theme.Button("Apply", V6Theme.Amber);
        applyExposure.Click += delegate
        {
            try
            {
                _preview.IntegratedSetExposure(_exposure.Value);
                _log.Info(
                    "CAMERA",
                    "Exposure requested: " +
                    _exposure.Value.ToString("0.###") +
                    " ms.");
            }
            catch (Exception ex)
            {
                ShowFailure("Exposure update failed", ex);
            }
        };
        toolbar.Controls.Add(applyExposure);

        Button advanced = V6Theme.Button("Full camera panel", V6Theme.Gray);
        advanced.Click += delegate
        {
            _advanced = !_advanced;
            _preview.IntegratedSetCompactMode(!_advanced);
            advanced.Text = _advanced ? "Compact camera panel" : "Full camera panel";
        };
        toolbar.Controls.Add(advanced);

        _summary = new Label();
        _summary.AutoSize = true;
        _summary.ForeColor = V6Theme.Muted;
        _summary.Margin = new Padding(12, 8, 3, 0);
        toolbar.Controls.Add(_summary);

        Panel host = new Panel();
        host.Dock = DockStyle.Fill;
        host.Margin = Padding.Empty;
        host.BackColor = Color.Black;
        cameraShell.Controls.Add(host, 0, 1);

        _preview = new PreviewForm(sharedStore, false);
        _preview.TopLevel = false;
        _preview.FormBorderStyle = FormBorderStyle.None;
        _preview.Dock = DockStyle.Fill;
        _preview.IntegratedSetCompactMode(true);
        host.Controls.Add(_preview);
        _preview.Show();

        ExperimentId = "NEFU_China_iDEC_" +
            DateTime.Now.ToString("yyyyMMdd_HHmmss");
        RecordingFps = 30;
    }

    public string ExperimentId { get; set; }

    public int RecordingFps { get; set; }

    public bool IsConnected
    {
        get { return _preview.IntegratedCameraConnected; }
    }

    public bool IsRecording
    {
        get { return _preview.IntegratedRecording; }
    }

    public string StatusText
    {
        get { return _preview.IntegratedCameraStatus; }
    }

    public Func<PynqV6Status> PynqStatusProvider
    {
        set { _preview.PynqStatusProvider = value; }
    }

    public void ConnectAndPreview()
    {
        _preview.IntegratedConnectAndPreview();
    }

    public void StartRecording()
    {
        _preview.IntegratedStartRecording(ExperimentId, RecordingFps);
    }

    public void StopRecording()
    {
        _preview.IntegratedStopRecording();
    }

    public void SetFocusMode(bool focused)
    {
        if (_advanced)
            return;
        _preview.IntegratedSetCompactMode(!focused);
    }

    public void UpdateStatus()
    {
        _recordButton.Text = IsRecording ? "Stop recording" : "Record";
        _recordButton.BackColor = IsRecording
            ? V6Theme.Red
            : Color.FromArgb(163, 42, 76);

        _summary.Text = IsConnected
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0:F1} FPS{1}",
                _preview.IntegratedCaptureFps,
                IsRecording ? " · REC" : "")
            : "Camera disconnected";
    }

    public void Shutdown()
    {
        if (_preview == null || _preview.IsDisposed)
            return;

        try
        {
            if (_preview.IntegratedRecording)
                _preview.IntegratedStopRecording();
        }
        catch
        {
        }

        try
        {
            _preview.IntegratedDisconnectCamera();
        }
        catch
        {
        }

        try
        {
            _preview.Close();
            _preview.Dispose();
        }
        catch
        {
        }
    }

    private void ShowFailure(string title, Exception ex)
    {
        _log.Error("CAMERA", title + ": " + ex.Message);
        MessageBox.Show(
            ex.Message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}

internal sealed class V6PmtModuleControl : UserControl
{
    private readonly Ch297SyncStore _store;
    private readonly V6AppSettings _settings;
    private readonly V6LogService _log;
    private readonly Action _startBridge;
    private readonly Action _stopBridge;
    private readonly Action<double, double> _applyThreshold;

    private readonly ComboBox _comPort;
    private readonly TextBox _portSetting;
    private readonly NumericUpDown _gate;
    private readonly NumericUpDown _threshold;
    private readonly NumericUpDown _hysteresis;
    private readonly Label _count;
    private readonly Label _detail;
    private readonly Label _decision;
    private readonly Label _rate;
    private readonly Label _window;
    private readonly Label _validPct;
    private readonly Label _invalid;
    private readonly V6SignalChart _chart;
    private long _lastSampleIndex;
    private string _lastSampleSession = "";

    public V6PmtModuleControl(
        Ch297SyncStore store,
        V6AppSettings settings,
        V6LogService log,
        Action startBridge,
        Action stopBridge,
        Action<double, double> applyThreshold)
    {
        _store = store;
        _settings = settings;
        _log = log;
        _startBridge = startBridge;
        _stopBridge = stopBridge;
        _applyThreshold = applyThreshold;
        BackColor = V6Theme.Surface;

        TableLayoutPanel pmtShell = new TableLayoutPanel();
        pmtShell.Dock = DockStyle.Fill;
        pmtShell.Margin = Padding.Empty;
        pmtShell.Padding = Padding.Empty;
        pmtShell.ColumnCount = 1;
        pmtShell.RowCount = 3;
        pmtShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        pmtShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F));
        pmtShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 112F));
        pmtShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        Controls.Add(pmtShell);

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
        pmtShell.Controls.Add(commandArea, 0, 0);

        FlowLayoutPanel connectionControls = new FlowLayoutPanel();
        connectionControls.Dock = DockStyle.Fill;
        connectionControls.Margin = Padding.Empty;
        connectionControls.Padding = new Padding(7, 5, 4, 2);
        connectionControls.WrapContents = false;
        connectionControls.AutoScroll = true;
        connectionControls.BackColor = Color.White;
        commandArea.Controls.Add(connectionControls, 0, 0);

        FlowLayoutPanel thresholdControls = new FlowLayoutPanel();
        thresholdControls.Dock = DockStyle.Fill;
        thresholdControls.Margin = Padding.Empty;
        thresholdControls.Padding = new Padding(7, 4, 4, 2);
        thresholdControls.WrapContents = false;
        thresholdControls.AutoScroll = true;
        thresholdControls.BackColor = Color.FromArgb(249, 251, 250);
        commandArea.Controls.Add(thresholdControls, 0, 1);

        connectionControls.Controls.Add(V6Theme.Caption("Serial port"));
        _comPort = V6Theme.Combo();
        _comPort.DropDownStyle = ComboBoxStyle.DropDown;
        _comPort.Width = 82;
        RefreshComPorts(settings.Ch297ComPort);
        connectionControls.Controls.Add(_comPort);

        Button refresh = V6Theme.Button("Refresh", V6Theme.Gray);
        refresh.Click += delegate { RefreshComPorts(ComPort); };
        connectionControls.Controls.Add(refresh);

        connectionControls.Controls.Add(V6Theme.Caption("Gate / ms"));
        _gate = V6Theme.Number(10, 60000, settings.Ch297GateMilliseconds, 0);
        _gate.Width = 68;
        _gate.Increment = 10;
        connectionControls.Controls.Add(_gate);

        Button start = V6Theme.Button("Start counting", V6Theme.Green);
        start.Click += delegate
        {
            PersistSettings();
            _startBridge();
        };
        connectionControls.Controls.Add(start);

        Button stop = V6Theme.Button("Stop", V6Theme.Red);
        stop.Click += delegate { _stopBridge(); };
        connectionControls.Controls.Add(stop);

        thresholdControls.Controls.Add(V6Theme.Caption("Threshold"));
        _threshold = V6Theme.Number(0, 1000000000M, (decimal)settings.Threshold, 0);
        _threshold.Width = 82;
        thresholdControls.Controls.Add(_threshold);

        thresholdControls.Controls.Add(V6Theme.Caption("Hysteresis"));
        _hysteresis = V6Theme.Number(0, 1000000000M, (decimal)settings.Hysteresis, 0);
        _hysteresis.Width = 72;
        thresholdControls.Controls.Add(_hysteresis);

        Button apply = V6Theme.Button("Apply threshold", V6Theme.Amber);
        apply.Click += delegate { ApplyThreshold(); };
        thresholdControls.Controls.Add(apply);

        thresholdControls.Controls.Add(V6Theme.Caption("Serial settings"));
        _portSetting = new TextBox();
        _portSetting.Text = settings.Ch297PortSetting;
        _portSetting.Width = 102;
        _portSetting.Height = 27;
        thresholdControls.Controls.Add(_portSetting);

        Panel reading = new Panel();
        reading.Dock = DockStyle.Fill;
        reading.Margin = Padding.Empty;
        reading.Padding = new Padding(8, 5, 8, 4);
        reading.BackColor = Color.FromArgb(249, 251, 250);
        reading.AutoScroll = true;
        pmtShell.Controls.Add(reading, 0, 1);

        FlowLayoutPanel metrics = new FlowLayoutPanel();
        metrics.Dock = DockStyle.Top;
        metrics.Height = 72;
        metrics.WrapContents = false;
        metrics.AutoScroll = true;
        metrics.Margin = Padding.Empty;
        metrics.Padding = Padding.Empty;
        metrics.BackColor = Color.Transparent;
        reading.Controls.Add(metrics);

        Label countValue;
        Panel countCard = CreateMetricCard(
            "CURRENT COUNT",
            "--",
            190,
            out countValue);
        _count = countValue;
        _count.Font = new Font("Segoe UI", 24F, FontStyle.Bold);
        _count.ForeColor = V6Theme.Green;
        metrics.Controls.Add(countCard);

        Label decisionValue;
        metrics.Controls.Add(CreateMetricCard(
            "PC JUDGE",
            "LOW / Waiting",
            170,
            out decisionValue));
        _decision = decisionValue;

        Label rateValue;
        metrics.Controls.Add(CreateMetricCard(
            "SOURCE RATE",
            "0.0 Hz",
            150,
            out rateValue));
        _rate = rateValue;

        Label windowValue;
        metrics.Controls.Add(CreateMetricCard(
            "TARGET GATE",
            settings.Ch297GateMilliseconds.ToString() + " ms",
            180,
            out windowValue));
        _window = windowValue;

        Label validValue;
        metrics.Controls.Add(CreateMetricCard(
            "VALID %",
            "--",
            110,
            out validValue));
        _validPct = validValue;

        Label invalidValue;
        metrics.Controls.Add(CreateMetricCard(
            "ERRORS",
            "--",
            110,
            out invalidValue));
        _invalid = invalidValue;

        _detail = new Label();
        _detail.Text = "Bridge disconnected";
        _detail.ForeColor = V6Theme.Muted;
        _detail.Dock = DockStyle.Bottom;
        _detail.Height = 24;
        _detail.Padding = new Padding(7, 3, 0, 0);
        reading.Controls.Add(_detail);

        _chart = new V6SignalChart();
        _chart.Dock = DockStyle.Fill;
        _chart.Capacity = 600;
        _chart.Threshold = settings.Threshold;
        pmtShell.Controls.Add(_chart, 0, 2);
    }

    public string ComPort
    {
        get { return _comPort.Text.Trim(); }
    }

    public string PortSetting
    {
        get { return _portSetting.Text.Trim(); }
    }

    public int GateMilliseconds
    {
        get { return Convert.ToInt32(_gate.Value); }
    }

    public double Threshold
    {
        get { return Convert.ToDouble(_threshold.Value); }
    }

    public double Hysteresis
    {
        get { return Convert.ToDouble(_hysteresis.Value); }
    }

    public bool IsConnected
    {
        get { return _store.IsConnected; }
    }

    public Ch297Sample LatestSample
    {
        get { return _store.Latest; }
    }

    public Ch297Sample LatestReceivedSample
    {
        get { return _store.LatestReceived; }
    }

    public void ApplyThreshold()
    {
        if (Hysteresis > Threshold)
        {
            MessageBox.Show(
                "Hysteresis must not exceed the threshold.",
                "Invalid threshold",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        PersistSettings();
        _chart.Threshold = Threshold;
        _applyThreshold(Threshold, Hysteresis);
    }

    public void UpdateSample()
    {
        Ch297Sample sample = _store.Latest;
        if (!_store.IsConnected)
        {
            _count.Text = "--";
            _count.ForeColor = V6Theme.Gray;
            _decision.Text = "LOW / Disconnected";
            _decision.ForeColor = V6Theme.Gray;
            _rate.Text = "0.0 Hz";
            _window.Text = GateMilliseconds.ToString() + " ms | Idle";
            _detail.Text = sample == null
                ? "Bridge disconnected | Camera and pump remain independent"
                : "Bridge disconnected; the last count is logged and excluded from gating.";
            return;
        }

        if (sample == null)
        {
            _count.Text = "--";
            Ch297Sample received = _store.LatestReceived;
            if (received == null)
            {
                _count.ForeColor = V6Theme.Gray;
                _decision.Text = "LOW / Waiting";
                _decision.ForeColor = V6Theme.Gray;
                _rate.Text = _store.ReceiveRate.ToString("0.0") + " Hz";
                _window.Text = GateMilliseconds.ToString() + " ms | Waiting";
                _detail.Text = "Bridge connected; waiting for valid counts in this session.";
            }
            else
            {
                _count.ForeColor = V6Theme.Red;
                _decision.Text = "LOW / Protected";
                _decision.ForeColor = V6Theme.Red;
                _rate.Text = _store.ReceiveRate.ToString("0.0") + " Hz";
                _window.Text = GateMilliseconds.ToString() + " ms · " +
                    LocalStatus(received.Status);
                _detail.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "CH297 invalid data: {0} (raw {1:0}); gate held LOW.",
                    LocalStatus(received.Status),
                    received.Count);
            }
            return;
        }

        _count.Text = sample.Count.ToString("0", CultureInfo.InvariantCulture);
        _count.ForeColor = sample.AboveThreshold
            ? V6Theme.Amber
            : V6Theme.Green;

        _decision.Text = sample.AboveThreshold
            ? "TRIGGER"
            : "LOW";
        _decision.ForeColor = sample.AboveThreshold
            ? V6Theme.Amber
            : V6Theme.Green;
        _rate.Text = _store.ReceiveRate.ToString("0.0") + " Hz";
        long receivedTotal = _store.ReceivedCount;
        long validTotal = _store.ValidReceivedCount;
        double validPct = receivedTotal > 0
            ? 100.0 * validTotal / receivedTotal
            : 0.0;
        _validPct.Text = validPct.ToString("0.00", CultureInfo.InvariantCulture) + " %";
        _invalid.Text = (receivedTotal - validTotal).ToString(CultureInfo.InvariantCulture);
        double nominalRate = 1000.0 / Math.Max(10.0, GateMilliseconds);
        double rateRatio = _store.ReceiveRate / nominalRate;
        _rate.ForeColor = rateRatio >= 0.70
            ? V6Theme.Green
            : (rateRatio >= 0.35 ? V6Theme.Amber : V6Theme.Red);
        _window.Text = GateMilliseconds.ToString() + " ms · " +
            (sample.Status == "OK" ? "Valid" : LocalStatus(sample.Status));

        _detail.Text = string.Format(
            CultureInfo.InvariantCulture,
            "SEQ #{0} | rate {1:0.0} Hz | valid {2:0.00} % | errors {3}",
            sample.SampleIndex,
            _store.ReceiveRate,
            validPct,
            receivedTotal - validTotal);

        if (!string.Equals(
                _lastSampleSession,
                sample.SessionId,
                StringComparison.Ordinal))
        {
            _lastSampleSession = sample.SessionId ?? "";
            _lastSampleIndex = 0;
            _chart.ClearValues();
        }

        if (sample.IsValid)
        {
            System.Collections.Generic.List<Ch297Sample> pending =
                _store.GetValidSamplesAfter(
                    _lastSampleSession,
                    _lastSampleIndex,
                    500);
            for (int i = 0; i < pending.Count; i++)
            {
                _chart.AddValue(pending[i].Count);
                _lastSampleIndex = pending[i].SampleIndex;
            }
        }
    }

    public void PersistSettings()
    {
        _settings.Ch297ComPort = ComPort;
        _settings.Ch297PortSetting = string.IsNullOrEmpty(PortSetting)
            ? "19200,n,8,1"
            : PortSetting;
        _settings.Ch297GateMilliseconds = GateMilliseconds;
        _settings.Threshold = Threshold;
        _settings.Hysteresis = Hysteresis;
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

        if (combo.DropDownStyle == ComboBoxStyle.DropDown)
            combo.Text = text;
        else if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static Panel CreateMetricCard(
        string title,
        string initialValue,
        int width,
        out Label value)
    {
        Panel card = new Panel();
        card.Size = new Size(width, 64);
        card.Margin = new Padding(0, 0, 7, 0);
        card.Padding = Padding.Empty;
        card.BackColor = Color.White;
        card.BorderStyle = BorderStyle.FixedSingle;

        Label caption = new Label();
        caption.Text = title;
        caption.ForeColor = V6Theme.Muted;
        caption.Font = new Font("Segoe UI", 8.5F);
        caption.AutoSize = true;
        caption.Location = new Point(9, 5);
        card.Controls.Add(caption);

        value = new Label();
        value.Text = initialValue;
        value.ForeColor = V6Theme.Green;
        value.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
        value.AutoEllipsis = true;
        value.Location = new Point(8, 24);
        value.Size = new Size(width - 18, 34);
        card.Controls.Add(value);

        return card;
    }

    private void RefreshComPorts(string preferred)
    {
        string[] ports;
        try
        {
            ports = SerialPort.GetPortNames();
        }
        catch (Exception ex)
        {
            ports = new string[0];
            _log.Warning("PMT", "Serial enumeration failed; enter the COM port manually: " + ex.Message);
        }
        Array.Sort(
            ports,
            delegate(string left, string right)
            {
                return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            });

        _comPort.BeginUpdate();
        _comPort.Items.Clear();
        for (int i = 0; i < ports.Length; i++)
            _comPort.Items.Add(ports[i]);
        _comPort.EndUpdate();

        _comPort.Text = string.IsNullOrEmpty(preferred)
            ? (ports.Length > 0 ? ports[0] : "COM7")
            : preferred;

        _log.Info(
            "PMT",
            ports.Length == 0
                ? "No serial ports found. Enter the COM port in the PMT port field."
                : "PMT serial ports refreshed: " + string.Join(", ", ports));
    }

    private static string LocalStatus(string status)
    {
        if (string.Equals(status, "CHECKSUM_ERROR", StringComparison.OrdinalIgnoreCase))
            return "Data checksum error";
        if (string.Equals(status, "NO_DATA", StringComparison.OrdinalIgnoreCase))
            return "Waiting for a complete count";
        if (string.Equals(status, "UNKNOWN_ERROR", StringComparison.OrdinalIgnoreCase))
            return "Unknown acquisition error";
        return string.IsNullOrEmpty(status) ? "No status" : status;
    }
}

internal static class V6BridgeControl
{
    public static string Send(
        int port,
        string command,
        double threshold,
        double hysteresis,
        int timeoutMilliseconds)
    {
        using (TcpClient client = new TcpClient())
        {
            IAsyncResult pending = client.BeginConnect(
                "127.0.0.1",
                port,
                null,
                null);
            System.Threading.WaitHandle waitHandle = pending.AsyncWaitHandle;
            try
            {
                if (!waitHandle.WaitOne(timeoutMilliseconds))
                {
                    client.Close();
                    throw new TimeoutException("CH297 bridge control port did not respond.");
                }

                client.EndConnect(pending);
            }
            finally
            {
                waitHandle.Close();
            }

            object payload;
            if (string.Equals(command, "set_threshold", StringComparison.Ordinal))
            {
                payload = new
                {
                    command = command,
                    threshold = threshold,
                    hysteresis = hysteresis
                };
            }
            else
            {
                payload = new { command = command };
            }

            string line = new JavaScriptSerializer().Serialize(payload) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            NetworkStream stream = client.GetStream();
            stream.ReadTimeout = timeoutMilliseconds;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();

            byte[] response = new byte[4096];
            int received = stream.Read(response, 0, response.Length);
            return Encoding.UTF8.GetString(response, 0, received).Trim();
        }
    }
}
