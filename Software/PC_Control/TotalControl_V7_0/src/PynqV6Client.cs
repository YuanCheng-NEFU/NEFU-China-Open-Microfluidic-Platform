using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class PynqV6Status
{
    public bool Connected;
    public string Mode = "AUTO";
    public bool GateHigh;
    public double Count;
    public double Threshold;
    public double Hysteresis;
    public long SampleIndex;
    public string Message = "Disconnected";
    public string LastCommand = "";
    public string LastCommandId = "";
    public string LastCommandResult = "Waiting for a control command";
    public string LastCommandTime = "";
    public long GateTransitions;
    public int LastPulseWidthMilliseconds;
    public long SentCount;
    public long AckedCount;
    public long PynqReceived;
    public long PynqMissing;
    public long PynqDuplicates;
    public long PynqOutOfOrder;
    public long PynqInvalidSlots;
    public long PynqParseErrors;
    public long PynqProtocolErrors;
    public long LastAckSeq;
    public double LastAckCount;
    public bool LastAckValid;
    public string LastAckJudge = "";
    public string LastAckGate = "";
    public string LastAckResult = "";
    public double RttMedianMs;
    public double RttP95Ms;
    public double RttP99Ms;
    public double RttMaxMs;
    public double GpioMedianMs;
    public double GpioP95Ms;
    public double GpioP99Ms;
    public double GpioMaxMs;
    public long RttSampleCount;
    public long Connects;
    public DateTime ReceiveTime;

    public PynqV6Status Clone()
    {
        return (PynqV6Status)MemberwiseClone();
    }
}

internal sealed class PynqAckRecord
{
    public string SessionId;
    public long Seq;
    public double RxCount;
    public string Judge;
    public string Gate;
    public double RxToDecideUs;
    public double DecideToGpioUs;
    public double RxToGpioUs;
    public double RxToAckUs;
    public double RttMilliseconds;
    public string Result;
}

internal sealed class SampleTask
{
    public Ch297Sample Sample;
    public double Threshold;
    public double Hysteresis;
}

internal sealed class PynqV6Client : IDisposable
{
    private readonly object _sync = new object();
    private readonly object _sendSync = new object();
    private readonly JavaScriptSerializer _json =
        new JavaScriptSerializer();

    private TcpClient _client;
    private StreamReader _reader;
    private StreamWriter _writer;
    private Thread _receiveThread;
    private volatile bool _stopRequested;
    private long _commandSequence;
    private PynqV6Status _status = new PynqV6Status();
    private Action<PynqV6Status> _statusChanged;

    private readonly ConcurrentQueue<SampleTask> _sendQueue =
        new ConcurrentQueue<SampleTask>();
    private readonly ConcurrentDictionary<long, long> _sendQpcBySeq =
        new ConcurrentDictionary<long, long>();
    private readonly List<double> _rttHistory = new List<double>();
    private readonly List<double> _gpioHistory = new List<double>();
    private Thread _sendThread;
    private volatile bool _sendStop;
    private long _lastMissingReported;
    private long _lastDuplicateReported;
    private long _lastOooReported;
    private long _lastInvalidReported;

    public event Action<PynqAckRecord> AckReceived;
    public event Action<string> IntegrityMilestone;

