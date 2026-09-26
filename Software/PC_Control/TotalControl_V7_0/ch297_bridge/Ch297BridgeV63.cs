using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class BridgeSettings
{
    private readonly object _sync = new object();
    private double _threshold;
    private double _hysteresis;

    public BridgeSettings(double threshold, double hysteresis)
    {
        Set(threshold, hysteresis);
    }

    public void Set(double threshold, double hysteresis)
    {
        if (
            double.IsNaN(threshold) ||
            double.IsInfinity(threshold) ||
            double.IsNaN(hysteresis) ||
            double.IsInfinity(hysteresis) ||
            threshold < 0.0 ||
            hysteresis < 0.0 ||
            hysteresis > threshold)
        {
            throw new ArgumentOutOfRangeException(
                "threshold",
                "Threshold and hysteresis are outside the valid range.");
        }

        lock (_sync)
        {
            _threshold = threshold;
            _hysteresis = hysteresis;
        }
    }

    public void Get(out double threshold, out double hysteresis)
    {
        lock (_sync)
        {
            threshold = _threshold;
            hysteresis = _hysteresis;
        }
    }
}

internal sealed class BridgeOptions
{
    public string ComPort = "COM7";
    public string PortSetting = "19200,n,8,1";
    public int GateMilliseconds = 100;
    public double Threshold = 15000.0;
    public double Hysteresis = 100.0;
    public string DataHost = "127.0.0.1";
    public int DataPort = 5101;
    public int ControlPort = 5102;
    public string RuntimeDirectory = "";
    public string RootDirectory = "";

    public static BridgeOptions FromEnvironment()
    {
        BridgeOptions value = new BridgeOptions();
        value.ComPort = ReadText("NEFU_CH297_COM", value.ComPort);
        value.PortSetting = ReadText(
            "NEFU_CH297_PORT_SETTING",
            value.PortSetting);
        value.GateMilliseconds = ReadInt(
            "NEFU_CH297_GATE_MS",
            value.GateMilliseconds);
        value.Threshold = ReadDouble(
            "NEFU_CH297_THRESHOLD",
            value.Threshold);
        value.Hysteresis = ReadDouble(
            "NEFU_CH297_HYSTERESIS",
            value.Hysteresis);
        value.DataHost = ReadText(
            "NEFU_CH297_DATA_HOST",
            value.DataHost);
        value.DataPort = ReadInt(
            "NEFU_CH297_DATA_PORT",
            value.DataPort);
        value.ControlPort = ReadInt(
            "NEFU_CH297_CONTROL_PORT",
            value.ControlPort);

        string executableDirectory =
            AppDomain.CurrentDomain.BaseDirectory;
        value.RuntimeDirectory = ReadText(
            "NEFU_CH297_RUNTIME",
            executableDirectory);
        value.RuntimeDirectory = Path.GetFullPath(value.RuntimeDirectory);
        value.RootDirectory = Path.GetFullPath(
            Path.Combine(value.RuntimeDirectory, "..", ".."));

        value.Validate();
        return value;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(ComPort))
            throw new InvalidOperationException("CH297 COM port is empty.");
        if (string.IsNullOrWhiteSpace(PortSetting))
            throw new InvalidOperationException("CH297 port setting is empty.");
        if (GateMilliseconds < 10 || GateMilliseconds > 60000)
            throw new InvalidOperationException(
                "CH297 gate time must be between 10 and 60000 ms.");
        if (GateMilliseconds % 10 != 0)
            throw new InvalidOperationException(
                "CH297 gate time must be a multiple of 10 ms.");
        if (DataPort < 1 || DataPort > 65535)
            throw new InvalidOperationException("Invalid CH297 data port.");
        if (ControlPort < 1 || ControlPort > 65535)
            throw new InvalidOperationException("Invalid CH297 control port.");
        if (!File.Exists(Path.Combine(RuntimeDirectory, "PMTCount.dll")))
            throw new FileNotFoundException(
                "PMTCount.dll was not found.",
                Path.Combine(RuntimeDirectory, "PMTCount.dll"));
        if (!File.Exists(Path.Combine(RuntimeDirectory, "para.ini")))
            throw new FileNotFoundException(
                "para.ini was not found.",
                Path.Combine(RuntimeDirectory, "para.ini"));

        // Reuse the same validation as the live settings object.
        new BridgeSettings(Threshold, Hysteresis);
    }

    private static string ReadText(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int ReadInt(string name, int fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        int parsed;
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out parsed)
            ? parsed
            : fallback;
    }

    private static double ReadDouble(string name, double fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        double parsed;
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out parsed)
            ? parsed
            : fallback;
    }
}

