using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal enum V6ModuleId
{
    Camera,
    Pmt,
    Pynq,
    Pump
}

internal enum V6LogLevel
{
    Info,
    Warning,
    Error
}

internal sealed class V6AppSettings
{
    public V6AppSettings()
    {
    }

    public int SchemaVersion = 1;
    public string LastScene = "All modules";
    public string LayoutMode = "Auto";
    public bool CameraVisible = true;
    public bool PmtVisible = true;
    public bool PynqVisible = true;
    public bool PumpVisible = true;

    public string Ch297ComPort = "COM7";
    public string Ch297PortSetting = "19200,n,8,1";
    public int Ch297GateMilliseconds = 100;
    public int Ch297DataPort = 5101;
    public int Ch297ControlPort = 5102;
    public double Threshold = 15000.0;
    public double Hysteresis = 100.0;

    public string PynqHost = "192.168.2.99";
    public int PynqPort = 5000;
    public int TestPulseMilliseconds = 100;

    public string PumpPort = "";
    public int PumpBaud = 2400;
    public string PumpParity = "Even";
    public int PumpAddress = 1;

    public int RecordingFps = 30;
    public bool ExperimentUsesCamera = true;
    public bool ExperimentUsesPmt = true;
    public bool ExperimentUsesPynq = true;
    public bool ExperimentUsesPump = false;

    public static string SettingsPath
    {
        get
        {
            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "config",
                "total_control_v6.json");
        }
    }

    public static V6AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new V6AppSettings();

            string json = File.ReadAllText(
                SettingsPath,
                Encoding.UTF8);

            V6AppSettings value =
                new JavaScriptSerializer().Deserialize<V6AppSettings>(json);

            if (value == null)
                return new V6AppSettings();

            value.Validate();
            return value;
        }
        catch
        {
            try
            {
                string backup = SettingsPath +
                    ".invalid_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") +
                    ".bak";
                File.Copy(SettingsPath, backup, true);
            }
            catch
            {
            }

            return new V6AppSettings();
        }
    }

    public void Save()
    {
        Validate();

        string directory = Path.GetDirectoryName(SettingsPath);
        Directory.CreateDirectory(directory);

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        string json = serializer.Serialize(this);
        string temporary = SettingsPath + ".tmp";

        File.WriteAllText(
            temporary,
            V6Json.Pretty(json),
            new UTF8Encoding(true));

        if (File.Exists(SettingsPath))
        {
            string backup = SettingsPath + ".bak";
            try
            {
                File.Replace(temporary, SettingsPath, backup, true);
                return;
            }
            catch
            {
                File.Copy(temporary, SettingsPath, true);
                File.Delete(temporary);
                return;
            }
        }

        File.Move(temporary, SettingsPath);
    }

    private void Validate()
    {
        Ch297GateMilliseconds = Clamp(Ch297GateMilliseconds, 10, 60000);
        Ch297GateMilliseconds =
            Math.Max(10, (Ch297GateMilliseconds / 10) * 10);
        Ch297DataPort = Clamp(Ch297DataPort, 1, 65535);
        Ch297ControlPort = Clamp(Ch297ControlPort, 1, 65535);
        PynqPort = Clamp(PynqPort, 1, 65535);
        TestPulseMilliseconds = Clamp(TestPulseMilliseconds, 1, 5000);
        PumpBaud = PumpBaud <= 0 ? 2400 : PumpBaud;
        PumpAddress = Clamp(PumpAddress, 1, 30);
        RecordingFps = Clamp(RecordingFps, 1, 400);
        if (double.IsNaN(Threshold) || double.IsInfinity(Threshold))
            Threshold = 15000.0;
        if (double.IsNaN(Hysteresis) || double.IsInfinity(Hysteresis))
            Hysteresis = 100.0;

        Threshold = Math.Max(0.0, Threshold);
        Hysteresis = Math.Max(0.0, Math.Min(Hysteresis, Threshold));

        if (string.IsNullOrEmpty(LayoutMode))
            LayoutMode = "Auto";

        if (string.IsNullOrEmpty(Ch297PortSetting))
            Ch297PortSetting = "19200,n,8,1";

        if (string.IsNullOrEmpty(PynqHost))
            PynqHost = "192.168.2.99";
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        return Math.Max(minimum, Math.Min(maximum, value));
    }
}

internal sealed class V6LogService
{
    private readonly object _sync = new object();
    private readonly string _directory;