    public PynqV6Client(Action<PynqV6Status> statusChanged)
    {
        _statusChanged = statusChanged;
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _status.Connected;
            }
        }
    }

    public PynqV6Status LatestStatus
    {
        get
        {
            lock (_sync)
            {
                return _status.Clone();
            }
        }
    }

    public void Connect(
        string host,
        int port,
        int timeoutMilliseconds)
    {
        if (string.IsNullOrEmpty(host))
            throw new ArgumentException("Enter the PYNQ IP address.", "host");

        Disconnect();

        TcpClient client = new TcpClient();
        IAsyncResult pending =
            client.BeginConnect(host, port, null, null);
        WaitHandle waitHandle = pending.AsyncWaitHandle;
        try
        {
            if (!waitHandle.WaitOne(timeoutMilliseconds))
            {
                client.Close();
                throw new TimeoutException(
                    "PYNQ connection timed out. Check the IP, Ethernet connection and board service.");
            }

            client.EndConnect(pending);
        }
        catch
        {
            client.Close();
            throw;
        }
        finally
        {
            waitHandle.Close();
        }
        client.NoDelay = true;

        NetworkStream stream = client.GetStream();
        stream.WriteTimeout = 750;
        StreamReader reader = new StreamReader(
            stream,
            new UTF8Encoding(false),
            false,
            65536);

        StreamWriter writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            65536);

        writer.NewLine = "\n";
        writer.AutoFlush = true;

        lock (_sync)
        {
            _client = client;
            _reader = reader;
            _writer = writer;
            _stopRequested = false;
            _status = new PynqV6Status
            {
                Connected = true,
                Message = "PYNQ connected; awaiting status",
                ReceiveTime = DateTime.Now
            };
        }

        Report();

        ResetSessionStats();

        _receiveThread = new Thread(
            new ThreadStart(ReceiveLoop));
        _receiveThread.IsBackground = true;
        _receiveThread.Name = "NEFU iDEC PYNQ V7 Client";
        _receiveThread.Start();

        StartSendThread();

        SendCommand(
            "hello",
            new Dictionary<string, object>
            {
                { "client", "NEFU-China_iDEC_TotalControl_V6" }
            });
    }

    public void SendSample(
        Ch297Sample sample,
        double threshold,
        double hysteresis)
    {
        if (sample == null || !sample.IsValid)
            return;

        Dictionary<string, object> data =
            new Dictionary<string, object>();

        data["type"] = "sample";
        data["session_id"] = sample.SessionId;
        data["sample_index"] = sample.SampleIndex;
        data["computer_time"] = sample.ComputerTime;
        data["qpc_ticks"] = sample.QpcTicks;
        data["qpc_frequency"] = sample.QpcFrequency;
        data["count"] = sample.Count;
        data["threshold"] = threshold;
        data["hysteresis"] = hysteresis;
        data["status"] = sample.Status;

        SendDictionary(data);
    }

    public void EnqueueSample(
        Ch297Sample sample,
        double threshold,
        double hysteresis)
    {
        if (sample == null)
            return;

        lock (_sync)
        {
            if (!_status.Connected)
                return;
            _status.SentCount++;
        }

        _sendQueue.Enqueue(new SampleTask
        {
            Sample = sample.Clone(),
            Threshold = threshold,
            Hysteresis = hysteresis
        });
    }

    private void StartSendThread()
    {
        _sendStop = false;
        _sendThread = new Thread(SendLoop);
        _sendThread.IsBackground = true;
        _sendThread.Name = "NEFU iDEC PYNQ V7 Sender";
        _sendThread.Start();
    }

    private void SendLoop()
    {
        while (!_sendStop)
        {
            SampleTask task;
            if (!_sendQueue.TryDequeue(out task))
            {
                Thread.Sleep(2);
                continue;
            }

            try
            {
                long sendQpc = Stopwatch.GetTimestamp();
                SendSampleMessage(task);
                _sendQpcBySeq[task.Sample.SampleIndex] = sendQpc;
            }
            catch (Exception ex)
            {
                if (!_sendStop)
                    MarkDisconnected(
                        "PYNQ send failed: " + ex.Message);
                break;
            }
        }
    }

    private void SendSampleMessage(SampleTask task)
    {
        Ch297Sample sample = task.Sample;

        Dictionary<string, object> data =
            new Dictionary<string, object>();

        data["type"] = "sample";
        data["protocol"] = "PYNQ_PERF_V1";
        data["session_id"] = sample.SessionId;
        data["seq"] = sample.SampleIndex;
        data["sample_index"] = sample.SampleIndex;
        data["computer_time"] = sample.ComputerTime;
        data["qpc_ticks"] = sample.QpcTicks;
        data["qpc_frequency"] = sample.QpcFrequency;
        data["count"] = sample.Count;
        data["threshold"] = task.Threshold;
        data["hysteresis"] = task.Hysteresis;
        data["valid"] = sample.IsValid ? 1 : 0;
        data["status"] = sample.Status;
        data["above_threshold"] = sample.AboveThreshold;

        SendDictionary(data);
    }

    public void SetParameters(
        double threshold,
        double hysteresis)
    {
        SendCommand(
            "set_params",
            new Dictionary<string, object>
            {
                { "threshold", threshold },
                { "hysteresis", hysteresis }
            });
    }

    public void SetAutoMode()
    {
        SendCommand("auto", null);
    }

    public void ForceLow()
    {
        SendCommand("force_low", null);
    }

    public void ManualPulse(int widthMilliseconds)
    {
        SendCommand(
            "pulse",
            new Dictionary<string, object>
            {
                { "width_ms", widthMilliseconds }
            });
    }

    public void SendHeartbeat()
    {
        SendCommand(
            "heartbeat",
            new Dictionary<string, object>
            {
                { "computer_time", DateTime.Now.ToString("o") }
            });
    }

    public void Disconnect()
    {
        _stopRequested = true;
        _sendStop = true;

        TcpClient client = null;

        lock (_sync)
        {
            client = _client;
            _client = null;
            _reader = null;
            _writer = null;
            _status.Connected = false;
            _status.GateHigh = false;
            _status.Message = "PYNQ disconnected";
            _status.ReceiveTime = DateTime.Now;
        }

        if (client != null)
        {
            try
            {
                client.Close();
            }
            catch
            {
            }
        }

        if (
            _receiveThread != null &&
            _receiveThread.IsAlive &&
            Thread.CurrentThread != _receiveThread)
        {
            _receiveThread.Join(1500);
        }

        if (
            _sendThread != null &&
            _sendThread.IsAlive &&
            Thread.CurrentThread != _sendThread)
        {
            _sendThread.Join(1500);
        }

        _sendThread = null;

        SampleTask leftover;
        while (_sendQueue.TryDequeue(out leftover))
        {
        }
        _sendQpcBySeq.Clear();

        Report();
    }

    private void SendCommand(
        string command,
        Dictionary<string, object> fields)
    {
        Dictionary<string, object> data =
            fields == null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(fields);

        data["type"] = "command";
        data["command"] = command;
        data["command_id"] = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyyMMddHHmmssfff}-{1}",
            DateTime.UtcNow,
            Interlocked.Increment(ref _commandSequence));
        data["sent_at"] = DateTime.Now.ToString("o");
        SendDictionary(data);
    }

    private void SendDictionary(
        Dictionary<string, object> data)
    {
        string line = _json.Serialize(data);

        lock (_sendSync)
        {
            StreamWriter writer;

            lock (_sync)
            {
                if (!_status.Connected || _writer == null)
                    throw new InvalidOperationException(
                        "PYNQ is not connected.");

                writer = _writer;
            }

            try
            {
                writer.WriteLine(line);
            }
            catch (Exception ex)
            {
                MarkDisconnected(
                    "PYNQ send failed: " + ex.Message);
                throw;
            }
        }
    }

    private void ReceiveLoop()
    {
        try
        {
            while (!_stopRequested)
            {
                StreamReader reader;

                lock (_sync)
                {
                    reader = _reader;
                }

                if (reader == null)
                    break;

                string line = reader.ReadLine();
                if (line == null)
                    break;

                ApplyStatus(line);
            }
        }
        catch (IOException ex)
        {
            if (!_stopRequested)
                MarkDisconnected(
                    "PYNQ connection interrupted: " + ex.Message);
        }
        catch (Exception ex)
        {
            if (!_stopRequested)
                MarkDisconnected(
                    "PYNQ receive exception: " + ex.Message);
        }
        finally
        {
            if (!_stopRequested)
                MarkDisconnected("PYNQ board disconnected");
        }
    }

    private void ApplyStatus(string line)
    {
        Dictionary<string, object> data;

        try
        {
            data = _json.Deserialize<
                Dictionary<string, object>>(line);
        }
        catch
        {
            return;
        }

        string type = GetString(data, "type");
        if (
            string.Equals(
                type,
                "ack",
                StringComparison.OrdinalIgnoreCase))
        {
            ApplyAckData(data);
            return;
        }

        if (
            !string.Equals(
                type,
                "status",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_sync)
        {
            _status.Connected = true;
            _status.Mode = GetString(data, "mode");
            _status.GateHigh = GetBoolean(data, "gate_high");
            _status.Count = GetDouble(data, "count");
            _status.Threshold = GetDouble(data, "threshold");
            _status.Hysteresis = GetDouble(data, "hysteresis");
            _status.SampleIndex = GetInt64(data, "sample_index");
            _status.Message = GetString(data, "message");
            _status.LastCommand = GetString(data, "last_command");
            _status.LastCommandId = GetString(data, "last_command_id");
            _status.LastCommandResult = GetString(data, "last_command_result");
            _status.LastCommandTime = GetString(data, "last_command_time");
            _status.GateTransitions = GetInt64(data, "gate_transitions");
            _status.Connects = GetInt64(data, "connects");
            _status.LastPulseWidthMilliseconds = Convert.ToInt32(
                GetInt64(data, "last_pulse_width_ms"));
            _status.ReceiveTime = DateTime.Now;
        }

        Report();
    }

    private void ApplyAckData(Dictionary<string, object> data)
    {
        long seq = GetInt64(data, "seq");
        double rtt = 0.0;
        long sendQpc;
        if (_sendQpcBySeq.TryRemove(seq, out sendQpc))
        {
            rtt = (Stopwatch.GetTimestamp() - sendQpc) *
                1000.0 / Stopwatch.Frequency;
            lock (_sync)
            {
                _rttHistory.Add(rtt);
                if (_rttHistory.Count > 20000)
                    _rttHistory.RemoveAt(0);
            }
        }

        double rxToGpioUs = GetDouble(data, "rx_to_gpio_us");
        if (rxToGpioUs > 0.0)
        {
            lock (_sync)
            {
                _gpioHistory.Add(rxToGpioUs / 1000.0);
                if (_gpioHistory.Count > 20000)
                    _gpioHistory.RemoveAt(0);
            }
        }

        PynqAckRecord record = new PynqAckRecord();
        record.SessionId = GetString(data, "session_id");
        record.Seq = seq;
        record.RxCount = GetDouble(data, "rx_count");
        record.Judge = GetString(data, "judge");
        record.Gate = GetString(data, "gate");
        record.RxToDecideUs = GetDouble(data, "rx_to_decide_us");
        record.DecideToGpioUs = GetDouble(data, "decide_to_gpio_us");
        record.RxToGpioUs = rxToGpioUs;
        record.RxToAckUs = GetDouble(data, "rx_to_ack_us");
        record.RttMilliseconds = rtt;
        record.Result = GetString(data, "result");

        lock (_sync)
        {
            _status.AckedCount++;
            _status.LastAckSeq = seq;
            _status.LastAckCount = record.RxCount;
            _status.LastAckValid = string.Equals(
                record.Result,
                "SUCCESS",
                StringComparison.OrdinalIgnoreCase);
            _status.LastAckJudge = record.Judge;
            _status.LastAckGate = record.Gate;
            _status.LastAckResult = record.Result;
            _status.PynqReceived = GetInt64(data, "received");
            _status.PynqMissing = GetInt64(data, "missing");
            _status.PynqDuplicates = GetInt64(data, "duplicates");
            _status.PynqOutOfOrder = GetInt64(data, "out_of_order");
            _status.PynqInvalidSlots = GetInt64(data, "invalid_slots");
            _status.PynqParseErrors = GetInt64(data, "parse_errors");
            _status.PynqProtocolErrors = GetInt64(data, "protocol_errors");
            _status.RttSampleCount = _rttHistory.Count;
        }

        RefreshLatencyStats();
        RaiseIntegrityIfNeeded(record);

        Action<PynqAckRecord> handler = AckReceived;
        if (handler != null)
            handler(record);
    }

    private void RefreshLatencyStats()
    {
        long acked;
        lock (_sync)
        {
            acked = _status.AckedCount;
        }

        if (acked < 100 && acked % 10 != 0)
            return;
        if (acked >= 100 && acked % 100 != 0)
            return;

        double rttMedian, rttP95, rttP99, rttMax;
        ComputeLatencyStats(
            _rttHistory,
            out rttMedian,
            out rttP95,
            out rttP99,
            out rttMax);
        double gpioMedian, gpioP95, gpioP99, gpioMax;
        ComputeLatencyStats(
            _gpioHistory,
            out gpioMedian,
            out gpioP95,
            out gpioP99,
            out gpioMax);

        lock (_sync)
        {
            _status.RttMedianMs = rttMedian;
            _status.RttP95Ms = rttP95;
            _status.RttP99Ms = rttP99;
            _status.RttMaxMs = rttMax;
            _status.GpioMedianMs = gpioMedian;
            _status.GpioP95Ms = gpioP95;
            _status.GpioP99Ms = gpioP99;
            _status.GpioMaxMs = gpioMax;
        }
    }

    private void ComputeLatencyStats(
        List<double> history,
        out double median,
        out double p95,
        out double p99,
        out double max)
    {
        double[] values;
        lock (_sync)
        {
            values = history.ToArray();
        }
        Array.Sort(values);
        if (values.Length == 0)
        {
            median = 0.0;
            p95 = 0.0;
            p99 = 0.0;
            max = 0.0;
            return;
        }
        max = values[values.Length - 1];
        median = PercentileSorted(values, 0.50);
        p95 = PercentileSorted(values, 0.95);
        p99 = PercentileSorted(values, 0.99);
    }

    private static double PercentileSorted(double[] values, double p)
    {
        int rank = (int)Math.Ceiling(p * values.Length) - 1;
        rank = Math.Max(0, Math.Min(values.Length - 1, rank));
        return values[rank];
    }

    private void RaiseIntegrityIfNeeded(PynqAckRecord record)
    {
        long acked;
        long missing, duplicates, outOfOrder, invalid;
        lock (_sync)
        {
            acked = _status.AckedCount;
            missing = _status.PynqMissing;
            duplicates = _status.PynqDuplicates;
            outOfOrder = _status.PynqOutOfOrder;
            invalid = _status.PynqInvalidSlots;
        }

        string session = record.SessionId;
        if (acked > 0 && acked % 10000 == 0)
        {
            RaiseIntegrity(
                "session=" + session + " ACK milestone " + acked);
        }
        if (missing > 0 && missing != _lastMissingReported)
        {
            _lastMissingReported = missing;
            RaiseIntegrity(
                "session=" + session + " PYNQ missing seq count=" + missing);
        }
        if (duplicates > 0 && duplicates != _lastDuplicateReported)
        {
            _lastDuplicateReported = duplicates;
            RaiseIntegrity(
                "session=" + session + " PYNQ duplicate=" + duplicates);
        }
        if (outOfOrder > 0 && outOfOrder != _lastOooReported)
        {
            _lastOooReported = outOfOrder;
            RaiseIntegrity(
                "session=" + session + " PYNQ out-of-order=" + outOfOrder);
        }
        if (invalid > 0 && invalid != _lastInvalidReported)
        {
            _lastInvalidReported = invalid;
            RaiseIntegrity(
                "session=" + session + " PYNQ invalid slots=" + invalid);
        }
    }

    private void RaiseIntegrity(string message)
    {
        Action<string> handler = IntegrityMilestone;
        if (handler != null)
            handler(message);
    }

    private void ResetSessionStats()
    {
        SampleTask leftover;
        while (_sendQueue.TryDequeue(out leftover))
        {
        }
        _sendQpcBySeq.Clear();

        lock (_sync)
        {
            _rttHistory.Clear();
            _gpioHistory.Clear();
        }

        lock (_sync)
        {
            _status.SentCount = 0;
            _status.AckedCount = 0;
            _status.PynqReceived = 0;
            _status.PynqMissing = 0;
            _status.PynqDuplicates = 0;
            _status.PynqOutOfOrder = 0;
            _status.PynqInvalidSlots = 0;
            _status.PynqParseErrors = 0;
            _status.PynqProtocolErrors = 0;
            _status.LastAckSeq = 0;
            _status.LastAckCount = 0.0;
            _status.LastAckValid = false;
            _status.LastAckJudge = "";
            _status.LastAckGate = "";
            _status.LastAckResult = "";
            _status.RttMedianMs = 0.0;
            _status.RttP95Ms = 0.0;
            _status.RttP99Ms = 0.0;
            _status.RttMaxMs = 0.0;
            _status.GpioMedianMs = 0.0;
            _status.GpioP95Ms = 0.0;
            _status.GpioP99Ms = 0.0;
            _status.GpioMaxMs = 0.0;
            _status.RttSampleCount = 0;
            _status.Connects = 0;
        }

        _lastMissingReported = 0;
        _lastDuplicateReported = 0;
        _lastOooReported = 0;
        _lastInvalidReported = 0;
    }
    private void MarkDisconnected(string message)
    {
        lock (_sync)
        {
            _status.Connected = false;
            _status.GateHigh = false;
            _status.Message = message;
            _status.ReceiveTime = DateTime.Now;
        }

        Report();
    }

    private void Report()
    {
        Action<PynqV6Status> callback = _statusChanged;

        if (callback != null)
            callback(LatestStatus);
    }

    private static string GetString(
        Dictionary<string, object> data,
        string key)
    {
        object value;
        return data.TryGetValue(key, out value)
            ? Convert.ToString(
                value,
                CultureInfo.InvariantCulture)
            : "";
    }

    private static double GetDouble(
        Dictionary<string, object> data,
        string key)
    {
        object value;
        return data.TryGetValue(key, out value)
            ? Convert.ToDouble(
                value,
                CultureInfo.InvariantCulture)
            : 0.0;
    }

    private static long GetInt64(
        Dictionary<string, object> data,
        string key)
    {
        object value;
        return data.TryGetValue(key, out value)
            ? Convert.ToInt64(
                value,
                CultureInfo.InvariantCulture)
            : 0L;
    }

    private static bool GetBoolean(
        Dictionary<string, object> data,
        string key)
    {
        object value;
        return data.TryGetValue(key, out value) &&
            Convert.ToBoolean(
                value,
                CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        Disconnect();
        _statusChanged = null;
    }
}
