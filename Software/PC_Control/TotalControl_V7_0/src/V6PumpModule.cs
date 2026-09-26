using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Windows.Forms;

internal sealed class V6PumpModuleControl : UserControl
{
    private readonly V6AppSettings _settings;
    private readonly V6LogService _log;
    private readonly Lsp02PumpController _pump = new Lsp02PumpController();

    private readonly ComboBox _port;
    private readonly ComboBox _baud;
    private readonly ComboBox _parity;
    private readonly NumericUpDown _address;
    private readonly ComboBox _channel;
    private readonly ComboBox _mode;
    private readonly TextBox _syringe;
    private readonly NumericUpDown _volume;
    private readonly ComboBox _volumeUnit;
    private readonly NumericUpDown _infuseTime;
    private readonly ComboBox _infuseUnit;
    private readonly NumericUpDown _withdrawTime;
    private readonly ComboBox _withdrawUnit;
    private readonly NumericUpDown _repeats;
    private readonly NumericUpDown _interval;
    private readonly Label _status;

    public V6PumpModuleControl(
        V6AppSettings settings,
        V6LogService log)
    {
        _settings = settings;
        _log = log;
        BackColor = V6Theme.Surface;

        TableLayoutPanel pumpShell = new TableLayoutPanel();
        pumpShell.Dock = DockStyle.Fill;
        pumpShell.Margin = Padding.Empty;
        pumpShell.Padding = Padding.Empty;
        pumpShell.ColumnCount = 1;
        pumpShell.RowCount = 4;
        pumpShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        pumpShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 78F));
        pumpShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 184F));
        pumpShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 45F));
        pumpShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        Controls.Add(pumpShell);

        FlowLayoutPanel connection = new FlowLayoutPanel();
        connection.Dock = DockStyle.Fill;
        connection.Margin = Padding.Empty;
        connection.Padding = new Padding(7, 5, 4, 2);
        connection.WrapContents = true;
        connection.AutoScroll = true;
        connection.BackColor = Color.White;
        pumpShell.Controls.Add(connection, 0, 0);

        connection.Controls.Add(V6Theme.Caption("Serial port"));
        _port = V6Theme.Combo();
        _port.Width = 76;
        connection.Controls.Add(_port);

        Button refresh = V6Theme.Button("Refresh", V6Theme.Gray);
        refresh.Click += delegate { RefreshPorts(); };
        connection.Controls.Add(refresh);

        connection.Controls.Add(V6Theme.Caption("Baud rate"));
        _baud = V6Theme.Combo();
        _baud.Width = 78;
        _baud.Items.AddRange(new object[] { "1200", "2400", "9600", "19200" });
        SelectText(_baud, settings.PumpBaud.ToString(CultureInfo.InvariantCulture));
        connection.Controls.Add(_baud);

        connection.Controls.Add(V6Theme.Caption("Parity"));
        _parity = V6Theme.Combo();
        _parity.Width = 70;
        _parity.Items.AddRange(new object[] { "Even", "None" });
        SelectText(_parity, settings.PumpParity);
        connection.Controls.Add(_parity);

        connection.Controls.Add(V6Theme.Caption("Address"));
        _address = V6Theme.Number(1, 30, settings.PumpAddress, 0);
        _address.Width = 56;
        connection.Controls.Add(_address);

        Button toggle = V6Theme.Button("Connect / Close", V6Theme.Green);
        toggle.Click += delegate { ToggleConnection(); };
        connection.Controls.Add(toggle);

        Button detect = V6Theme.Button("Autodetect", V6Theme.Blue);
        detect.Click += delegate { AutoDetectConnection(); };
        connection.Controls.Add(detect);

        _status = new Label();
        _status.Text = "Disconnected | Half-duplex RS485 | 2400, Even, 8, 1";
        _status.ForeColor = V6Theme.Muted;
        _status.AutoSize = true;
        _status.Margin = new Padding(10, 8, 3, 0);
        connection.Controls.Add(_status);

        TableLayoutPanel parameters = new TableLayoutPanel();
        parameters.Dock = DockStyle.Fill;
        parameters.Margin = Padding.Empty;
        parameters.Padding = new Padding(10, 8, 10, 4);
        parameters.ColumnCount = 6;
        parameters.RowCount = 4;
        parameters.BackColor = Color.FromArgb(249, 251, 250);
        for (int i = 0; i < 6; i++)
        {
            parameters.ColumnStyles.Add(
                new ColumnStyle(
                    i % 2 == 0 ? SizeType.Absolute : SizeType.Percent,
                    i % 2 == 0 ? 76F : 33.33F));
        }
        for (int i = 0; i < 4; i++)
            parameters.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
        pumpShell.Controls.Add(parameters, 0, 1);

        _channel = V6Theme.Combo();
        _channel.Items.AddRange(new object[] { "1", "2" });
        _channel.SelectedIndex = 0;

        _mode = V6Theme.Combo();
        _mode.Items.AddRange(new object[] {
            "1 Infusion", "2 Withdrawal", "3 Withdraw then infuse", "4 Infuse then withdraw", "5 Continuous"
        });
        _mode.SelectedIndex = 0;

        _syringe = new TextBox();
        _syringe.Text = "22";
        _syringe.Width = 70;

        _volume = V6Theme.Number(0, 65535, 100, 0);
        _volumeUnit = V6Theme.Combo();
        _volumeUnit.Items.AddRange(new object[] {
            "1 nL", "2 0.01uL", "3 0.1uL", "4 uL", "5 0.01mL"
        });
        _volumeUnit.SelectedIndex = 3;

        _infuseTime = V6Theme.Number(0, 65535, 10, 0);
        _infuseUnit = TimeUnitCombo();
        _withdrawTime = V6Theme.Number(0, 65535, 10, 0);
        _withdrawUnit = TimeUnitCombo();
        _repeats = V6Theme.Number(1, 999, 1, 0);
        _interval = V6Theme.Number(0, 65535, 1, 0);

        AddField(parameters, 0, 0, "Channel", _channel);
        AddField(parameters, 2, 0, "Mode", _mode);
        AddField(parameters, 4, 0, "Syringe code", _syringe);
        AddField(parameters, 0, 1, "Volume", _volume);
        AddField(parameters, 2, 1, "Volume unit", _volumeUnit);
        AddField(parameters, 4, 1, "Repeat count", _repeats);
        AddField(parameters, 0, 2, "Infusion time", _infuseTime);
        AddField(parameters, 2, 2, "Infusion unit", _infuseUnit);
        AddField(parameters, 4, 2, "Interval (0.1 min)", _interval);
        AddField(parameters, 0, 3, "Withdrawal time", _withdrawTime);
        AddField(parameters, 2, 3, "Withdraw unit", _withdrawUnit);

        FlowLayoutPanel actions = new FlowLayoutPanel();
        actions.Dock = DockStyle.Fill;
        actions.Margin = Padding.Empty;
        actions.Padding = new Padding(7, 6, 4, 3);
        actions.WrapContents = false;
        actions.AutoScroll = true;
        actions.BackColor = Color.White;
        pumpShell.Controls.Add(actions, 0, 2);

        Button read = V6Theme.Button("Read parameters", V6Theme.Blue);
        read.Click += delegate { ReadParameters(); };
        actions.Controls.Add(read);

        Button write = V6Theme.Button("Write parameters", V6Theme.Amber);
        write.Click += delegate { WriteParameters(); };
        actions.Controls.Add(write);

        Button run = V6Theme.Button("Start CH1", V6Theme.Green);
        run.Click += delegate { StartChannelOne(true); };
        actions.Controls.Add(run);

        Button stop = V6Theme.Button("Stop all", V6Theme.Red);
        stop.Click += delegate { StopAll(true); };
        actions.Controls.Add(stop);

        Button readStatus = V6Theme.Button("Read status", V6Theme.Gray);
        readStatus.Click += delegate { ReadStatus(); };
        actions.Controls.Add(readStatus);

        Label note = new Label();
        note.Dock = DockStyle.Fill;
        note.Margin = Padding.Empty;
        note.Padding = new Padding(12, 12, 12, 6);
        note.Text =
            "Motion control uses the documented CH1 state values 0x03/0x00. " +
            "CH2 parameters can be read and written; CH2 remote motion remains locked.";
        note.ForeColor = Color.FromArgb(142, 82, 33);
        note.BackColor = V6Theme.Surface;
        pumpShell.Controls.Add(note, 0, 3);

        RefreshPorts();
    }

    public bool IsOpen
    {
        get { return _pump.IsOpen; }
    }

    public string PortName
    {
        get { return _pump.PortName; }
    }

    public void ToggleConnection()
    {
        try
        {
            if (_pump.IsOpen)
            {
                try { _pump.StopAll(); } catch { }
                _pump.Close();
                _status.Text = "Disconnected";
                _log.Info("PUMP", "Pump stop requested and serial port closed.");
                return;
            }

            string selectedPort = Convert.ToString(_port.SelectedItem);
            int selectedBaud = Convert.ToInt32(_baud.Text, CultureInfo.InvariantCulture);
            Parity selectedParity = string.Equals(
                _parity.Text,
                "Even",
                StringComparison.OrdinalIgnoreCase)
                ? Parity.Even
                : Parity.None;

            _pump.Open(
                selectedPort,
                selectedBaud,
                selectedParity,
                Convert.ToByte(_address.Value));

            _status.Text = "Serial port open; RSE read-only handshake (up to 3 attempts)...";
            _status.Refresh();
            Lsp02PumpStatus result = _pump.ProbeRunningStatus(3);
            PersistSettings();
            _status.Text = "Connected " + selectedPort + " · " + result.Describe();
            _log.Info("PUMP", "Pump connected: " + _status.Text);
        }
        catch (Exception ex)
        {
            try { _pump.Close(); } catch { }
            ShowFailure("Pump connection failed", ex);
        }
    }

    private void AutoDetectConnection()
    {
        if (_pump.IsOpen)
        {
            MessageBox.Show(
                "Close the current pump serial connection before auto-detecting.",
                "Pump auto-detection",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        string selectedPort = Convert.ToString(_port.SelectedItem);
        if (string.IsNullOrEmpty(selectedPort))
        {
            ShowFailure(
                "Pump auto-detection failed",
                new InvalidOperationException("Select the COM port assigned to the USB-RS485 adapter."));
            return;
        }

        int selectedBaud = Convert.ToInt32(
            _baud.Text,
            CultureInfo.InvariantCulture);
        Parity selectedParity = string.Equals(
            _parity.Text,
            "Even",
            StringComparison.OrdinalIgnoreCase)
            ? Parity.Even
            : Parity.None;

        int[] baudCandidates = new int[] {
            selectedBaud, 2400, 9600, 19200, 1200
        };
        Parity[] parityCandidates = new Parity[] {
            selectedParity,
            selectedParity == Parity.Even ? Parity.None : Parity.Even
        };

        Cursor previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        Exception last = null;
        string tried = "";

        try
        {
            for (int baudIndex = 0; baudIndex < baudCandidates.Length; baudIndex++)
            {
                int baud = baudCandidates[baudIndex];
                bool duplicateBaud = false;
                for (int earlier = 0; earlier < baudIndex; earlier++)
                    duplicateBaud = duplicateBaud || baudCandidates[earlier] == baud;
                if (duplicateBaud)
                    continue;

                for (int parityIndex = 0; parityIndex < parityCandidates.Length; parityIndex++)
                {
                    Parity parity = parityCandidates[parityIndex];
                    string profile = baud.ToString(CultureInfo.InvariantCulture) +
                        ", " + parity.ToString() + ", 8, 1";
                    tried += (tried.Length == 0 ? "" : "; ") + profile;
                    _status.Text = "Read-only probe " + selectedPort + " · " + profile;
                    _status.Refresh();

                    try
                    {
                        _pump.Open(
                            selectedPort,
                            baud,
                            parity,
                            Convert.ToByte(_address.Value));
                        Lsp02PumpStatus result = _pump.ProbeRunningStatus(1);
                        SelectText(
                            _baud,
                            baud.ToString(CultureInfo.InvariantCulture));
                        SelectText(_parity, parity.ToString());
                        PersistSettings();
                        _status.Text =
                            "Detected " + selectedPort + " · " + profile +
                            " · " + result.Describe();
                        _log.Info("PUMP", "Pump auto-detection succeeded: " + _status.Text);
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        try { _pump.Close(); } catch { }
                    }
                }
            }

            throw new IOException(
                "For address " + _address.Value.ToString() +
                ", no valid RSE reply was received using these read-only serial profiles: " + tried +
                ". For semaphore timeouts, reinstall the USB-RS485 driver or replace the adapter. " +
                "If there is no response, check RS485 A/B polarity.",
                last);
        }
        catch (Exception ex)
        {
            try { _pump.Close(); } catch { }
            _status.Text = "Autodetection failed";
            ShowFailure("Pump auto-detection failed", ex);
        }
        finally
        {
            Cursor = previousCursor;
        }
    }

    public bool StartChannelOne(bool askConfirmation)
    {
        try
        {
            if (!_pump.IsOpen)
                throw new InvalidOperationException("Pump is not connected.");

            if (askConfirmation)
            {
                DialogResult answer = MessageBox.Show(
                    "Confirm that the syringe is secured, the tubing is clear and hands are away from the pushrod.\r\n\r\nStart channel 1?",
                    "Start syringe pump",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (answer != DialogResult.Yes)
                    return false;
            }

            _pump.StartDocumentedChannelOne();
            _status.Text = "CH1 run command sent";
            _log.Warning("PUMP", "Pump CH1 run command acknowledged.");
            return true;
        }
        catch (Exception ex)
        {
            if (!askConfirmation)
                throw;

            ShowFailure("Pump start failed", ex);
            return false;
        }
    }

    public bool StopAll(bool showError)
    {
        try
        {
            if (!_pump.IsOpen)
                return true;
            _pump.StopAll();
            _status.Text = "Stop-all command sent";
            _log.Info("PUMP", "Pump stop-all command sent.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("PUMP", "Pump stop failed: " + ex.Message);
            if (showError)
                ShowFailure("Pump stop failed", ex);
            return false;
        }
    }

    public void Shutdown()
    {
        try { StopAll(false); } catch { }
        _pump.Dispose();
    }

    public void PersistSettings()
    {
        _settings.PumpPort = Convert.ToString(_port.SelectedItem);
        _settings.PumpBaud = Convert.ToInt32(_baud.Text, CultureInfo.InvariantCulture);
        _settings.PumpParity = _parity.Text;
        _settings.PumpAddress = Convert.ToInt32(_address.Value);
    }

    private void ReadParameters()
    {
        try
        {
            Lsp02PumpParameters value = _pump.ReadParameters(
                Convert.ToByte(_channel.Text, CultureInfo.InvariantCulture));
            SelectCode(_mode, value.WorkMode);
            _syringe.Text = value.SyringeCode.ToString("X2");
            _volume.Value = value.VolumeValue;
            SelectCode(_volumeUnit, value.VolumeUnit);
            _infuseTime.Value = value.InfusionTimeValue;
            SelectCode(_infuseUnit, value.InfusionTimeUnit);
            _withdrawTime.Value = value.WithdrawalTimeValue;
            SelectCode(_withdrawUnit, value.WithdrawalTimeUnit);
            _repeats.Value = value.RepeatCount;
            _interval.Value = value.IntervalValue;
            _status.Text = "Parameters read: " + value.Describe();
            _log.Info("PUMP", _status.Text);
        }
        catch (Exception ex)
        {
            ShowFailure("Pump parameter read failed", ex);
        }
    }

    private void WriteParameters()
    {
        try
        {
            Lsp02PumpParameters value = BuildParameters();
            DialogResult answer = MessageBox.Show(
                "Parameters to write:\r\n\r\n" + value.Describe() + "\r\n\r\nContinue?",
                "Confirm pump parameters",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
                return;

            _pump.WriteParameters(value);
            _status.Text = "Parameters written";
            _log.Info("PUMP", "Pump parameters written: " + value.Describe());
        }
        catch (Exception ex)
        {
            ShowFailure("Pump parameter write failed", ex);
        }
    }

    private void ReadStatus()
    {
        try
        {
            Lsp02PumpStatus value = _pump.ReadRunningStatus();
            _status.Text = value.Describe();
            _log.Info("PUMP", "Run state: " + value.Describe());
        }
        catch (Exception ex)
        {
            ShowFailure("Pump status read failed", ex);
        }
    }

    private Lsp02PumpParameters BuildParameters()
    {
        byte syringe;
        if (!byte.TryParse(
                _syringe.Text.Trim(),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out syringe))
        {
            throw new InvalidOperationException("Syringe code must contain two hexadecimal digits, for example 22 or 42.");
        }

        return new Lsp02PumpParameters
        {
            Channel = Convert.ToByte(_channel.Text, CultureInfo.InvariantCulture),
            WorkMode = SelectedCode(_mode),
            SyringeCode = syringe,
            VolumeValue = Convert.ToUInt16(_volume.Value),
            VolumeUnit = SelectedCode(_volumeUnit),
            InfusionTimeValue = Convert.ToUInt16(_infuseTime.Value),
            InfusionTimeUnit = SelectedCode(_infuseUnit),
            WithdrawalTimeValue = Convert.ToUInt16(_withdrawTime.Value),
            WithdrawalTimeUnit = SelectedCode(_withdrawUnit),
            RepeatCount = Convert.ToUInt16(_repeats.Value),
            IntervalValue = Convert.ToUInt16(_interval.Value)
        };
    }

    private void RefreshPorts()
    {
        string selected = _port.SelectedItem == null
            ? _settings.PumpPort
            : Convert.ToString(_port.SelectedItem);
        _port.Items.Clear();
        _port.Items.AddRange(Lsp02PumpController.GetAvailablePorts());
        SelectText(_port, selected);
    }

    private static ComboBox TimeUnitCombo()
    {
        ComboBox value = V6Theme.Combo();
        value.Items.AddRange(new object[] { "1 sec", "2 min", "3 hour" });
        value.SelectedIndex = 1;
        return value;
    }

    private static void AddField(
        TableLayoutPanel table,
        int column,
        int row,
        string labelText,
        Control value)
    {
        Label label = new Label();
        label.Text = labelText;
        label.ForeColor = V6Theme.Muted;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Dock = DockStyle.Fill;
        table.Controls.Add(label, column, row);
        value.Dock = DockStyle.Fill;
        value.Margin = new Padding(3, 6, 10, 4);
        table.Controls.Add(value, column + 1, row);
    }

    private static byte SelectedCode(ComboBox combo)
    {
        string text = Convert.ToString(combo.SelectedItem);
        return Convert.ToByte(text.Split(' ')[0], CultureInfo.InvariantCulture);
    }

    private static void SelectCode(ComboBox combo, byte code)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            string text = Convert.ToString(combo.Items[i]);
            if (text.StartsWith(code.ToString() + " ", StringComparison.Ordinal))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
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

    private void ShowFailure(string title, Exception ex)
    {
        _log.Error("PUMP", title + ": " + ex.Message);
        MessageBox.Show(
            ex.Message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