internal sealed class BridgeLog : IDisposable
{
    private readonly object _sync = new object();
    private readonly StreamWriter _writer;

    public BridgeLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        _writer = new StreamWriter(
            path,
            false,
            new UTF8Encoding(true));
        _writer.AutoFlush = true;
    }

    public void Write(string text)
    {
        string line =
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
            "  " + text;
        lock (_sync)
        {
            _writer.WriteLine(line);
        }

        try
        {
            Console.WriteLine(line);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer.Dispose();
        }
    }
}

internal sealed class JsonLinePublisher : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly BridgeLog _log;
    private TcpClient _client;
    private StreamWriter _writer;
    private DateTime _nextConnect = DateTime.MinValue;

    public JsonLinePublisher(string host, int port, BridgeLog log)
    {
        _host = host;
        _port = port;
        _log = log;
    }

    public bool Send(string line)
    {
        if (!ConnectIfDue())
            return false;

        try
        {
            _writer.WriteLine(line);
            return true;
        }
        catch
        {
            CloseConnection();
            _nextConnect = DateTime.UtcNow.AddSeconds(1);
            return false;
        }
    }

    private bool ConnectIfDue()
    {
        if (_client != null && _client.Connected && _writer != null)
            return true;
        if (DateTime.UtcNow < _nextConnect)
            return false;

        TcpClient pendingClient = new TcpClient();
        IAsyncResult pending = null;
        try
        {
            pending = pendingClient.BeginConnect(_host, _port, null, null);
            if (!pending.AsyncWaitHandle.WaitOne(500))
                throw new TimeoutException();
            pendingClient.EndConnect(pending);
            pendingClient.NoDelay = true;
            _client = pendingClient;
            _writer = new StreamWriter(
                _client.GetStream(),
                new UTF8Encoding(false));
            _writer.AutoFlush = true;
            _log.Write(
                "Connected to total control " +
                _host + ":" +
                _port.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch
        {
            try { pendingClient.Close(); }
            catch { }
            _nextConnect = DateTime.UtcNow.AddSeconds(1);
            return false;
        }
        finally
        {
            if (pending != null)
            {
                try { pending.AsyncWaitHandle.Close(); }
                catch { }
            }
        }
    }

    private void CloseConnection()
    {
        if (_writer != null)
        {
            try { _writer.Dispose(); }
            catch { }
            _writer = null;
        }
        if (_client != null)
        {
            try { _client.Close(); }
            catch { }
            _client = null;
        }
    }

    public void Dispose()
    {
        CloseConnection();
    }
}

internal sealed class ControlServer : IDisposable
{
    private readonly int _port;
    private readonly BridgeSettings _settings;
    private readonly Action _requestStop;
    private readonly BridgeLog _log;
    private readonly JavaScriptSerializer _json =
        new JavaScriptSerializer();
    private volatile bool _stopped;
    private TcpListener _listener;
    private Thread _thread;

    public ControlServer(
        int port,
        BridgeSettings settings,
        Action requestStop,
        BridgeLog log)
    {
        _port = port;
        _settings = settings;
        _requestStop = requestStop;
        _log = log;
    }

    public void Start()
    {
        _thread = new Thread(ServerLoop);
        _thread.IsBackground = true;
        _thread.Name = "NEFU CH297 control";
        _thread.Start();
    }

    private void ServerLoop()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start(4);
            _log.Write(
                "Control port 127.0.0.1:" +
                _port.ToString(CultureInfo.InvariantCulture));

            while (!_stopped)
            {
                TcpClient client = null;
                try
                {
                    if (!_listener.Pending())
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    client = _listener.AcceptTcpClient();
                    client.ReceiveTimeout = 700;
                    client.SendTimeout = 700;
                    using (client)
                    using (NetworkStream stream = client.GetStream())
                    using (StreamReader reader = new StreamReader(
                        stream,
                        new UTF8Encoding(false),
                        false,
                        4096,
                        true))
                    using (StreamWriter writer = new StreamWriter(
                        stream,
                        new UTF8Encoding(false),
                        4096,
                        true))
                    {
                        writer.AutoFlush = true;
                        string line = reader.ReadLine();
                        writer.WriteLine(Handle(line));
                    }
                }
                catch (SocketException)
                {
                    if (!_stopped)
                        Thread.Sleep(100);
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stopped)
                _log.Write("Control server error: " + ex.Message);
        }
    }

    private string Handle(string line)
    {
        try
        {
            Dictionary<string, object> command =
                _json.Deserialize<Dictionary<string, object>>(line ?? "");
            object rawCommand;
            string name = command != null &&
                command.TryGetValue("command", out rawCommand)
                ? Convert.ToString(rawCommand, CultureInfo.InvariantCulture)
                : "";

            if (string.Equals(name, "shutdown", StringComparison.Ordinal))
            {
                _requestStop();
                return "{\"ok\":true,\"message\":\"bridge stopping\"}";
            }

            if (string.Equals(name, "set_threshold", StringComparison.Ordinal))
            {
                double threshold = ReadDouble(command, "threshold");
                double hysteresis = ReadDouble(command, "hysteresis");
                _settings.Set(threshold, hysteresis);
                return "{\"ok\":true}";
            }

            if (string.Equals(name, "get_status", StringComparison.Ordinal))
            {
                double threshold;
                double hysteresis;
                _settings.Get(out threshold, out hysteresis);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{{\"ok\":true,\"threshold\":{0:R},\"hysteresis\":{1:R}}}",
                    threshold,
                    hysteresis);
            }

            return "{\"ok\":false,\"error\":\"unknown command\"}";
        }
        catch (Exception ex)
        {
            return _json.Serialize(new Dictionary<string, object>
            {
                { "ok", false },
                { "error", ex.Message }
            });
        }
    }

    private static double ReadDouble(
        Dictionary<string, object> values,
        string key)
    {
        object raw;
        if (!values.TryGetValue(key, out raw))
            throw new InvalidOperationException("Missing " + key + ".");
        return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _stopped = true;
        if (_listener != null)
        {
            try { _listener.Stop(); }
            catch { }
        }
        if (_thread != null && _thread.IsAlive)
            _thread.Join(1000);
    }
}

internal sealed class Ch297Bridge
{
    private readonly BridgeOptions _options;
    private readonly BridgeSettings _settings;
    private readonly JavaScriptSerializer _json =
        new JavaScriptSerializer();
    private volatile bool _stopRequested;

    public Ch297Bridge(BridgeOptions options)
    {
        _options = options;
        _settings = new BridgeSettings(
            options.Threshold,
            options.Hysteresis);
    }

    public int Run()
    {
        Directory.SetCurrentDirectory(_options.RuntimeDirectory);
        string logDirectory = Path.Combine(
            _options.RootDirectory,
            "ch297_logs");
        Directory.CreateDirectory(logDirectory);
        string sessionId =
            "CH297_V63_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string csvPath = Path.Combine(logDirectory, sessionId + ".csv");
        string serviceLogPath = Path.Combine(
            logDirectory,
            "CH297_bridge_latest.log");

        dynamic counter = null;
        bool started = false;
        bool gateHigh = false;

        using (BridgeLog log = new BridgeLog(serviceLogPath))
        using (ControlServer control = new ControlServer(
            _options.ControlPort,
            _settings,
            delegate { _stopRequested = true; },
            log))
        using (JsonLinePublisher publisher = new JsonLinePublisher(
            _options.DataHost,
            _options.DataPort,
            log))
        {
            control.Start();
            try
            {
                log.Write("NEFU-China iDEC CH297 Bridge V6.3 x86");
                log.Write(
                    "Starting " + _options.ComPort + ", " +
                    _options.PortSetting + ", gate=" +
                    _options.GateMilliseconds.ToString(
                        CultureInfo.InvariantCulture) + " ms");

                Type comType = Type.GetTypeFromProgID(
                    "PMTCount.PMTCounter",
                    true);
                counter = Activator.CreateInstance(comType);
                counter.ComPort = _options.ComPort;
                counter.PortSetting = _options.PortSetting;

                // Do not call GetPMTName after continuous acquisition starts.
                // The vendor examples disable device-query controls while
                // counting, and the verified Y7000P program only performs
                // StartContinueCount followed by GetRLU.
                int startResult = Convert.ToInt32(
                    counter.StartContinueCount(
                        _options.GateMilliseconds),
                    CultureInfo.InvariantCulture);
                if (startResult != 0)
                {
                    string reason = startResult == -1
                        ? "device did not respond"
                        : (startResult == -4
                            ? "gate time rejected"
                            : "unknown status");
                    throw new InvalidOperationException(
                        "StartContinueCount returned " +
                        startResult.ToString(CultureInfo.InvariantCulture) +
                        ": " + reason);
                }

                started = true;
                log.Write("Continuous counting started. CSV: " + csvPath);

                using (StreamWriter csv = new StreamWriter(
                    csvPath,
                    false,
                    new UTF8Encoding(true)))
                {
                    // Per-sample disk flushes limited the verified 10 ms gate
                    // stream to roughly one third of its useful rate. Buffer
                    // rows and flush twice per second instead.
                    csv.AutoFlush = false;
                    csv.WriteLine(
                        "sample_index,computer_time,elapsed_s,qpc_ticks," +
                        "qpc_frequency,count,threshold,hysteresis," +
                        "above_threshold,status,valid,total_control_sent");

                    Stopwatch elapsed = Stopwatch.StartNew();
                    Stopwatch csvFlushClock = Stopwatch.StartNew();
                    long sampleIndex = 0;
                    long invalidCount = 0;
                    List<double> elapsedHistory = new List<double>();
                    int invalidRun = 0;

                    while (!_stopRequested)
                    {
                        double count = Convert.ToDouble(
                            counter.GetRLU(),
                            CultureInfo.InvariantCulture);
                        long qpcTicks = Stopwatch.GetTimestamp();
                        DateTime now = DateTime.Now;
                        sampleIndex++;
                        elapsedHistory.Add(elapsed.Elapsed.TotalSeconds);

                        string status = StatusText(count);
                        if (count < 0.0)
                        {
                            gateHigh = false;
                            invalidRun++;
                            invalidCount++;
                        }
                        else
                        {
                            invalidRun = 0;
                            double threshold;
                            double hysteresis;
                            _settings.Get(out threshold, out hysteresis);
                            if (!gateHigh && count >= threshold)
                                gateHigh = true;
                            else if (
                                gateHigh &&
                                count <= threshold - hysteresis)
                                gateHigh = false;
                        }

                        double currentThreshold;
                        double currentHysteresis;
                        _settings.Get(
                            out currentThreshold,
                            out currentHysteresis);

                        Dictionary<string, object> message =
                            new Dictionary<string, object>();
                        message["session_id"] = sessionId;
                        message["protocol"] = "NEFU_CH297_SYNC_V2";
                        message["bridge_version"] = "6.3-csharp-x86";
                        message["pmt_name"] = "CH297";
                        message["gate_ms"] = _options.GateMilliseconds;
                        message["sample_index"] = sampleIndex;
                        message["computer_time"] =
                            now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        message["utc_ticks"] = DateTime.UtcNow.Ticks;
                        message["qpc_ticks"] = qpcTicks;
                        message["qpc_frequency"] = Stopwatch.Frequency;
                        message["elapsed_s"] = elapsed.Elapsed.TotalSeconds;
                        message["count"] = count;
                        message["threshold"] = currentThreshold;
                        message["hysteresis"] = currentHysteresis;
                        message["above_threshold"] = gateHigh;
                        message["status"] = status;
                        message["valid"] = count >= 0.0 ? 1 : 0;

                        bool sent = publisher.Send(_json.Serialize(message));
                        csv.WriteLine(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0},{1},{2:F9},{3},{4},{5:F6},{6:F6},{7:F6},{8},{9},{10},{11}",
                            sampleIndex,
                            now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                            elapsed.Elapsed.TotalSeconds,
                            qpcTicks,
                            Stopwatch.Frequency,
                            count,
                            currentThreshold,
                            currentHysteresis,
                            gateHigh ? 1 : 0,
                            status,
                            count >= 0.0 ? 1 : 0,
                            sent ? 1 : 0));

                        if (csvFlushClock.ElapsedMilliseconds >= 500)
                        {
                            csv.Flush();
                            csvFlushClock.Restart();
                        }

                        if (sampleIndex % 100 == 0)
                        {
                            log.Write(string.Format(
                                CultureInfo.InvariantCulture,
                                "#{0} count={1:F1} threshold={2:F1} " +
                                "state={3} status={4} total_control={5}",
                                sampleIndex,
                                count,
                                currentThreshold,
                                gateHigh ? "HIGH" : "LOW",
                                status,
                                sent ? "OK" : "WAIT"));
                        }

                        if (invalidRun == 20)
                        {
                            log.Write(
                                "20 consecutive invalid readings; " +
                                "check the CH297 connection and acquisition timing.");
                        }

                        // GetRLU already waits for the CH297 count frame. The
                        // vendor demo's additional 10 ms sleep was only used
                        // to keep its own WinForms text box responsive; this
                        // bridge has no UI and must not throw away half of the
                        // available 10 ms samples.
                    }


                    csv.Flush();

                    // V7.0 PERF: write CH297 source summary (session level)
                    WriteSourceSummary(
                        sessionId,
                        logDirectory,
                        elapsedHistory,
                        sampleIndex,
                        invalidCount,
                        elapsed.Elapsed.TotalSeconds,
                        _options.GateMilliseconds,
                        log);
                }
            }
            catch (Exception ex)
            {
                log.Write("ERROR: " + ex);
                return 1;
            }
            finally
            {
                if (counter != null && started)
                {
                    try { counter.StopCounting(); }
                    catch { }
                }
                if (counter != null && Marshal.IsComObject(counter))
                {
                    try { Marshal.FinalReleaseComObject(counter); }
                    catch { }
                }
                counter = null;
                log.Write("Bridge stopped and COM port released.");
            }
        }

        return 0;
    }


    private static double PercentileSorted(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
            return 0.0;
        if (sorted.Count == 1)
            return sorted[0];
        int rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        rank = Math.Max(0, Math.Min(sorted.Count - 1, rank));
        return sorted[rank];
    }

    private static double Average(List<double> values)
    {
        if (values.Count == 0)
            return 0.0;
        double sum = 0.0;
        for (int i = 0; i < values.Count; i++)
            sum += values[i];
        return sum / values.Count;
    }

    private static void WriteSourceSummary(
        string sessionId,
        string logDirectory,
        List<double> elapsedHistory,
        long sourceSlots,
        long invalidSlots,
        double durationSeconds,
        int gateMilliseconds,
        BridgeLog log)
    {
        try
        {
            string summaryPath = Path.Combine(
                logDirectory,
                "CH297_SOURCE_SUMMARY_" + sessionId + ".csv");
            List<double> intervals = new List<double>();
            for (int i = 1; i < elapsedHistory.Count; i++)
                intervals.Add(elapsedHistory[i] - elapsedHistory[i - 1]);
            intervals.Sort();
            double mean = intervals.Count > 0 ? Average(intervals) * 1000.0 : 0.0;
            double rate = durationSeconds > 0.0
                ? (double)sourceSlots / durationSeconds
                : 0.0;
            using (StreamWriter summary = new StreamWriter(
                summaryPath,
                false,
                new UTF8Encoding(true)))
            {
                summary.WriteLine(
                    "session_id,target_gate_ms,source_slots,valid_samples,invalid_slots," +
                    "actual_valid_rate_hz,mean_interval_ms,median_interval_ms," +
                    "p95_interval_ms,p99_interval_ms,max_interval_ms");
                summary.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5:F3},{6:F3},{7:F3},{8:F3},{9:F3},{10:F3}",
                    sessionId,
                    gateMilliseconds,
                    sourceSlots,
                    sourceSlots - invalidSlots,
                    invalidSlots,
                    rate,
                    mean,
                    PercentileSorted(intervals, 0.50) * 1000.0,
                    PercentileSorted(intervals, 0.95) * 1000.0,
                    PercentileSorted(intervals, 0.99) * 1000.0,
                    intervals.Count > 0 ? intervals[intervals.Count - 1] * 1000.0 : 0.0));
            }
            log.Write(
                "SOURCE SUMMARY " + sessionId +
                " slots=" + sourceSlots +
                " invalid=" + invalidSlots +
                " rate=" + rate.ToString("0.00", CultureInfo.InvariantCulture) + " Hz");
        }
        catch (Exception ex)
        {
            log.Write("SOURCE SUMMARY ERROR: " + ex.Message);
        }
    }

    private static string StatusText(double value)
    {
        if (value >= 0.0)
            return "OK";
        if (value == -1.0)
            return "CHECKSUM_ERROR";
        if (value == -2.0)
            return "NO_DATA";
        return "UNKNOWN_ERROR";
    }
}

internal static class Ch297BridgeProgram
{
    [STAThread]
    private static int Main()
    {
        try
        {
            BridgeOptions options = BridgeOptions.FromEnvironment();
            return new Ch297Bridge(options).Run();
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine(ex);
                string path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "CH297_bridge_startup_error.log");
                File.WriteAllText(path, ex.ToString(), new UTF8Encoding(true));
            }
            catch
            {
            }
            return 1;
        }
    }
}