    public event Action<string, V6LogLevel> Logged;

    public V6LogService()
    {
        _directory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "control_logs");
    }

    public void Info(string source, string message)
    {
        Write(source, message, V6LogLevel.Info);
    }

    public void Warning(string source, string message)
    {
        Write(source, message, V6LogLevel.Warning);
    }

    public void Error(string source, string message)
    {
        Write(source, message, V6LogLevel.Error);
    }

    private void Write(string source, string message, V6LogLevel level)
    {
        string line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss.fff}\t{1}\t{2}\t{3}",
            DateTime.Now,
            level.ToString().ToUpperInvariant(),
            source ?? "SYSTEM",
            (message ?? "").Replace("\r", " ").Replace("\n", " "));

        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(
                    Path.Combine(
                        _directory,
                        "NEFU_iDEC_TotalControl_V6_2_" +
                        DateTime.Now.ToString("yyyyMMdd") +
                        ".log"),
                    line + Environment.NewLine,
                    new UTF8Encoding(true));
            }
        }
        catch
        {
        }

        Action<string, V6LogLevel> handler = Logged;
        if (handler != null)
            handler(line, level);
    }
}

internal static class V6Theme
{
    public static readonly Color Canvas = Color.FromArgb(244, 247, 246);
    public static readonly Color Surface = Color.White;
    public static readonly Color Ink = Color.FromArgb(31, 44, 40);
    public static readonly Color Muted = Color.FromArgb(99, 116, 110);
    public static readonly Color Border = Color.FromArgb(218, 227, 223);
    public static readonly Color Green = Color.FromArgb(16, 104, 75);
    public static readonly Color GreenDark = Color.FromArgb(11, 62, 50);
    public static readonly Color GreenSoft = Color.FromArgb(226, 240, 234);
    public static readonly Color Blue = Color.FromArgb(46, 104, 196);
    public static readonly Color Amber = Color.FromArgb(206, 128, 34);
    public static readonly Color Red = Color.FromArgb(191, 58, 61);
    public static readonly Color Gray = Color.FromArgb(105, 117, 113);

    public static Button Button(string text, Color backColor)
    {
        Button button = new Button();
        button.Text = text;
        button.Height = 30;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(10, 0, 10, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = backColor;
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
        return button;
    }

    public static Label Caption(string text)
    {
        Label label = new Label();
        label.Text = text;
        label.AutoSize = true;
        label.ForeColor = Muted;
        label.Margin = new Padding(3, 7, 3, 0);
        return label;
    }

    public static NumericUpDown Number(
        decimal minimum,
        decimal maximum,
        decimal value,
        int decimals)
    {
        NumericUpDown number = new NumericUpDown();
        number.Minimum = minimum;
        number.Maximum = maximum;
        number.DecimalPlaces = decimals;
        number.Value = Math.Max(minimum, Math.Min(maximum, value));
        number.Height = 27;
        number.Width = 92;
        number.ThousandsSeparator = true;
        return number;
    }

    public static ComboBox Combo()
    {
        ComboBox combo = new ComboBox();
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Height = 27;
        return combo;
    }
}

internal static class V6Json
{
    public static string Pretty(string json)
    {
        if (string.IsNullOrEmpty(json))
            return json;

        StringBuilder result = new StringBuilder();
        bool quoted = false;
        bool escaped = false;
        int depth = 0;

        for (int i = 0; i < json.Length; i++)
        {
            char value = json[i];

            if (quoted)
            {
                result.Append(value);
                if (escaped)
                    escaped = false;
                else if (value == '\\')
                    escaped = true;
                else if (value == '"')
                    quoted = false;
                continue;
            }

            if (value == '"')
            {
                quoted = true;
                result.Append(value);
            }
            else if (value == '{' || value == '[')
            {
                result.Append(value);
                result.AppendLine();
                depth++;
                result.Append(new string(' ', depth * 2));
            }
            else if (value == '}' || value == ']')
            {
                result.AppendLine();
                depth = Math.Max(0, depth - 1);
                result.Append(new string(' ', depth * 2));
                result.Append(value);
            }
            else if (value == ',')
            {
                result.Append(value);
                result.AppendLine();
                result.Append(new string(' ', depth * 2));
            }
            else if (value == ':')
            {
                result.Append(": ");
            }
            else if (!char.IsWhiteSpace(value))
            {
                result.Append(value);
            }
        }

        return result.ToString();
    }
}
