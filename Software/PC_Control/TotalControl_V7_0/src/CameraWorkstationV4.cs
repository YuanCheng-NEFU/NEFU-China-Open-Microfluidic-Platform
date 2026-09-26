using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Native
{
    private const string DllName = "OEApi64.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetDllDirectory(string lpPathName);

    [DllImport(DllName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int KSJ_Init();

    [DllImport(DllName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int KSJ_UnInit();

    [DllImport(DllName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int KSJ_DeviceGetCount();

    [DllImport(DllName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int KSJ_DeviceGetInformation(
        int deviceIndex,
        out ushort deviceType,
        out int serialNumber,
        out ushort firmwareVersion);

    [DllImport(DllName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int KSJ_CaptureGetSizeEx(
        int deviceIndex,
        out int width,
        out int height,
        out int bitCount);

    [DllImport(DllName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int KSJ_CaptureRgbData(
        int deviceIndex,
        IntPtr buffer);

    [DllImport(DllName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int KSJ_ExposureTimeGet(
        int deviceIndex,
        out float exposureMs);

    [DllImport(DllName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int KSJ_ExposureTimeSet(
        int deviceIndex,
        float exposureMs);
}


internal sealed class Ch297Sample
{
    public string SessionId;
    public string Protocol;
    public long SampleIndex;
    public string ComputerTime;
    public long UtcTicks;
    public long QpcTicks;
    public long QpcFrequency;
    public double ElapsedSeconds;
    public double Count;
    public double Threshold;
    public double Hysteresis;
    public bool AboveThreshold;
    public string Status;
    public DateTime ReceiveTime;
    public long ReceiveQpcTicks;
    public double TransportMilliseconds;

    public bool IsValid
    {
        get
        {
            return
                Count >= 0.0 &&
                string.Equals(
                    Status,
                    "OK",
                    StringComparison.OrdinalIgnoreCase) &&
                QpcTicks > 0 &&
                QpcFrequency > 0;
        }
    }

    public Ch297Sample Clone()
    {
        return (Ch297Sample)MemberwiseClone();
    }
}

internal sealed class Ch297Match
{
    public bool Bound;
    public Ch297Sample Sample;
    public double FrameMinusSampleMilliseconds;
}

internal sealed class Ch297SyncStore
{
    private readonly object _sync = new object();
    private readonly List<Ch297Sample> _samples =
        new List<Ch297Sample>();
    private readonly AutoResetEvent _sampleArrived =
        new AutoResetEvent(false);

    public event Action<Ch297Sample> SampleReceived;

    private bool _connected;
    private long _received;
    private long _validReceived;
    private long _sequenceGaps;
    private long _lastSampleIndex;
    private string _lastSessionId = "";
    private Ch297Sample _latest;
    private Ch297Sample _latestReceived;
    private double _rate;
    private long _rateCounter;
    private readonly Stopwatch _rateClock =
        Stopwatch.StartNew();

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _connected;
            }
        }
    }

    public long ReceivedCount
    {
        get
        {
            lock (_sync)
            {
                return _received;
            }
        }
    }

    public long ValidReceivedCount
    {
        get
        {
            lock (_sync)
            {
                return _validReceived;
            }
        }
    }

    public long SequenceGaps
    {
        get
        {
            lock (_sync)
            {
                return _sequenceGaps;
            }
        }
    }

    public double ReceiveRate
    {
        get
        {
            lock (_sync)
            {
                return _rate;
            }
        }
    }

    public Ch297Sample Latest
    {
        get
        {
            lock (_sync)
            {
                return _latest == null
                    ? null
                    : _latest.Clone();
            }
        }
    }

    public Ch297Sample LatestReceived
    {
        get
        {
            lock (_sync)
            {
                return _latestReceived == null
                    ? null
                    : _latestReceived.Clone();
            }
        }
    }

    public List<Ch297Sample> GetValidSamplesAfter(
        string sessionId,
        long sampleIndex,
        int maximum)
    {
        List<Ch297Sample> result = new List<Ch297Sample>();
        maximum = Math.Max(1, Math.Min(1000, maximum));

        lock (_sync)
        {
            for (int i = 0; i < _samples.Count; i++)
            {
                Ch297Sample sample = _samples[i];
                if (!string.Equals(
                        sample.SessionId,
                        sessionId,
                        StringComparison.Ordinal))
                    continue;
                if (sample.SampleIndex <= sampleIndex)
                    continue;

                result.Add(sample.Clone());
                if (result.Count >= maximum)
                    break;
            }
        }

        return result;
    }

    public void SetConnected(bool connected)
    {
        lock (_sync)
        {
            if (connected && !_connected)
            {
                // A reconnect must not expose the previous session's last
                // count as a fresh sample to the PYNQ gate controller.
                _latest = null;
                _latestReceived = null;
                _rate = 0.0;
                _rateCounter = 0;
                _rateClock.Restart();
            }
            _connected = connected;
        }

        _sampleArrived.Set();
    }

    public void Add(Ch297Sample sample)
    {
        if (sample == null)
            return;

        lock (_sync)
        {
            _received++;
            _latestReceived = sample;

            if (
                !string.Equals(
                    _lastSessionId,
                    sample.SessionId,
                    StringComparison.Ordinal))
            {
                _lastSessionId =
                    sample.SessionId ?? "";
                _lastSampleIndex = 0;
            }

            if (
                _lastSampleIndex > 0 &&
                sample.SampleIndex >
                    _lastSampleIndex + 1)
            {
                _sequenceGaps +=
                    sample.SampleIndex -
                    _lastSampleIndex -
                    1;
            }

            _lastSampleIndex =
                sample.SampleIndex;

            if (sample.IsValid)
            {
                _validReceived++;
                _rateCounter++;
                _samples.Add(sample);

                while (_samples.Count > 10000)
                {
                    _samples.RemoveAt(0);
                }

                _latest = sample;
            }

            if (_rateClock.ElapsedMilliseconds >= 1000)
            {
                double seconds =
                    _rateClock.Elapsed.TotalSeconds;

                _rate =
                    seconds > 0.0
                    ? _rateCounter / seconds
                    : 0.0;

                _rateCounter = 0;
                _rateClock.Restart();
            }
        }

        _sampleArrived.Set();

        Action<Ch297Sample> handler = SampleReceived;
        if (handler != null)
            handler(sample);
    }

    public Ch297Match MatchClosest(
        long frameQpcTicks,
        int waitForFutureMilliseconds)
    {
        if (frameQpcTicks <= 0)
        {
            return new Ch297Match();
        }

        Stopwatch waitClock =
            Stopwatch.StartNew();

        while (true)
        {
            bool shouldWait = false;

            lock (_sync)
            {
                if (
                    _connected &&
                    _samples.Count > 0 &&
                    _samples[_samples.Count - 1].QpcTicks <
                        frameQpcTicks &&
                    waitClock.ElapsedMilliseconds <
                        waitForFutureMilliseconds)
                {
                    shouldWait = true;
                }
            }

            if (!shouldWait)
                break;

            int remaining =
                waitForFutureMilliseconds -
                (int)waitClock.ElapsedMilliseconds;

            if (remaining <= 0)
                break;

            _sampleArrived.WaitOne(
                Math.Min(remaining, 5));
        }

        lock (_sync)
        {
            if (_samples.Count == 0)
            {
                return new Ch297Match();
            }

            Ch297Sample best = null;
            long bestDistance =
                long.MaxValue;

            // Search backwards because the closest sample is normally near
            // the newest end of the buffer.
            for (
                int i = _samples.Count - 1;
                i >= 0;
                i--)
            {
                Ch297Sample candidate =
                    _samples[i];

                long distance =
                    Math.Abs(
                        frameQpcTicks -
                        candidate.QpcTicks);

                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
                else if (
                    best != null &&
                    candidate.QpcTicks <
                        frameQpcTicks &&
                    distance >
                        bestDistance)
                {
                    break;
                }
            }

            if (best == null)
            {
                return new Ch297Match();
            }

            long frequency =
                best.QpcFrequency > 0
                ? best.QpcFrequency
                : Stopwatch.Frequency;

            double deltaMilliseconds =
                (
                    frameQpcTicks -
                    best.QpcTicks
                ) *
                1000.0 /
                frequency;

            if (
                Math.Abs(
                    deltaMilliseconds) >
                100.0)
            {
                return new Ch297Match();
            }

            return new Ch297Match
            {
                Bound = true,
                Sample = best.Clone(),
                FrameMinusSampleMilliseconds =
                    deltaMilliseconds
            };
        }
    }

    public void Dispose()
    {
        _sampleArrived.Dispose();
    }
}

internal sealed class Ch297SyncServer : IDisposable
{
    private readonly Ch297SyncStore _store;
    private readonly Action<string> _statusCallback;
    private readonly JavaScriptSerializer _json =
        new JavaScriptSerializer();
    private readonly object _clientLock =
        new object();

    private Thread _thread;
    private volatile bool _stopRequested;
    private TcpListener _listener;
    private TcpClient _activeClient;
    private int _port;
    private StreamWriter _csv;
    private string _csvPath = "";
    private readonly Stopwatch _csvFlushClock = Stopwatch.StartNew();

    public Ch297SyncServer(
        Ch297SyncStore store,
        Action<string> statusCallback)
    {
        _store = store;
        _statusCallback = statusCallback;
    }

    public string CsvPath
    {
        get { return _csvPath; }
    }

    public void Start(
        int port,
        string logDirectory)
    {
        if (
            _thread != null &&
            _thread.IsAlive)
        {
            return;
        }

        _port = port;
        _stopRequested = false;

        Directory.CreateDirectory(
            logDirectory);

        _csvPath =
            Path.Combine(
                logDirectory,
                "CH297_SYNC_RECEIVE_V6_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss") +
                ".csv");

        _csv =
            new StreamWriter(
                _csvPath,
                false,
                new UTF8Encoding(true));

        _csv.AutoFlush = false;
        _csvFlushClock.Restart();

        _csv.WriteLine(
            "receive_index,receive_time,receive_qpc_ticks," +
            "transport_ms,session_id,protocol,sample_index," +
            "sender_time,sender_utc_ticks,sender_qpc_ticks," +
            "qpc_frequency,elapsed_s,count,threshold,hysteresis," +
            "above_threshold,status,valid");

        _thread =
            new Thread(
                new ThreadStart(
                    ServerLoop));

        _thread.IsBackground = true;
        _thread.Name =
            "MUS40M-G CH297 Sync Server";

        _thread.Start();
    }

    public void Stop()
    {
        _stopRequested = true;

        lock (_clientLock)
        {
            if (_activeClient != null)
            {
                try
                {
                    _activeClient.Close();
                }
                catch
                {
                }

                _activeClient = null;
            }
        }

        if (_listener != null)
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
            }
        }

        if (
            _thread != null &&
            _thread.IsAlive)
        {
            _thread.Join(3000);
        }

        _thread = null;

        if (_csv != null)
        {
            try
            {
                _csv.Flush();
                _csv.Close();
            }
            catch
            {
            }

            _csv = null;
        }

        _store.SetConnected(false);
    }

    private void ServerLoop()
    {
        long receiveIndex = 0;

        try
        {
            _listener =
                new TcpListener(
                    IPAddress.Loopback,
                    _port);

            _listener.Start(1);

            Report(
                "CH297 sync: listening on 127.0.0.1:" +
                _port.ToString(
                    CultureInfo.InvariantCulture));

            while (!_stopRequested)
            {
                TcpClient client = null;

                try
                {
                    client =
                        _listener.AcceptTcpClient();

                    client.NoDelay = true;

                    lock (_clientLock)
                    {
                        _activeClient = client;
                    }

                    _store.SetConnected(true);

                    Report(
                        "CH297 sync: sender connected");

                    using (client)
                    using (
                        NetworkStream stream =
                            client.GetStream())
                    using (
                        StreamReader reader =
                            new StreamReader(
                                stream,
                                new UTF8Encoding(false),
                                false,
                                65536))
                    {
                        while (!_stopRequested)
                        {
                            string line =
                                reader.ReadLine();

                            if (line == null)
                                break;

                            DateTime receiveTime =
                                DateTime.Now;

                            long receiveQpc =
                                Stopwatch.GetTimestamp();

                            Ch297Sample sample =
                                Parse(
                                    line,
                                    receiveTime,
                                    receiveQpc);

                            if (sample == null)
                                continue;

                            receiveIndex++;

                            if (_csv != null)
                            {
                                lock (_csv)
                                {
                                    _csv.WriteLine(
                                        string.Format(
                                            CultureInfo.InvariantCulture,
                                            "{0},{1},{2},{3:F6},{4},{5},{6},{7},{8},{9},{10},{11:F9},{12:F6},{13:F6},{14:F6},{15},{16},{17}",
                                            receiveIndex,
                                            receiveTime.ToString(
                                                "yyyy-MM-dd HH:mm:ss.fff"),
                                            receiveQpc,
                                            sample.TransportMilliseconds,
                                            EscapeCsv(sample.SessionId),
                                            EscapeCsv(sample.Protocol),
                                            sample.SampleIndex,
                                            EscapeCsv(sample.ComputerTime),
                                            sample.UtcTicks,
                                            sample.QpcTicks,
                                            sample.QpcFrequency,
                                            sample.ElapsedSeconds,
                                            sample.Count,
                                            sample.Threshold,
                                            sample.Hysteresis,
                                            sample.AboveThreshold ? 1 : 0,
                                            EscapeCsv(sample.Status),
                                            sample.IsValid ? 1 : 0));

                                    if (_csvFlushClock.ElapsedMilliseconds >= 500)
                                    {
                                        _csv.Flush();
                                        _csvFlushClock.Restart();
                                    }
                                }
                            }

                            _store.Add(sample);
                        }
                    }
                }
                catch (SocketException)
                {
                    if (!_stopRequested)
                    {
                        Report(
                            "CH297 sync: network error; waiting for reconnection");
                    }
                }
                catch (IOException)
                {
                    if (!_stopRequested)
                    {
                        Report(
                            "CH297 sync: connection lost; waiting for reconnection");
                    }
                }
                finally
                {
                    lock (_clientLock)
                    {
                        if (
                            object.ReferenceEquals(
                                _activeClient,
                                client))
                        {
                            _activeClient = null;
                        }
                    }

                    _store.SetConnected(false);

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
                }

                if (!_stopRequested)
                {
                    Report(
                        "CH297 sync: sender disconnected; listener remains active");
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stopRequested)
            {
                Report(
                    "CH297 sync error: " +
                    ex.Message);
            }
        }
        finally
        {
            if (_listener != null)
            {
                try
                {
                    _listener.Stop();
                }
                catch
                {
                }

                _listener = null;
            }

            _store.SetConnected(false);

            Report(
                "CH297 sync: listener stopped");
        }
    }

    private Ch297Sample Parse(
        string line,
        DateTime receiveTime,
        long receiveQpc)
    {
        try
        {
            Dictionary<string, object> data =
                _json.Deserialize<
                    Dictionary<string, object>>(
                        line);

            Ch297Sample sample =
                new Ch297Sample();

            sample.SessionId =
                GetString(
                    data,
                    "session_id");

            if (
                string.IsNullOrEmpty(
                    sample.SessionId))
            {
                sample.SessionId =
                    "LEGACY";
            }

            sample.Protocol =
                GetString(
                    data,
                    "protocol");

            sample.SampleIndex =
                GetInt64(
                    data,
                    "sample_index");

            sample.ComputerTime =
                GetString(
                    data,
                    "computer_time");

            sample.UtcTicks =
                GetInt64(
                    data,
                    "utc_ticks");

            sample.QpcTicks =
                GetInt64(
                    data,
                    "qpc_ticks");

            sample.QpcFrequency =
                GetInt64(
                    data,
                    "qpc_frequency");

            sample.ElapsedSeconds =
                GetDouble(
                    data,
                    "elapsed_s");

            sample.Count =
                GetDouble(
                    data,
                    "count");

            sample.Threshold =
                GetDouble(
                    data,
                    "threshold");

            sample.Hysteresis =
                GetDouble(
                    data,
                    "hysteresis");

            sample.AboveThreshold =
                GetBoolean(
                    data,
                    "above_threshold");

            sample.Status =
                GetString(
                    data,
                    "status");

            sample.ReceiveTime =
                receiveTime;

            sample.ReceiveQpcTicks =
                receiveQpc;

            if (
                sample.QpcFrequency > 0 &&
                sample.QpcTicks > 0)
            {
                sample.TransportMilliseconds =
                    (
                        receiveQpc -
                        sample.QpcTicks
                    ) *
                    1000.0 /
                    sample.QpcFrequency;
            }

            return sample;
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(
        Dictionary<string, object> data,
        string key)
    {
        object value;

        return data.TryGetValue(
            key,
            out value)
            ? Convert.ToString(
                value,
                CultureInfo.InvariantCulture)
            : "";
    }

    private static long GetInt64(
        Dictionary<string, object> data,
        string key)
    {
        object value;

        return data.TryGetValue(
            key,
            out value)
            ? Convert.ToInt64(
                value,
                CultureInfo.InvariantCulture)
            : 0L;
    }

    private static double GetDouble(
        Dictionary<string, object> data,
        string key)
    {
        object value;

        return data.TryGetValue(
            key,
            out value)
            ? Convert.ToDouble(
                value,
                CultureInfo.InvariantCulture)
            : 0.0;
    }

    private static bool GetBoolean(
        Dictionary<string, object> data,
        string key)
    {
        object value;

        return data.TryGetValue(
            key,
            out value) &&
            Convert.ToBoolean(
                value,
                CultureInfo.InvariantCulture);
    }

    private static string EscapeCsv(
        string value)
    {
        if (value == null)
            return "";

        if (
            value.IndexOf(',') < 0 &&
            value.IndexOf('"') < 0 &&
            value.IndexOf('\r') < 0 &&
            value.IndexOf('\n') < 0)
        {
            return value;
        }

        return
            "\"" +
            value.Replace(
                "\"",
                "\"\"") +
            "\"";
    }

    private void Report(
        string text)
    {
        if (_statusCallback != null)
        {
            _statusCallback(text);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

internal sealed class RecordedFrame
{
    public byte[] Data;
    public long SourceFrameNumber;
    public long CaptureTick;
    public DateTime ComputerTime;
    public float ExposureMs;
}

internal sealed class AviIndexEntry
{
    public uint Flags;
    public uint Offset;
    public uint Size;
}

internal sealed class AviGray8Writer : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _stride;
    private readonly int _frameSize;
    private readonly byte[] _bottomUpBuffer;
    private readonly List<AviIndexEntry> _index =
        new List<AviIndexEntry>();

    private long _riffSizePosition;
    private long _avihTotalFramesPosition;
    private long _strhLengthPosition;
    private long _moviSizePosition;
    private long _moviTypePosition;
    private bool _closed;
    private int _frameCount;

    public AviGray8Writer(
        string path,
        int width,
        int height,
        int fps)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException("width/height");

        if (fps <= 0 || fps > 1000)
            throw new ArgumentOutOfRangeException("fps");

        _width = width;
        _height = height;
        _fps = fps;
        _stride = (width + 3) & ~3;
        _frameSize = checked(_stride * height);
        _bottomUpBuffer = new byte[_frameSize];

        _stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read);

        _writer = new BinaryWriter(
            _stream,
            Encoding.ASCII);

        WriteHeader();
    }

    public int FrameCount
    {
        get { return _frameCount; }
    }

    public long FileLength
    {
        get { return _stream.Length; }
    }

    public int FrameSize
    {
        get { return _frameSize; }
    }

    private void WriteHeader()
    {
        WriteFourCC("RIFF");
        _riffSizePosition = _stream.Position;
        _writer.Write((uint)0);
        WriteFourCC("AVI ");

        WriteFourCC("LIST");
        long hdrlSizePosition = _stream.Position;
        _writer.Write((uint)0);
        long hdrlDataStart = _stream.Position;
        WriteFourCC("hdrl");

        WriteFourCC("avih");
        _writer.Write((uint)56);
        _writer.Write(
            (uint)Math.Max(
                1,
                (int)Math.Round(1000000.0 / _fps)));
        _writer.Write(
            (uint)checked(_frameSize * _fps));
        _writer.Write((uint)0);
        _writer.Write((uint)0x10);
        _avihTotalFramesPosition = _stream.Position;
        _writer.Write((uint)0);
        _writer.Write((uint)0);
        _writer.Write((uint)1);
        _writer.Write((uint)_frameSize);
        _writer.Write((uint)_width);
        _writer.Write((uint)_height);
        _writer.Write((uint)0);
        _writer.Write((uint)0);
        _writer.Write((uint)0);
        _writer.Write((uint)0);

        WriteFourCC("LIST");
        long strlSizePosition = _stream.Position;
        _writer.Write((uint)0);
        long strlDataStart = _stream.Position;
        WriteFourCC("strl");

        WriteFourCC("strh");
        _writer.Write((uint)56);
        WriteFourCC("vids");
        WriteFourCC("DIB ");
        _writer.Write((uint)0);
        _writer.Write((ushort)0);
        _writer.Write((ushort)0);
        _writer.Write((uint)0);
        _writer.Write((uint)1);
        _writer.Write((uint)_fps);
        _writer.Write((uint)0);
        _strhLengthPosition = _stream.Position;
        _writer.Write((uint)0);
        _writer.Write((uint)_frameSize);
        _writer.Write((uint)0xFFFFFFFF);
        _writer.Write((uint)0);
        _writer.Write((short)0);
        _writer.Write((short)0);
        _writer.Write((short)_width);
        _writer.Write((short)_height);

        WriteFourCC("strf");
        _writer.Write((uint)(40 + 256 * 4));
        _writer.Write((uint)40);
        _writer.Write(_width);
        _writer.Write(_height);
        _writer.Write((ushort)1);
        _writer.Write((ushort)8);
        _writer.Write((uint)0);
        _writer.Write((uint)_frameSize);
        _writer.Write(3780);
        _writer.Write(3780);
        _writer.Write((uint)256);
        _writer.Write((uint)256);

        for (int i = 0; i < 256; i++)
        {
            _writer.Write((byte)i);
            _writer.Write((byte)i);
            _writer.Write((byte)i);
            _writer.Write((byte)0);
        }

        long afterStrl = _stream.Position;
        PatchUInt32(
            strlSizePosition,
            checked((uint)(afterStrl - strlDataStart)));

        long afterHdrl = _stream.Position;
        PatchUInt32(
            hdrlSizePosition,
            checked((uint)(afterHdrl - hdrlDataStart)));

        WriteFourCC("LIST");
        _moviSizePosition = _stream.Position;
        _writer.Write((uint)0);
        _moviTypePosition = _stream.Position;
        WriteFourCC("movi");
    }

    public void WriteFrame(byte[] grayTopDown)
    {
        if (_closed)
            throw new ObjectDisposedException("AviGray8Writer");

        int required = checked(_width * _height);
        if (grayTopDown == null || grayTopDown.Length < required)
            throw new ArgumentException("Frame buffer is too small.");

        Array.Clear(
            _bottomUpBuffer,
            0,
            _bottomUpBuffer.Length);

        for (int sourceRow = 0; sourceRow < _height; sourceRow++)
        {
            int destinationRow =
                _height - 1 - sourceRow;

            Buffer.BlockCopy(
                grayTopDown,
                sourceRow * _width,
                _bottomUpBuffer,
                destinationRow * _stride,
                _width);
        }

        long chunkPosition = _stream.Position;

        WriteFourCC("00db");
        _writer.Write((uint)_frameSize);
        _writer.Write(
            _bottomUpBuffer,
            0,
            _bottomUpBuffer.Length);

        if ((_frameSize & 1) != 0)
            _writer.Write((byte)0);

        _index.Add(new AviIndexEntry
        {
            Flags = 0x10,
            Offset = checked(
                (uint)(chunkPosition - _moviTypePosition)),
            Size = (uint)_frameSize
        });

        _frameCount++;
    }

    public void Close()
    {
        if (_closed)
            return;

        long beforeIndex = _stream.Position;

        PatchUInt32(
            _moviSizePosition,
            checked(
                (uint)(
                    beforeIndex -
                    (_moviSizePosition + 4))));

        WriteFourCC("idx1");
        _writer.Write(
            checked((uint)(_index.Count * 16)));

        for (int i = 0; i < _index.Count; i++)
        {
            AviIndexEntry entry = _index[i];
            WriteFourCC("00db");
            _writer.Write(entry.Flags);
            _writer.Write(entry.Offset);
            _writer.Write(entry.Size);
        }

        long finalLength = _stream.Position;

        PatchUInt32(
            _avihTotalFramesPosition,
            (uint)_frameCount);

        PatchUInt32(
            _strhLengthPosition,
            (uint)_frameCount);

        PatchUInt32(
            _riffSizePosition,
            checked((uint)(finalLength - 8)));

        _writer.Flush();
        _stream.Flush(true);
        _closed = true;

        _writer.Close();
        _stream.Close();
    }

    private void PatchUInt32(long position, uint value)
    {
        long current = _stream.Position;
        _stream.Position = position;
        _writer.Write(value);
        _stream.Position = current;
    }

    private void WriteFourCC(string value)
    {
        if (value == null || value.Length != 4)
            throw new ArgumentException("FourCC must contain four characters.");

        byte[] bytes = Encoding.ASCII.GetBytes(value);
        _writer.Write(bytes, 0, 4);
    }

    public void Dispose()
    {
        Close();
    }
}

internal static class ReviewFrameRenderer
{
    public static int OutputWidth(int sourceWidth)
    {
        return Math.Max(1, sourceWidth / 2);
    }

    public static int OutputHeight(int sourceHeight)
    {
        return Math.Max(1, sourceHeight / 2);
    }

    public static byte[] Build(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        long frameNumber,
        DateTime frameTime,
        Ch297Match match)
    {
        int width = OutputWidth(sourceWidth);
        int height = OutputHeight(sourceHeight);
        byte[] output = new byte[checked(width * height)];

        for (int y = 0; y < height; y++)
        {
            int sourceY = Math.Min(sourceHeight - 1, y * 2);
            int nextSourceY = Math.Min(sourceHeight - 1, sourceY + 1);

            for (int x = 0; x < width; x++)
            {
                int sourceX = Math.Min(sourceWidth - 1, x * 2);
                int nextSourceX = Math.Min(sourceWidth - 1, sourceX + 1);

                int value =
                    source[sourceY * sourceWidth + sourceX] +
                    source[sourceY * sourceWidth + nextSourceX] +
                    source[nextSourceY * sourceWidth + sourceX] +
                    source[nextSourceY * sourceWidth + nextSourceX];

                output[y * width + x] = (byte)(value / 4);
            }
        }

        int panelHeight = Math.Min(height, 25);
        for (int y = 0; y < panelHeight; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                output[index] = (byte)(output[index] / 4);
            }
        }

        string firstLine = string.Format(
            CultureInfo.InvariantCulture,
            "FRAME {0:000000}  TIME {1}",
            frameNumber,
            frameTime.ToString("HH:mm:ss.fff"));

        string secondLine;
        Ch297Sample sample = match == null ? null : match.Sample;

        if (match != null && match.Bound && sample != null)
        {
            secondLine = string.Format(
                CultureInfo.InvariantCulture,
                "PMT {0:0}  TH {1:0}  {2}",
                sample.Count,
                sample.Threshold,
                sample.AboveThreshold ? "HIGH" : "LOW");
        }
        else
        {
            secondLine = "PMT NO DATA";
        }

        DrawText(output, width, height, 5, 4, firstLine, 238);
        DrawText(output, width, height, 5, 14, secondLine, 238);
        return output;
    }

    private static void DrawText(
        byte[] image,
        int width,
        int height,
        int startX,
        int startY,
        string text,
        byte color)
    {
        int x = startX;

        for (int i = 0; i < text.Length; i++)
        {
            string[] glyph = GetGlyph(text[i]);

            for (int row = 0; row < 7; row++)
            {
                int y = startY + row;
                if (y < 0 || y >= height)
                    continue;

                for (int column = 0; column < 5; column++)
                {
                    int pixelX = x + column;
                    if (pixelX < 0 || pixelX >= width)
                        continue;

                    if (glyph[row][column] == '1')
                        image[y * width + pixelX] = color;
                }
            }

            x += 6;
            if (x + 5 >= width)
                break;
        }
    }

    private static string[] GetGlyph(char value)
    {
        switch (char.ToUpperInvariant(value))
        {
            case '0': return G("01110", "10001", "10011", "10101", "11001", "10001", "01110");
            case '1': return G("00100", "01100", "00100", "00100", "00100", "00100", "01110");
            case '2': return G("01110", "10001", "00001", "00010", "00100", "01000", "11111");
            case '3': return G("11110", "00001", "00001", "01110", "00001", "00001", "11110");
            case '4': return G("00010", "00110", "01010", "10010", "11111", "00010", "00010");
            case '5': return G("11111", "10000", "10000", "11110", "00001", "00001", "11110");
            case '6': return G("01110", "10000", "10000", "11110", "10001", "10001", "01110");
            case '7': return G("11111", "00001", "00010", "00100", "01000", "01000", "01000");
            case '8': return G("01110", "10001", "10001", "01110", "10001", "10001", "01110");
            case '9': return G("01110", "10001", "10001", "01111", "00001", "00001", "01110");
            case 'A': return G("01110", "10001", "10001", "11111", "10001", "10001", "10001");
            case 'C': return G("01111", "10000", "10000", "10000", "10000", "10000", "01111");
            case 'D': return G("11110", "10001", "10001", "10001", "10001", "10001", "11110");
            case 'E': return G("11111", "10000", "10000", "11110", "10000", "10000", "11111");
            case 'F': return G("11111", "10000", "10000", "11110", "10000", "10000", "10000");
            case 'G': return G("01111", "10000", "10000", "10111", "10001", "10001", "01111");
            case 'H': return G("10001", "10001", "10001", "11111", "10001", "10001", "10001");
            case 'I': return G("01110", "00100", "00100", "00100", "00100", "00100", "01110");
            case 'L': return G("10000", "10000", "10000", "10000", "10000", "10000", "11111");
            case 'M': return G("10001", "11011", "10101", "10101", "10001", "10001", "10001");
            case 'N': return G("10001", "11001", "10101", "10011", "10001", "10001", "10001");
            case 'O': return G("01110", "10001", "10001", "10001", "10001", "10001", "01110");
            case 'P': return G("11110", "10001", "10001", "11110", "10000", "10000", "10000");
            case 'R': return G("11110", "10001", "10001", "11110", "10100", "10010", "10001");
            case 'S': return G("01111", "10000", "10000", "01110", "00001", "00001", "11110");
            case 'T': return G("11111", "00100", "00100", "00100", "00100", "00100", "00100");
            case 'U': return G("10001", "10001", "10001", "10001", "10001", "10001", "01110");
            case 'W': return G("10001", "10001", "10001", "10101", "10101", "10101", "01010");
            case ':': return G("00000", "00100", "00100", "00000", "00100", "00100", "00000");
            case '.': return G("00000", "00000", "00000", "00000", "00000", "00110", "00110");
            case '-': return G("00000", "00000", "00000", "11111", "00000", "00000", "00000");
            default: return G("00000", "00000", "00000", "00000", "00000", "00000", "00000");
        }
    }

    private static string[] G(
        string a,
        string b,
        string c,
        string d,
        string e,
        string f,
        string g)
    {
        return new string[] { a, b, c, d, e, f, g };
    }
}

internal sealed class LosslessVideoRecorder : IDisposable
{
    private const long SegmentLimitBytes =
        1500L * 1024L * 1024L;

    private readonly object _queueLock = new object();
    private readonly Queue<RecordedFrame> _queue =
        new Queue<RecordedFrame>();
    private readonly AutoResetEvent _wake =
        new AutoResetEvent(false);

    private volatile bool _acceptingFrames;
    private volatile bool _stopRequested;
    private volatile bool _running;
    private Thread _writerThread;

    private string _sessionDirectory;
    private string _sessionName;
    private int _width;
    private int _height;
    private int _fps;
    private int _partNumber;
    private AviGray8Writer _avi;
    private AviGray8Writer _reviewAvi;
    private StreamWriter _csv;
    private Stopwatch _sessionClock;
    private Ch297SyncStore _syncStore;
    private bool _writeReviewCopy;
    private string _experimentId = "";

    private long _framesAccepted;
    private long _framesWritten;
    private long _framesDropped;
    private long _framesBoundToCh297;
    private long _framesUnboundToCh297;
    private long _bytesWritten;
    private string _lastError = "";
    private string _currentAviPath = "";
    private string _currentReviewPath = "";
    private int _currentPartFrameNumber;

    public bool IsRunning
    {
        get { return _running; }
    }

    public long FramesAccepted
    {
        get { return Interlocked.Read(ref _framesAccepted); }
    }

    public long FramesWritten
    {
        get { return Interlocked.Read(ref _framesWritten); }
    }

    public long FramesDropped
    {
        get { return Interlocked.Read(ref _framesDropped); }
    }

    public long FramesBoundToCh297
    {
        get { return Interlocked.Read(ref _framesBoundToCh297); }
    }

    public long FramesUnboundToCh297
    {
        get { return Interlocked.Read(ref _framesUnboundToCh297); }
    }

    public long BytesWritten
    {
        get { return Interlocked.Read(ref _bytesWritten); }
    }

    public string LastError
    {
        get { return _lastError; }
    }

    public string SessionDirectory
    {
        get { return _sessionDirectory; }
    }

    public string CurrentAviPath
    {
        get { return _currentAviPath; }
    }

    public int QueueCount
    {
        get
        {
            lock (_queueLock)
            {
                return _queue.Count;
            }
        }
    }

    public void Start(
        string rootDirectory,
        int width,
        int height,
        int fps,
        float exposureMs,
        Ch297SyncStore syncStore,
        string experimentId,
        bool writeReviewCopy)
    {
        if (_running)
            throw new InvalidOperationException("Recorder is already running.");

        _width = width;
        _height = height;
        _fps = fps;
        _syncStore = syncStore;
        _experimentId = experimentId == null ? "" : experimentId.Trim();
        _writeReviewCopy = writeReviewCopy;
        _partNumber = 0;
        _currentPartFrameNumber = 0;
        _framesAccepted = 0;
        _framesWritten = 0;
        _framesDropped = 0;
        _framesBoundToCh297 = 0;
        _framesUnboundToCh297 = 0;
        _bytesWritten = 0;
        _lastError = "";

        _sessionName =
            "NEFU_IDEC_" +
            DateTime.Now.ToString("yyyyMMdd_HHmmss") +
            NormalizeFileSuffix(_experimentId);

        _sessionDirectory = Path.Combine(
            rootDirectory,
            _sessionName);

        Directory.CreateDirectory(_sessionDirectory);

        _csv = new StreamWriter(
            Path.Combine(
                _sessionDirectory,
                "frame_index.csv"),
            false,
            new UTF8Encoding(true));

        _csv.AutoFlush = true;
        _csv.WriteLine(
            "record_frame,part,part_frame,source_frame," +
            "computer_time,elapsed_s,capture_tick," +
            "exposure_ms,avi_file," +
            "ch297_bound,ch297_session_id,ch297_sample_index," +
            "ch297_count,ch297_threshold,ch297_hysteresis," +
            "ch297_above_threshold,ch297_status,ch297_sample_time," +
            "ch297_qpc_ticks,frame_minus_ch297_ms,ch297_transport_ms," +
            "review_avi_file");

        File.WriteAllText(
            Path.Combine(
                _sessionDirectory,
                "recording_metadata.txt"),
            BuildInitialMetadata(exposureMs),
            new UTF8Encoding(true));

        OpenNextPart();

        _sessionClock = Stopwatch.StartNew();
        _acceptingFrames = true;
        _stopRequested = false;
        _running = true;

        _writerThread = new Thread(
            new ThreadStart(WriterLoop));

        _writerThread.IsBackground = true;
        _writerThread.Name =
            "MUS40M-G Lossless AVI Writer";

        _writerThread.Start();
    }

    public bool TryEnqueue(RecordedFrame frame)
    {
        if (!_acceptingFrames || !_running)
            return false;

        lock (_queueLock)
        {
            if (_queue.Count >= 256)
            {
                Interlocked.Increment(
                    ref _framesDropped);
                return false;
            }

            _queue.Enqueue(frame);
            Interlocked.Increment(
                ref _framesAccepted);
        }

        _wake.Set();
        return true;
    }

    public void StopAndWait()
    {
        _acceptingFrames = false;
        _stopRequested = true;
        _wake.Set();

        if (
            _writerThread != null &&
            _writerThread.IsAlive)
        {
            _writerThread.Join(15000);
        }

        _writerThread = null;
    }

    private void WriterLoop()
    {
        try
        {
            while (true)
            {
                RecordedFrame frame = null;

                lock (_queueLock)
                {
                    if (_queue.Count > 0)
                    {
                        frame = _queue.Dequeue();
                    }
                    else if (_stopRequested)
                    {
                        break;
                    }
                }

                if (frame == null)
                {
                    _wake.WaitOne(250);
                    continue;
                }

                long estimatedNextLength =
                    _avi.FileLength +
                    _avi.FrameSize +
                    64;

                if (
                    estimatedNextLength >=
                    SegmentLimitBytes)
                {
                    CloseCurrentPart();
                    OpenNextPart();
                }

                Ch297Match syncMatch =
                    _syncStore == null
                    ? new Ch297Match()
                    : _syncStore.MatchClosest(
                        frame.CaptureTick,
                        60);

                _avi.WriteFrame(frame.Data);
                _currentPartFrameNumber++;

                long globalFrame =
                    Interlocked.Increment(
                        ref _framesWritten);

                if (_reviewAvi != null)
                {
                    byte[] reviewFrame =
                        ReviewFrameRenderer.Build(
                            frame.Data,
                            _width,
                            _height,
                            globalFrame,
                            frame.ComputerTime,
                            syncMatch);

                    _reviewAvi.WriteFrame(reviewFrame);
                }

                Interlocked.Exchange(
                    ref _bytesWritten,
                    CalculateSessionBytes());

                Ch297Sample sample =
                    syncMatch.Sample;

                if (syncMatch.Bound)
                {
                    Interlocked.Increment(
                        ref _framesBoundToCh297);
                }
                else
                {
                    Interlocked.Increment(
                        ref _framesUnboundToCh297);
                }

                _csv.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5:F6},{6},{7:F6},{8}," +
                    "{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19},{20},{21}",
                    globalFrame,
                    _partNumber,
                    _currentPartFrameNumber,
                    frame.SourceFrameNumber,
                    frame.ComputerTime.ToString(
                        "yyyy-MM-dd HH:mm:ss.fff"),
                    _sessionClock.Elapsed.TotalSeconds,
                    frame.CaptureTick,
                    frame.ExposureMs,
                    Path.GetFileName(_currentAviPath),
                    syncMatch.Bound ? 1 : 0,
                    sample == null
                        ? ""
                        : EscapeCsv(sample.SessionId),
                    sample == null
                        ? ""
                        : sample.SampleIndex.ToString(
                            CultureInfo.InvariantCulture),
                    sample == null
                        ? ""
                        : sample.Count.ToString(
                            "F6",
                            CultureInfo.InvariantCulture),
                    sample == null
                        ? ""
                        : sample.Threshold.ToString(
                            "F6",
                            CultureInfo.InvariantCulture),
                    sample == null
                        ? ""
                        : sample.Hysteresis.ToString(
                            "F6",
                            CultureInfo.InvariantCulture),
                    sample == null
                        ? ""
                        : (
                            sample.AboveThreshold
                            ? "1"
                            : "0"
                        ),
                    sample == null
                        ? ""
                        : EscapeCsv(sample.Status),
                    sample == null
                        ? ""
                        : EscapeCsv(sample.ComputerTime),
                    sample == null
                        ? ""
                        : sample.QpcTicks.ToString(
                            CultureInfo.InvariantCulture),
                    syncMatch.Bound
                        ? syncMatch.FrameMinusSampleMilliseconds.ToString(
                            "F6",
                            CultureInfo.InvariantCulture)
                        : "",
                    sample == null
                        ? ""
                        : sample.TransportMilliseconds.ToString(
                            "F6",
                            CultureInfo.InvariantCulture),
                    _reviewAvi == null
                        ? ""
                        : Path.GetFileName(_currentReviewPath)));
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
        }
        finally
        {
            try
            {
                CloseCurrentPart();
            }
            catch (Exception closeEx)
            {
                if (_lastError.Length == 0)
                    _lastError = closeEx.ToString();
            }

            try
            {
                if (_csv != null)
                {
                    _csv.Flush();
                    _csv.Close();
                    _csv = null;
                }
            }
            catch
            {
            }

            try
            {
                File.AppendAllText(
                    Path.Combine(
                        _sessionDirectory,
                        "recording_metadata.txt"),
                    BuildFinalMetadata(),
                    new UTF8Encoding(true));
            }
            catch
            {
            }

            _running = false;
        }
    }

    private void OpenNextPart()
    {
        _partNumber++;
        _currentPartFrameNumber = 0;

        _currentAviPath = Path.Combine(
            _sessionDirectory,
            _sessionName +
            "_part" +
            _partNumber.ToString("000") +
            ".avi");

        _avi = new AviGray8Writer(
            _currentAviPath,
            _width,
            _height,
            _fps);

        if (_writeReviewCopy)
        {
            _currentReviewPath = Path.Combine(
                _sessionDirectory,
                _sessionName +
                "_review_part" +
                _partNumber.ToString("000") +
                ".avi");

            _reviewAvi = new AviGray8Writer(
                _currentReviewPath,
                ReviewFrameRenderer.OutputWidth(_width),
                ReviewFrameRenderer.OutputHeight(_height),
                _fps);
        }
    }

    private void CloseCurrentPart()
    {
        if (_avi != null)
        {
            _avi.Close();
            _avi = null;
        }

        if (_reviewAvi != null)
        {
            _reviewAvi.Close();
            _reviewAvi = null;
        }
    }

    private long CalculateSessionBytes()
    {
        long total = 0;

        try
        {
            DirectoryInfo directory =
                new DirectoryInfo(
                    _sessionDirectory);

            FileInfo[] files =
                directory.GetFiles("*.avi");

            for (int i = 0; i < files.Length; i++)
            {
                total += files[i].Length;
            }
        }
        catch
        {
        }

        return total;
    }

    private string BuildInitialMetadata(
        float exposureMs)
    {
        StringBuilder sb =
            new StringBuilder();

        sb.AppendLine(
            "NEFU-China iDEC MUS40M-G recording session");

        sb.AppendLine(
            "ExperimentId=" +
            _experimentId.Replace("\r", " ").Replace("\n", " "));

        sb.AppendLine(
            "StartTime=" +
            DateTime.Now.ToString(
                "yyyy-MM-dd HH:mm:ss.fff"));

        sb.AppendLine(
            "Width=" +
            _width.ToString(
                CultureInfo.InvariantCulture));

        sb.AppendLine(
            "Height=" +
            _height.ToString(
                CultureInfo.InvariantCulture));

        sb.AppendLine("BitCount=8");

        sb.AppendLine(
            "RecordFPS=" +
            _fps.ToString(
                CultureInfo.InvariantCulture));

        sb.AppendLine(
            "ExposureMsAtStart=" +
            exposureMs.ToString(
                "F6",
                CultureInfo.InvariantCulture));

        sb.AppendLine(
            "Format=Uncompressed 8-bit grayscale AVI");

        sb.AppendLine(
            "RawMaster=Always preserved without annotations");

        sb.AppendLine(
            "ReviewCopy=" +
            (_writeReviewCopy
                ? "Half-resolution AVI with frame time and PMT overlay"
                : "Disabled"));

        sb.AppendLine(
            "SegmentLimitBytes=" +
            SegmentLimitBytes.ToString(
                CultureInfo.InvariantCulture));

        sb.AppendLine(
            "FrameIndex=frame_index.csv");

        sb.AppendLine(
            "CH297SyncTransport=V6 shared QPC sample store");

        sb.AppendLine(
            "CH297Binding=Nearest valid sample by shared QPC timestamp");

        sb.AppendLine(
            "ReviewOverlayNote=PMT values are nearest samples for visual review; frame_index.csv retains the timing difference");

        sb.AppendLine(
            "CH297InvalidSamples=Logged but excluded from frame binding");

        return sb.ToString();
    }

    private static string NormalizeFileSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        StringBuilder safe = new StringBuilder();
        char[] invalid = Path.GetInvalidFileNameChars();

        for (int i = 0; i < value.Length && safe.Length < 36; i++)
        {
            char current = value[i];
            bool isInvalid = false;

            for (int j = 0; j < invalid.Length; j++)
            {
                if (current == invalid[j])
                {
                    isInvalid = true;
                    break;
                }
            }

            if (!isInvalid)
                safe.Append(char.IsWhiteSpace(current) ? '_' : current);
        }

        return safe.Length == 0 ? "" : "_" + safe.ToString();
    }

    private string BuildFinalMetadata()
    {
        StringBuilder sb =
            new StringBuilder();

        sb.AppendLine();
        sb.AppendLine(
            "StopTime=" +
            DateTime.Now.ToString(
                "yyyy-MM-dd HH:mm:ss.fff"));

        sb.AppendLine(
            "FramesAccepted=" +
            FramesAccepted.ToString(
                CultureInfo.InvariantCulture));

        sb.AppendLine(
            "FramesWritten=" +
            FramesWritten.ToString(
                CultureInfo.InvariantCulture));

        sb.AppendLine(
            "FramesDropped=" +
            FramesDropped.ToString(
                CultureInfo.InvariantCulture));

        sb.AppendLine(
            "FramesBoundToCH297=" +
            FramesBoundToCh297.ToString(
                CultureInfo.InvariantCulture));

        sb.AppendLine(
            "FramesUnboundToCH297=" +
            FramesUnboundToCh297.ToString(
                CultureInfo.InvariantCulture));

        sb.AppendLine(
            "LastError=" +
            _lastError.Replace(
                "\r",
                " ").Replace(
                "\n",
                " "));

        return sb.ToString();
    }

    private static string EscapeCsv(
        string value)
    {
        if (value == null)
            return "";

        if (
            value.IndexOf(',') < 0 &&
            value.IndexOf('"') < 0 &&
            value.IndexOf('\r') < 0 &&
            value.IndexOf('\n') < 0)
        {
            return value;
        }

        return
            "\"" +
            value.Replace(
                "\"",
                "\"\"") +
            "\"";
    }

    public void Dispose()
    {
        StopAndWait();
        _wake.Dispose();
    }
}

internal sealed class PreviewForm : Form
{
    public Func<PynqV6Status> PynqStatusProvider { get; set; }

    private static readonly string OscamDirectory =
        ResolveOscamDirectory();

    private const int CameraIndex = 0;

    private readonly object _apiLock =
        new object();

    private readonly object _frameLock =
        new object();

    private PictureBox _picture;
    private Panel _integratedHeader;
    private Panel _integratedToolbar;
    private Panel _integratedRecordPanel;
    private Panel _integratedAnalysisPanel;
    private Label _statusLabel;
    private Label _fpsLabel;
    private Label _brightnessLabel;
    private Label _deviceLabel;
    private Label _recordStatusLabel;
    private Label _syncStatusLabel;

    private NumericUpDown _exposureBox;
    private NumericUpDown _recordFpsBox;
    private TextBox _experimentBox;

    private CheckBox _stretchBox;
    private CheckBox _previewOverlayBox;
    private CheckBox _crosshairBox;
    private CheckBox _reviewCopyBox;

    private Panel _histogramPanel;
    private int[] _latestHistogram;

    private Button _connectButton;
    private Button _startButton;
    private Button _stopButton;
    private Button _applyExposureButton;
    private Button _snapshotButton;
    private Button _disconnectButton;
    private Button _startRecordButton;
    private Button _stopRecordButton;
    private Button _openRecordingsButton;

    private System.Windows.Forms.Timer _statusTimer;

    private bool _initialized;
    private bool _connected;

    private volatile bool _previewRequested;
    private volatile bool _autoStretch = true;
    private volatile bool _recordingRequested;

    private Thread _captureThread;
    private LosslessVideoRecorder _recorder;
    private readonly Ch297SyncStore _syncStore;
    private readonly bool _ownsSyncStore;
    private readonly bool _ownsSyncServer;
    private Ch297SyncServer _syncServer;
    private const int SyncPort = 5101;

    private IntPtr _buffer =
        IntPtr.Zero;

    private int _bufferSize;
    private int _width;
    private int _height;
    private int _bitCount;

    private byte[] _latestRaw;
    private long _latestFrameNumber;
    private int _latestCaptureReturn;
    private DateTime _latestFrameTime;

    private double _captureFps;
    private double _displayFps;
    private int _displayPending;

    private long _nextRecordTick;
    private long _recordIntervalTicks;
    private float _actualExposureMs;

    public PreviewForm()
        : this(null, true)
    {
    }

    internal PreviewForm(
        Ch297SyncStore sharedSyncStore,
        bool startOwnSyncServer)
    {
        _syncStore = sharedSyncStore ?? new Ch297SyncStore();
        _ownsSyncStore = sharedSyncStore == null;
        _ownsSyncServer = startOwnSyncServer;

        Text =
            "NEFU-China iDEC | Scientific Camera";

        Width = 1480;
        Height = 920;
        MinimumSize =
            new Size(1180, 760);

        StartPosition =
            FormStartPosition.CenterScreen;

        BackColor =
            Color.FromArgb(242, 246, 251);

        Font =
            new Font(
                "Segoe UI",
                9F);

        BuildUi();

        _statusTimer =
            new System.Windows.Forms.Timer();

        _statusTimer.Interval = 500;
        _statusTimer.Tick +=
            delegate
            {
                UpdateRecorderStatus();
                UpdateSyncStatus();
            };

        _statusTimer.Start();

        if (_ownsSyncServer)
            StartSyncServer();

        FormClosing += OnFormClosing;
    }

    internal bool IntegratedCameraConnected
    {
        get { return _connected; }
    }

    internal bool IntegratedRecording
    {
        get { return _recordingRequested; }
    }

    internal bool IntegratedPreviewActive
    {
        get { return _previewRequested; }
    }

    internal double IntegratedCaptureFps
    {
        get { return _captureFps; }
    }

    internal string IntegratedRecordingStatus
    {
        get
        {
            return _recordStatusLabel == null
                ? "Recording module not initialized"
                : _recordStatusLabel.Text;
        }
    }

    internal Ch297SyncStore IntegratedSyncStore
    {
        get { return _syncStore; }
    }

    internal bool IntegratedCh297Connected
    {
        get { return _syncStore.IsConnected; }
    }

    internal Ch297Sample IntegratedLatestCh297Sample
    {
        get { return _syncStore.Latest; }
    }

    internal string IntegratedCameraStatus
    {
        get
        {
            return _statusLabel == null
                ? "Camera module not initialized"
                : _statusLabel.Text;
        }
    }

    internal void IntegratedConnectAndPreview()
    {
        ConnectCamera();

        if (_connected && !_previewRequested)
            StartPreview();
    }

    internal void IntegratedStartRecording(
        string experimentId,
        int recordFps)
    {
        if (_experimentBox != null)
            _experimentBox.Text = experimentId ?? "";

        if (_recordFpsBox != null)
        {
            decimal value = recordFps;

            if (value < _recordFpsBox.Minimum)
                value = _recordFpsBox.Minimum;

            if (value > _recordFpsBox.Maximum)
                value = _recordFpsBox.Maximum;

            _recordFpsBox.Value = value;
        }

        StartRecording();
    }

    internal void IntegratedStopRecording()
    {
        StopRecording();
    }

    internal void IntegratedDisconnectCamera()
    {
        DisconnectCamera();
    }

    internal void IntegratedSaveSnapshot()
    {
        SaveSnapshot();
    }

    internal void IntegratedSetExposure(decimal exposureMilliseconds)
    {
        if (_exposureBox == null)
            return;

        decimal value = Math.Max(
            _exposureBox.Minimum,
            Math.Min(_exposureBox.Maximum, exposureMilliseconds));

        _exposureBox.Value = value;
        ApplyExposure();
    }

    internal void IntegratedSetCompactMode(bool compact)
    {
        if (_integratedHeader != null)
            _integratedHeader.Visible = !compact;

        if (_integratedToolbar != null)
            _integratedToolbar.Visible = !compact;

        if (_integratedRecordPanel != null)
            _integratedRecordPanel.Visible = !compact;

        if (_integratedAnalysisPanel != null)
            _integratedAnalysisPanel.Visible = !compact;

        MinimumSize = compact
            ? Size.Empty
            : new Size(1180, 760);
    }

    private static string ResolveOscamDirectory()
    {
        string configured =
            Environment.GetEnvironmentVariable(
                "NEFU_OSCAM_DIR");

        if (
            !string.IsNullOrEmpty(configured) &&
            File.Exists(
                Path.Combine(
                    configured,
                    "OEApi64.dll")))
        {
            return configured;
        }

        string[] candidates = new string[]
        {
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                "Oscam"),
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86),
                "Oscam"),
            AppDomain.CurrentDomain.BaseDirectory
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (
                !string.IsNullOrEmpty(candidates[i]) &&
                File.Exists(
                    Path.Combine(
                        candidates[i],
                        "OEApi64.dll")))
            {
                return candidates[i];
            }
        }

        return @"C:\Program Files\Oscam";
    }

    private void BuildUi()
    {
        Panel header = new Panel();
        _integratedHeader = header;
        header.Dock = DockStyle.Top;
        header.Height = 72;
        header.BackColor =
            Color.FromArgb(16, 42, 67);
        Controls.Add(header);

        Label title = new Label();
        title.Text =
            "NEFU-China iDEC | Scientific Camera";
        title.ForeColor = Color.White;
        title.Font =
            new Font(
                "Segoe UI",
                18F,
                FontStyle.Bold);
        title.AutoSize = true;
        title.Location =
            new Point(20, 12);
        header.Controls.Add(title);

        Label subtitle = new Label();
        subtitle.Text =
            "MUS40M-G | Microscopy, raw recording and photon-count annotation";
        subtitle.ForeColor =
            Color.FromArgb(185, 209, 235);
        subtitle.AutoSize = true;
        subtitle.Location =
            new Point(24, 46);
        header.Controls.Add(subtitle);

        Panel toolbar = new Panel();
        _integratedToolbar = toolbar;
        toolbar.Dock = DockStyle.Top;
        toolbar.Height = 126;
        toolbar.BackColor = Color.White;
        Controls.Add(toolbar);

        _connectButton =
            MakeButton(
                "Connect / Preview",
                14,
                12,
                110,
                Color.FromArgb(37, 99, 235));

        _connectButton.Click +=
            delegate
            {
                ConnectCamera();

                if (_connected && !_previewRequested)
                    StartPreview();
            };

        toolbar.Controls.Add(
            _connectButton);

        _startButton =
            MakeButton(
                "Resume preview",
                134,
                12,
                96,
                Color.FromArgb(34, 166, 112));

        _startButton.Enabled = false;
        _startButton.Click +=
            delegate
            {
                StartPreview();
            };

        toolbar.Controls.Add(_startButton);

        _stopButton =
            MakeButton(
                "Stop preview",
                240,
                12,
                96,
                Color.FromArgb(226, 74, 74));

        _stopButton.Enabled = false;
        _stopButton.Click +=
            delegate
            {
                StopPreview();
            };

        toolbar.Controls.Add(_stopButton);

        _snapshotButton =
            MakeButton(
                "Save snapshot",
                346,
                12,
                96,
                Color.FromArgb(18, 43, 76));

        _snapshotButton.Enabled = false;
        _snapshotButton.Click +=
            delegate
            {
                SaveSnapshot();
            };

        toolbar.Controls.Add(_snapshotButton);

        _disconnectButton =
            MakeButton(
                "Release camera",
                452,
                12,
                96,
                Color.FromArgb(92, 107, 125));

        _disconnectButton.Enabled = false;
        _disconnectButton.Click +=
            delegate
            {
                DisconnectCamera();
            };

        toolbar.Controls.Add(
            _disconnectButton);

        Label exposureTitle =
            MakeFieldLabel(
                "Exposure / ms",
                574,
                22,
                58);

        toolbar.Controls.Add(
            exposureTitle);

        _exposureBox =
            new NumericUpDown();

        _exposureBox.DecimalPlaces = 3;
        _exposureBox.Minimum = 0.001M;
        _exposureBox.Maximum = 10000M;
        _exposureBox.Increment = 0.100M;
        _exposureBox.Value = 1.000M;
        _exposureBox.Width = 100;
        _exposureBox.Location =
            new Point(636, 17);
        _exposureBox.Enabled = false;

        toolbar.Controls.Add(
            _exposureBox);

        _applyExposureButton =
            MakeButton(
                "Apply exposure",
                746,
                12,
                96,
                Color.FromArgb(242, 153, 74));

        _applyExposureButton.Enabled = false;
        _applyExposureButton.Click +=
            delegate
            {
                ApplyExposure();
            };

        toolbar.Controls.Add(
            _applyExposureButton);

        _stretchBox = new CheckBox();
        _stretchBox.Text =
            "Auto contrast";
        _stretchBox.Checked = true;
        _stretchBox.AutoSize = true;
        _stretchBox.Location =
            new Point(858, 20);
        _stretchBox.CheckedChanged +=
            delegate
            {
                _autoStretch =
                    _stretchBox.Checked;
            };

        toolbar.Controls.Add(
            _stretchBox);

        Label recordFpsTitle =
            MakeFieldLabel(
                "Recording FPS",
                14,
                71,
                58);

        toolbar.Controls.Add(
            recordFpsTitle);

        _recordFpsBox =
            new NumericUpDown();

        _recordFpsBox.Minimum = 1;
        _recordFpsBox.Maximum = 400;
        _recordFpsBox.Value = 30;
        _recordFpsBox.Increment = 10;
        _recordFpsBox.Width = 80;
        _recordFpsBox.Location =
            new Point(76, 66);

        toolbar.Controls.Add(
            _recordFpsBox);

        _startRecordButton =
            MakeButton(
                "Record",
                170,
                61,
                110,
                Color.FromArgb(172, 36, 64));

        _startRecordButton.Enabled = false;
        _startRecordButton.Click +=
            delegate
            {
                StartRecording();
            };

        toolbar.Controls.Add(
            _startRecordButton);

        _stopRecordButton =
            MakeButton(
                "Stop recording",
                290,
                61,
                110,
                Color.FromArgb(104, 45, 134));

        _stopRecordButton.Enabled = false;
        _stopRecordButton.Click +=
            delegate
            {
                StopRecording();
            };

        toolbar.Controls.Add(
            _stopRecordButton);

        _openRecordingsButton =
            MakeButton(
                "Open recordings",
                410,
                61,
                124,
                Color.FromArgb(18, 43, 76));

        _openRecordingsButton.Click +=
            delegate
            {
                OpenRecordingsDirectory();
            };

        toolbar.Controls.Add(
            _openRecordingsButton);

        Label warning = new Label();
        warning.Text =
            "Raw video preserves original pixels. Optional half-resolution review video includes frame, time and PMT annotations.";
        warning.AutoSize = true;
        warning.ForeColor =
            Color.FromArgb(124, 88, 25);
        warning.Font =
            new Font(
                "Segoe UI",
                9F,
                FontStyle.Bold);
        warning.Location =
            new Point(560, 72);

        toolbar.Controls.Add(warning);

        _deviceLabel = new Label();
        _deviceLabel.Text =
            "Device: disconnected";
        _deviceLabel.Location =
            new Point(14, 103);
        _deviceLabel.Size =
            new Size(540, 22);
        _deviceLabel.ForeColor =
            Color.FromArgb(76, 91, 108);

        toolbar.Controls.Add(
            _deviceLabel);

        _fpsLabel = new Label();
        _fpsLabel.Text =
            "Capture FPS: --  Display FPS: --";
        _fpsLabel.Location =
            new Point(565, 103);
        _fpsLabel.Size =
            new Size(260, 22);
        _fpsLabel.ForeColor =
            Color.FromArgb(76, 91, 108);

        toolbar.Controls.Add(_fpsLabel);

        _brightnessLabel = new Label();
        _brightnessLabel.Text =
            "Brightness: --";
        _brightnessLabel.Location =
            new Point(835, 103);
        _brightnessLabel.Size =
            new Size(310, 22);
        _brightnessLabel.ForeColor =
            Color.FromArgb(76, 91, 108);

        toolbar.Controls.Add(
            _brightnessLabel);

        Panel recordPanel = new Panel();
        _integratedRecordPanel = recordPanel;
        recordPanel.Dock = DockStyle.Bottom;
        recordPanel.Height = 92;
        recordPanel.BackColor = Color.White;
        recordPanel.Padding =
            new Padding(14, 8, 14, 8);

        Controls.Add(recordPanel);

        _recordStatusLabel = new Label();
        _recordStatusLabel.Dock = DockStyle.Top;
        _recordStatusLabel.Height = 25;
        _recordStatusLabel.Text =
            "Recording: idle";
        _recordStatusLabel.ForeColor =
            Color.FromArgb(76, 91, 108);
        _recordStatusLabel.Font =
            new Font(
                "Segoe UI",
                9.5F,
                FontStyle.Bold);

        recordPanel.Controls.Add(
            _recordStatusLabel);

        _syncStatusLabel = new Label();
        _syncStatusLabel.Dock = DockStyle.Top;
        _syncStatusLabel.Height = 25;
        _syncStatusLabel.Text =
            "Optional PMT: preparing local sync port";
        _syncStatusLabel.ForeColor =
            Color.FromArgb(115, 130, 145);
        _syncStatusLabel.Font =
            new Font(
                "Segoe UI",
                9F,
                FontStyle.Bold);

        recordPanel.Controls.Add(
            _syncStatusLabel);

        _statusLabel = new Label();
        _statusLabel.Dock = DockStyle.Bottom;
        _statusLabel.Height = 24;
        _statusLabel.Text =
            "Ready. Close OsCam, then select Connect / Preview. PMT synchronization is optional.";
        _statusLabel.ForeColor =
            Color.FromArgb(76, 91, 108);

        recordPanel.Controls.Add(
            _statusLabel);

        Panel imageCard = new Panel();
        imageCard.Dock = DockStyle.Fill;
        imageCard.Padding =
            new Padding(12);
        imageCard.BackColor =
            Color.FromArgb(242, 246, 251);

        Controls.Add(imageCard);

        Panel analysisPanel = new Panel();
        _integratedAnalysisPanel = analysisPanel;
        analysisPanel.Dock = DockStyle.Right;
        analysisPanel.Width = 292;
        analysisPanel.BackColor = Color.White;
        analysisPanel.Padding = new Padding(16);
        imageCard.Controls.Add(analysisPanel);

        Label experimentTitle = new Label();
        experimentTitle.Text = "Experiment ID";
        experimentTitle.Font = new Font(
            "Segoe UI",
            10F,
            FontStyle.Bold);
        experimentTitle.ForeColor = Color.FromArgb(16, 42, 67);
        experimentTitle.Location = new Point(16, 18);
        experimentTitle.AutoSize = true;
        analysisPanel.Controls.Add(experimentTitle);

        _experimentBox = new TextBox();
        _experimentBox.Location = new Point(16, 46);
        _experimentBox.Width = 258;
        _experimentBox.Text = "NEFU-China_iDEC";
        analysisPanel.Controls.Add(_experimentBox);

        Label experimentHint = new Label();
        experimentHint.Text = "Chip, sample or experiment batch";
        experimentHint.ForeColor = Color.FromArgb(115, 130, 145);
        experimentHint.Location = new Point(16, 74);
        experimentHint.AutoSize = true;
        analysisPanel.Controls.Add(experimentHint);

        Label displayTitle = new Label();
        displayTitle.Text = "Viewing aids";
        displayTitle.Font = new Font(
            "Segoe UI",
            10F,
            FontStyle.Bold);
        displayTitle.ForeColor = Color.FromArgb(16, 42, 67);
        displayTitle.Location = new Point(16, 112);
        displayTitle.AutoSize = true;
        analysisPanel.Controls.Add(displayTitle);

        _previewOverlayBox = new CheckBox();
        _previewOverlayBox.Text = "Frame, time and PMT overlay";
        _previewOverlayBox.Checked = true;
        _previewOverlayBox.AutoSize = true;
        _previewOverlayBox.Location = new Point(16, 141);
        _previewOverlayBox.CheckedChanged += delegate
        {
            if (_picture != null)
                _picture.Invalidate();
        };
        analysisPanel.Controls.Add(_previewOverlayBox);

        _crosshairBox = new CheckBox();
        _crosshairBox.Text = "Center crosshair";
        _crosshairBox.Checked = false;
        _crosshairBox.AutoSize = true;
        _crosshairBox.Location = new Point(16, 168);
        _crosshairBox.CheckedChanged += delegate
        {
            if (_picture != null)
                _picture.Invalidate();
        };
        analysisPanel.Controls.Add(_crosshairBox);

        Label histogramTitle = new Label();
        histogramTitle.Text = "Intensity histogram";
        histogramTitle.Font = new Font(
            "Segoe UI",
            10F,
            FontStyle.Bold);
        histogramTitle.ForeColor = Color.FromArgb(16, 42, 67);
        histogramTitle.Location = new Point(16, 210);
        histogramTitle.AutoSize = true;
        analysisPanel.Controls.Add(histogramTitle);

        _histogramPanel = new Panel();
        _histogramPanel.Location = new Point(16, 240);
        _histogramPanel.Size = new Size(258, 92);
        _histogramPanel.BackColor = Color.FromArgb(246, 249, 252);
        _histogramPanel.Paint += DrawHistogram;
        analysisPanel.Controls.Add(_histogramPanel);

        Label recordTitle = new Label();
        recordTitle.Text = "Recording output";
        recordTitle.Font = new Font(
            "Segoe UI",
            10F,
            FontStyle.Bold);
        recordTitle.ForeColor = Color.FromArgb(16, 42, 67);
        recordTitle.Location = new Point(16, 368);
        recordTitle.AutoSize = true;
        analysisPanel.Controls.Add(recordTitle);

        _reviewCopyBox = new CheckBox();
        _reviewCopyBox.Text = "Save a data-overlay review video";
        _reviewCopyBox.Checked = true;
        _reviewCopyBox.AutoSize = true;
        _reviewCopyBox.Location = new Point(16, 397);
        analysisPanel.Controls.Add(_reviewCopyBox);

        Label reviewHint = new Label();
        reviewHint.Text =
            "Original AVI is preserved.\r\nReview video uses half resolution.";
        reviewHint.ForeColor = Color.FromArgb(115, 130, 145);
        reviewHint.Location = new Point(16, 424);
        reviewHint.Size = new Size(258, 42);
        analysisPanel.Controls.Add(reviewHint);

        Label teamInfo = new Label();
        teamInfo.Text =
            "Northeast Forestry University\r\nNEFU-China · iDEC Experimental Group";
        teamInfo.ForeColor = Color.FromArgb(76, 91, 108);
        teamInfo.Location = new Point(16, 500);
        teamInfo.Size = new Size(258, 44);
        analysisPanel.Controls.Add(teamInfo);

        _picture = new PictureBox();
        _picture.Dock = DockStyle.Fill;
        _picture.BackColor = Color.Black;
        _picture.SizeMode =
            PictureBoxSizeMode.Zoom;
        _picture.BorderStyle =
            BorderStyle.None;
        _picture.Paint += DrawPictureOverlay;

        imageCard.Controls.Add(_picture);
        analysisPanel.BringToFront();
    }

    private static Label MakeFieldLabel(
        string text,
        int x,
        int y,
        int width)
    {
        Label label = new Label();
        label.Text = text;
        label.Location =
            new Point(x, y);
        label.Width = width;
        label.ForeColor =
            Color.FromArgb(76, 91, 108);
        return label;
    }

    private static Button MakeButton(
        string text,
        int x,
        int y,
        int width,
        Color color)
    {
        Button button = new Button();
        button.Text = text;
        button.Location =
            new Point(x, y);
        button.Width = width;
        button.Height = 34;
        button.FlatStyle =
            FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.Font =
            new Font(
                "Segoe UI",
                9F,
                FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        return button;
    }

    private void DrawHistogram(
        object sender,
        PaintEventArgs e)
    {
        Rectangle bounds = _histogramPanel.ClientRectangle;
        e.Graphics.Clear(Color.FromArgb(246, 249, 252));

        using (Pen gridPen = new Pen(Color.FromArgb(222, 230, 238)))
        {
            for (int i = 1; i < 4; i++)
            {
                int y = bounds.Height * i / 4;
                e.Graphics.DrawLine(gridPen, 0, y, bounds.Width, y);
            }
        }

        int[] histogram = _latestHistogram;
        if (histogram == null || histogram.Length != 256)
            return;

        int max = 1;
        for (int i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] > max)
                max = histogram[i];
        }

        PointF[] points = new PointF[256];
        for (int i = 0; i < 256; i++)
        {
            float x = i * (bounds.Width - 1F) / 255F;
            double normalized = Math.Log(1.0 + histogram[i]) /
                Math.Log(1.0 + max);
            float y = (float)((bounds.Height - 4) * (1.0 - normalized)) + 2F;
            points[i] = new PointF(x, y);
        }

        using (Pen linePen = new Pen(Color.FromArgb(28, 126, 160), 1.5F))
        {
            e.Graphics.DrawLines(linePen, points);
        }
    }

    private void DrawPictureOverlay(
        object sender,
        PaintEventArgs e)
    {
        if (_picture == null || _picture.Image == null)
            return;

        Rectangle imageRect = GetDisplayedImageRectangle();
        if (imageRect.Width <= 0 || imageRect.Height <= 0)
            return;

        if (_crosshairBox != null && _crosshairBox.Checked)
        {
            int centerX = imageRect.Left + imageRect.Width / 2;
            int centerY = imageRect.Top + imageRect.Height / 2;

            using (Pen shadow = new Pen(Color.FromArgb(150, 0, 0, 0), 3F))
            using (Pen line = new Pen(Color.FromArgb(220, 87, 214, 190), 1F))
            {
                e.Graphics.DrawLine(
                    shadow,
                    centerX - 34,
                    centerY,
                    centerX + 34,
                    centerY);
                e.Graphics.DrawLine(
                    shadow,
                    centerX,
                    centerY - 34,
                    centerX,
                    centerY + 34);
                e.Graphics.DrawLine(
                    line,
                    centerX - 34,
                    centerY,
                    centerX + 34,
                    centerY);
                e.Graphics.DrawLine(
                    line,
                    centerX,
                    centerY - 34,
                    centerX,
                    centerY + 34);
            }
        }

        if (_previewOverlayBox == null || !_previewOverlayBox.Checked)
            return;

        long frameNumber = Interlocked.Read(ref _latestFrameNumber);
        DateTime frameTime = _latestFrameTime;
        Ch297Sample latest = _syncStore.Latest;

        string firstLine = string.Format(
            CultureInfo.InvariantCulture,
            "FRAME {0:000000}   {1}   EXP {2:F3} ms",
            frameNumber,
            frameTime == DateTime.MinValue
                ? "--:--:--.---"
                : frameTime.ToString("HH:mm:ss.fff"),
            _actualExposureMs);

        string secondLine = latest == null
            ? "PMT  --"
            : string.Format(
                CultureInfo.InvariantCulture,
                "PMT {0:0}  TH {1:0}  PC JUDGE {2}",
                latest.Count,
                latest.Threshold,
                latest.AboveThreshold ? "HIGH" : "LOW");

        string thirdLine;
        PynqV6Status pynqStatus = null;
        try
        {
            pynqStatus = PynqStatusProvider == null
                ? null
                : PynqStatusProvider();
        }
        catch
        {
            pynqStatus = null;
        }
        if (pynqStatus == null || !pynqStatus.Connected)
        {
            thirdLine = "PYNQ GATE OFFLINE / LOW";
        }
        else if (pynqStatus.GateHigh)
        {
            thirdLine = string.Format(
                CultureInfo.InvariantCulture,
                "PYNQ GATE HIGH OK  RX->GPIO {0:0.000} ms",
                pynqStatus.GpioMedianMs);
        }
        else
        {
            thirdLine = "PYNQ GATE LOW";
        }

        int panelWidth = Math.Min(430, Math.Max(260, imageRect.Width - 24));
        Rectangle panel = new Rectangle(
            imageRect.Left + 12,
            imageRect.Top + 12,
            panelWidth,
            76);

        using (Brush panelBrush = new SolidBrush(Color.FromArgb(172, 8, 22, 34)))
        using (Brush textBrush = new SolidBrush(Color.FromArgb(245, 247, 250)))
        using (Font overlayFont = new Font("Consolas", 9F, FontStyle.Regular))
        {
            e.Graphics.FillRectangle(panelBrush, panel);
            e.Graphics.DrawString(
                firstLine,
                overlayFont,
                textBrush,
                panel.Left + 10,
                panel.Top + 7);
            e.Graphics.DrawString(
                secondLine,
                overlayFont,
                textBrush,
                panel.Left + 10,
                panel.Top + 29);
            e.Graphics.DrawString(
                thirdLine,
                overlayFont,
                textBrush,
                panel.Left + 10,
                panel.Top + 51);
        }
    }

    private Rectangle GetDisplayedImageRectangle()
    {
        if (_picture.Image == null)
            return Rectangle.Empty;

        int imageWidth = _picture.Image.Width;
        int imageHeight = _picture.Image.Height;
        int clientWidth = _picture.ClientSize.Width;
        int clientHeight = _picture.ClientSize.Height;

        if (
            imageWidth <= 0 ||
            imageHeight <= 0 ||
            clientWidth <= 0 ||
            clientHeight <= 0)
        {
            return Rectangle.Empty;
        }

        float ratio = Math.Min(
            (float)clientWidth / imageWidth,
            (float)clientHeight / imageHeight);

        int width = Math.Max(1, (int)Math.Round(imageWidth * ratio));
        int height = Math.Max(1, (int)Math.Round(imageHeight * ratio));
        int left = (clientWidth - width) / 2;
        int top = (clientHeight - height) / 2;

        return new Rectangle(left, top, width, height);
    }


    private void StartSyncServer()
    {
        if (_syncServer != null)
            return;

        try
        {
            string logDirectory =
                Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "sync_logs");

            _syncServer =
                new Ch297SyncServer(
                    _syncStore,
                    delegate(string text)
                    {
                        Ui(delegate
                        {
                            _syncStatusLabel.Text = text;

                            _syncStatusLabel.ForeColor =
                                text.IndexOf(
                                    "Connected",
                                    StringComparison.Ordinal) >= 0
                                ? Color.FromArgb(
                                    33, 166, 112)
                                : Color.FromArgb(
                                    242, 153, 74);
                        });
                    });

            _syncServer.Start(
                SyncPort,
                logDirectory);
        }
        catch (Exception ex)
        {
            _syncStatusLabel.Text =
                "CH297 sync startup failed: " +
                ex.Message;

            _syncStatusLabel.ForeColor =
                Color.Red;
        }
    }

    private void UpdateSyncStatus()
    {
        if (_syncStore == null)
            return;

        Ch297Sample latest =
            _syncStore.Latest;

        if (latest == null)
        {
            _syncStatusLabel.Text =
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Optional PMT: {0} | Local port {1}",
                    _syncStore.IsConnected
                        ? "Connected; waiting for valid samples"
                        : "Disconnected; camera remains available",
                    SyncPort);

            _syncStatusLabel.ForeColor =
                _syncStore.IsConnected
                ? Color.FromArgb(
                    33, 166, 112)
                : Color.FromArgb(
                    115, 130, 145);

            return;
        }

        _syncStatusLabel.Text =
            string.Format(
                CultureInfo.InvariantCulture,
                "CH297 sync: {0} | count={1:0} | sample={2} | rate={3:F1}/s | gap={4} | transport={5:F3} ms | session={6}",
                _syncStore.IsConnected
                    ? "Connected"
                    : "Disconnected",
                latest.Count,
                latest.SampleIndex,
                _syncStore.ReceiveRate,
                _syncStore.SequenceGaps,
                latest.TransportMilliseconds,
                latest.SessionId);

        _syncStatusLabel.ForeColor =
            _syncStore.IsConnected
            ? Color.FromArgb(
                33, 166, 112)
            : Color.FromArgb(
                242, 153, 74);
    }

    private void ConnectCamera()
    {
        if (_connected)
            return;

        try
        {
            if (IntPtr.Size != 8)
                throw new InvalidOperationException(
                    "Run this application as a 64-bit process.");

            string dllPath =
                Path.Combine(
                    OscamDirectory,
                    "OEApi64.dll");

            if (!File.Exists(dllPath))
                throw new FileNotFoundException(
                    "OEApi64.dll not found.",
                    dllPath);

            if (!Native.SetDllDirectory(
                    OscamDirectory))
            {
                throw new InvalidOperationException(
                    "SetDllDirectory failed, error code: " +
                    Marshal.GetLastWin32Error()
                        .ToString(
                            CultureInfo.InvariantCulture));
            }

            _statusLabel.Text =
                "Initializing camera API...";

            Application.DoEvents();

            int initResult;

            lock (_apiLock)
            {
                initResult =
                    Native.KSJ_Init();
            }

            _initialized = true;

            int count;

            lock (_apiLock)
            {
                count =
                    Native.KSJ_DeviceGetCount();
            }

            if (count <= 0)
                throw new InvalidOperationException(
                    "MUS40M-G not detected. Close OsCam and check the USB connection.");

            ushort deviceType;
            int serial;
            ushort firmware;
            int infoResult;

            lock (_apiLock)
            {
                infoResult =
                    Native.KSJ_DeviceGetInformation(
                        CameraIndex,
                        out deviceType,
                        out serial,
                        out firmware);
            }

            int sizeResult;

            lock (_apiLock)
            {
                sizeResult =
                    Native.KSJ_CaptureGetSizeEx(
                        CameraIndex,
                        out _width,
                        out _height,
                        out _bitCount);
            }

            if (_bitCount != 8)
                throw new NotSupportedException(
                    "8-bit grayscale acquisition required. Reported bit depth: " +
                    _bitCount);

            _bufferSize =
                checked(
                    _width *
                    _height);

            _buffer =
                Marshal.AllocHGlobal(
                    _bufferSize);

            _latestRaw =
                new byte[_bufferSize];

            int exposureResult;

            lock (_apiLock)
            {
                exposureResult =
                    Native.KSJ_ExposureTimeGet(
                        CameraIndex,
                        out _actualExposureMs);
            }

            if (
                _actualExposureMs > 0.0F &&
                _actualExposureMs <= 10000.0F)
            {
                decimal value =
                    (decimal)_actualExposureMs;

                if (value < _exposureBox.Minimum)
                    value =
                        _exposureBox.Minimum;

                if (value > _exposureBox.Maximum)
                    value =
                        _exposureBox.Maximum;

                _exposureBox.Value = value;
            }

            _connected = true;

            _connectButton.Enabled = false;
            _startButton.Enabled = true;
            _snapshotButton.Enabled = true;
            _disconnectButton.Enabled = true;
            _applyExposureButton.Enabled = true;
            _exposureBox.Enabled = true;
            _startRecordButton.Enabled = true;

            _deviceLabel.Text =
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Device: MUS40M-G | Count={0} | Type={1} | IndexSerial={2} | {3}x{4}x8bit",
                    count,
                    deviceType,
                    serial,
                    _width,
                    _height);

            _statusLabel.Text =
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Connected: Init={0} Info={1} Size={2} ExposureGet={3}; exposure approximately {4:F3} ms.",
                    initResult,
                    infoResult,
                    sizeResult,
                    exposureResult,
                    _actualExposureMs);

            StartPreview();
        }
        catch (Exception ex)
        {
            SafeRelease();

            MessageBox.Show(
                ex.ToString(),
                "Camera connection failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            _statusLabel.Text =
                "Connection failed: " +
                ex.Message;
        }
    }

    private void StartPreview()
    {
        if (
            !_connected ||
            _previewRequested)
            return;

        _previewRequested = true;

        _captureThread =
            new Thread(
                new ThreadStart(
                    CaptureLoop));

        _captureThread.IsBackground = true;
        _captureThread.Name =
            "MUS40M-G Capture Thread";

        _captureThread.Start();

        _startButton.Enabled = false;
        _stopButton.Enabled = true;
        _statusLabel.Text =
            "Live preview started.";
    }

    private void StopPreview()
    {
        if (_recordingRequested)
        {
            MessageBox.Show(
                "Stop recording before stopping preview.",
                "Recording in progress",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _previewRequested = false;

        if (
            _captureThread != null &&
            _captureThread.IsAlive)
        {
            _captureThread.Join(3000);
        }

        _captureThread = null;
        _startButton.Enabled = _connected;
        _stopButton.Enabled = false;
        _statusLabel.Text =
            "Live preview stopped; camera remains connected.";
    }

    private void CaptureLoop()
    {
        Stopwatch fpsClock =
            Stopwatch.StartNew();

        long captureCount = 0;
        long displayCount = 0;
        long lastDisplayTick = 0;

        while (
            _previewRequested &&
            _connected)
        {
            int result;

            lock (_apiLock)
            {
                result =
                    Native.KSJ_CaptureRgbData(
                        CameraIndex,
                        _buffer);
            }

            if (
                !_previewRequested ||
                !_connected)
                break;

            if (result <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            long captureTick =
                Stopwatch.GetTimestamp();

            DateTime captureTime =
                DateTime.Now;

            long sourceFrame =
                Interlocked.Increment(
                    ref _latestFrameNumber);

            captureCount++;

            bool needDisplay =
                lastDisplayTick == 0 ||
                (
                    captureTick -
                    lastDisplayTick
                ) *
                1000.0 /
                Stopwatch.Frequency
                >= 33.0;

            bool needRecord =
                _recordingRequested &&
                captureTick >=
                _nextRecordTick;

            if (
                needDisplay ||
                needRecord)
            {
                byte[] frame =
                    new byte[_bufferSize];

                Marshal.Copy(
                    _buffer,
                    frame,
                    0,
                    _bufferSize);

                lock (_frameLock)
                {
                    _latestRaw = frame;
                    _latestCaptureReturn = result;
                    _latestFrameTime = captureTime;
                }

                if (needRecord)
                {
                    RecordedFrame recordFrame =
                        new RecordedFrame();

                    recordFrame.Data = frame;
                    recordFrame.SourceFrameNumber =
                        sourceFrame;
                    recordFrame.CaptureTick =
                        captureTick;
                    recordFrame.ComputerTime =
                        captureTime;
                    recordFrame.ExposureMs =
                        _actualExposureMs;

                    LosslessVideoRecorder recorder =
                        _recorder;

                    if (recorder != null)
                    {
                        recorder.TryEnqueue(
                            recordFrame);
                    }

                    long next =
                        _nextRecordTick +
                        _recordIntervalTicks;

                    if (
                        next <
                        captureTick -
                        _recordIntervalTicks * 4)
                    {
                        next =
                            captureTick +
                            _recordIntervalTicks;
                    }

                    _nextRecordTick = next;
                }

                if (needDisplay)
                {
                    lastDisplayTick =
                        captureTick;

                    if (
                        Interlocked.CompareExchange(
                            ref _displayPending,
                            1,
                            0) == 0)
                    {
                        FrameStats stats;

                        Bitmap bitmap =
                            CreateDisplayBitmap(
                                frame,
                                _width,
                                _height,
                                _autoStretch,
                                out stats);

                        BeginInvoke(
                            new MethodInvoker(
                                delegate
                                {
                                    Image old =
                                        _picture.Image;

                                    _picture.Image =
                                        bitmap;

                                    if (old != null)
                                        old.Dispose();

                                    _brightnessLabel.Text =
                                        string.Format(
                                            CultureInfo.InvariantCulture,
                                            "Intensity: mean={0:F1}  P1={1}  P99={2}  Saturated={3:F2}%",
                                            stats.Mean,
                                            stats.PercentileLow,
                                            stats.PercentileHigh,
                                            stats.SaturatedPercent);

                                    _latestHistogram =
                                        stats.Histogram;

                                    if (_histogramPanel != null)
                                        _histogramPanel.Invalidate();

                                    _picture.Invalidate();

                                    displayCount++;

                                    Interlocked.Exchange(
                                        ref _displayPending,
                                        0);
                                }));
                    }
                }
            }

            if (
                fpsClock.ElapsedMilliseconds >=
                1000)
            {
                double seconds =
                    fpsClock.Elapsed.TotalSeconds;

                _captureFps =
                    captureCount /
                    seconds;

                _displayFps =
                    displayCount /
                    seconds;

                captureCount = 0;
                displayCount = 0;
                fpsClock.Restart();

                BeginInvoke(
                    new MethodInvoker(
                        delegate
                        {
                            _fpsLabel.Text =
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Capture FPS: {0:F1}  Display FPS: {1:F1}",
                                    _captureFps,
                                    _displayFps);
                        }));
            }
        }
    }

    private void StartRecording()
    {
        if (!_connected)
        {
            MessageBox.Show(
                "Connect the camera first.");
            return;
        }

        if (!_previewRequested)
            StartPreview();

        if (_recordingRequested)
            return;

        try
        {
            int targetFps =
                Convert.ToInt32(
                    _recordFpsBox.Value);

            float exposure;

            lock (_apiLock)
            {
                Native.KSJ_ExposureTimeGet(
                    CameraIndex,
                    out exposure);
            }

            _actualExposureMs = exposure;

            string root =
                Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "recordings");

            Directory.CreateDirectory(root);

            _recorder =
                new LosslessVideoRecorder();

            _recorder.Start(
                root,
                _width,
                _height,
                targetFps,
                _actualExposureMs,
                _syncStore,
                _experimentBox == null
                    ? ""
                    : _experimentBox.Text,
                _reviewCopyBox != null &&
                    _reviewCopyBox.Checked);

            _recordIntervalTicks =
                Math.Max(
                    1L,
                    Stopwatch.Frequency /
                    targetFps);

            _nextRecordTick =
                Stopwatch.GetTimestamp();

            _recordingRequested = true;

            _startRecordButton.Enabled = false;
            _stopRecordButton.Enabled = true;
            _recordFpsBox.Enabled = false;
            _disconnectButton.Enabled = false;
            _experimentBox.Enabled = false;
            _reviewCopyBox.Enabled = false;

            _statusLabel.Text =
                _reviewCopyBox.Checked
                ? "Recording raw AVI and data-overlay review video."
                : "Recording raw AVI and per-frame timestamps.";
        }
        catch (Exception ex)
        {
            _recordingRequested = false;

            if (_recorder != null)
            {
                _recorder.Dispose();
                _recorder = null;
            }

            MessageBox.Show(
                ex.ToString(),
                "Recording start failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void StopRecording()
    {
        if (!_recordingRequested)
            return;

        _recordingRequested = false;
        _stopRecordButton.Enabled = false;
        _statusLabel.Text =
            "Stopping recording and writing AVI index...";

        Application.DoEvents();

        LosslessVideoRecorder recorder =
            _recorder;

        if (recorder != null)
        {
            recorder.StopAndWait();
        }

        string error =
            recorder == null
            ? ""
            : recorder.LastError;

        string session =
            recorder == null
            ? ""
            : recorder.SessionDirectory;

        if (recorder != null)
        {
            recorder.Dispose();
            _recorder = null;
        }

        _startRecordButton.Enabled = _connected;
        _stopRecordButton.Enabled = false;
        _recordFpsBox.Enabled = true;
        _experimentBox.Enabled = true;
        _reviewCopyBox.Enabled = true;
        _disconnectButton.Enabled = _connected;
        _experimentBox.Enabled = true;
        _reviewCopyBox.Enabled = true;

        if (error.Length > 0)
        {
            _statusLabel.Text =
                "Recording stopped with a disk write error.";

            MessageBox.Show(
                error,
                "Recording write error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        else
        {
            _statusLabel.Text =
                session.Length > 0
                ? "Recording saved: " + session
                : "Recording stopped and AVI index finalized.";
        }
    }

    private void UpdateRecorderStatus()
    {
        LosslessVideoRecorder recorder =
            _recorder;

        if (
            recorder == null ||
            !_recordingRequested)
        {
            if (
                recorder == null)
            {
                _recordStatusLabel.Text =
                    "Recording: idle";
                _recordStatusLabel.ForeColor =
                    Color.FromArgb(
                        76, 91, 108);
            }

            return;
        }

        double gb =
            recorder.BytesWritten /
            1024.0 /
            1024.0 /
            1024.0;

        _recordStatusLabel.Text =
            string.Format(
                CultureInfo.InvariantCulture,
                "REC | Written {0} | Camera drops {1} | CH297 matched {2}/unmatched {3} | Queue {4}/256 | {5:F3} GB | {6}",
                recorder.FramesWritten,
                recorder.FramesDropped,
                recorder.FramesBoundToCh297,
                recorder.FramesUnboundToCh297,
                recorder.QueueCount,
                gb,
                Path.GetFileName(
                    recorder.CurrentAviPath));

        _recordStatusLabel.ForeColor =
            recorder.FramesDropped == 0
            ? Color.FromArgb(
                172, 36, 64)
            : Color.Red;
    }

    private void ApplyExposure()
    {
        if (!_connected)
            return;

        float requested =
            (float)_exposureBox.Value;

        try
        {
            int setResult;
            float actual;
            int getResult;

            lock (_apiLock)
            {
                setResult =
                    Native.KSJ_ExposureTimeSet(
                        CameraIndex,
                        requested);

                getResult =
                    Native.KSJ_ExposureTimeGet(
                        CameraIndex,
                        out actual);
            }

            _actualExposureMs = actual;

            if (
                actual > 0.0F &&
                actual <= 10000.0F)
            {
                decimal value =
                    (decimal)actual;

                if (value < _exposureBox.Minimum)
                    value =
                        _exposureBox.Minimum;

                if (value > _exposureBox.Maximum)
                    value =
                        _exposureBox.Maximum;

                _exposureBox.Value = value;
            }

            _statusLabel.Text =
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Exposure applied: requested {0:F3} ms, actual {1:F3} ms; Set={2} Get={3}.",
                    requested,
                    actual,
                    setResult,
                    getResult);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.ToString(),
                "Exposure update failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void SaveSnapshot()
    {
        if (!_connected)
            return;

        byte[] raw;
        long frameNo;
        int captureReturn;

        lock (_frameLock)
        {
            if (_latestRaw == null)
            {
                MessageBox.Show(
                    "No image available to save.");
                return;
            }

            raw =
                (byte[])_latestRaw.Clone();

            frameNo =
                Interlocked.Read(
                    ref _latestFrameNumber);

            captureReturn =
                _latestCaptureReturn;
        }

        string folder =
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "snapshots");

        Directory.CreateDirectory(folder);

        string stamp =
            DateTime.Now.ToString(
                "yyyyMMdd_HHmmss_fff");

        string prefix =
            Path.Combine(
                folder,
                "MUS40M_G_" + stamp);

        FrameStats rawStats;

        Bitmap rawBmp =
            CreateDisplayBitmap(
                raw,
                _width,
                _height,
                false,
                out rawStats);

        rawBmp.Save(
            prefix + "_raw.bmp",
            ImageFormat.Bmp);

        rawBmp.Dispose();

        FrameStats displayStats;

        Bitmap displayBmp =
            CreateDisplayBitmap(
                raw,
                _width,
                _height,
                _autoStretch,
                out displayStats);

        displayBmp.Save(
            prefix + "_display.bmp",
            ImageFormat.Bmp);

        displayBmp.Dispose();

        File.WriteAllBytes(
            prefix + ".raw",
            raw);

        File.WriteAllText(
            prefix + "_metadata.txt",
            string.Format(
                CultureInfo.InvariantCulture,
                "Time={0}\r\nFrameNumber={1}\r\nCaptureReturn={2}\r\nWidth={3}\r\nHeight={4}\r\nBitCount=8\r\nExposureMs={5:F6}\r\nRawMin={6}\r\nRawMax={7}\r\nRawMean={8:F4}\r\nAutoStretch={9}\r\nCaptureFPS={10:F3}\r\nDisplayFPS={11:F3}\r\n",
                DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff"),
                frameNo,
                captureReturn,
                _width,
                _height,
                _actualExposureMs,
                rawStats.Min,
                rawStats.Max,
                rawStats.Mean,
                _autoStretch,
                _captureFps,
                _displayFps),
            new UTF8Encoding(true));

        _statusLabel.Text =
            "Snapshot saved: " +
            prefix +
            "_raw.bmp";
    }

    private void OpenRecordingsDirectory()
    {
        string folder =
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "recordings");

        Directory.CreateDirectory(folder);

        Process.Start(
            "explorer.exe",
            folder);
    }

    private void DisconnectCamera()
    {
        if (_recordingRequested)
        {
            MessageBox.Show(
                "Stop recording first.");
            return;
        }

        StopPreview();
        SafeRelease();

        _statusLabel.Text =
            "Camera resources released.";
    }

    private void SafeRelease()
    {
        if (_recordingRequested)
        {
            try
            {
                StopRecording();
            }
            catch
            {
            }
        }

        _previewRequested = false;

        if (
            _captureThread != null &&
            _captureThread.IsAlive)
        {
            _captureThread.Join(3000);
        }

        _captureThread = null;

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(
                _buffer);

            _buffer = IntPtr.Zero;
        }

        if (_initialized)
        {
            try
            {
                lock (_apiLock)
                {
                    Native.KSJ_UnInit();
                }
            }
            catch
            {
            }

            _initialized = false;
        }

        _connected = false;

        _connectButton.Enabled = true;
        _startButton.Enabled = false;
        _stopButton.Enabled = false;
        _snapshotButton.Enabled = false;
        _disconnectButton.Enabled = false;
        _applyExposureButton.Enabled = false;
        _exposureBox.Enabled = false;
        _startRecordButton.Enabled = false;
        _stopRecordButton.Enabled = false;
        _recordFpsBox.Enabled = true;

        _deviceLabel.Text =
            "Device: disconnected";

        _fpsLabel.Text =
            "Capture FPS: --  Display FPS: --";

        _brightnessLabel.Text =
            "Brightness: --";

        _latestHistogram = null;
        _latestFrameTime = DateTime.MinValue;

        if (_histogramPanel != null)
            _histogramPanel.Invalidate();

        Image old =
            _picture.Image;

        _picture.Image = null;

        if (old != null)
            old.Dispose();
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
            // The form may be closing while a background callback is returning.
        }
    }

    private void OnFormClosing(
        object sender,
        FormClosingEventArgs e)
    {
        if (_recordingRequested)
        {
            DialogResult answer =
                MessageBox.Show(
                    "Recording is active. Stop recording and finalize its index before closing.\r\n\r\nStop recording and exit now?",
                    "Confirm exit",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        SafeRelease();

        if (_ownsSyncServer && _syncServer != null)
        {
            _syncServer.Stop();
            _syncServer.Dispose();
            _syncServer = null;
        }

        if (_ownsSyncStore)
            _syncStore.Dispose();

        if (_statusTimer != null)
        {
            _statusTimer.Stop();
            _statusTimer.Dispose();
        }
    }

    private static Bitmap CreateDisplayBitmap(
        byte[] raw,
        int width,
        int height,
        bool autoStretch,
        out FrameStats stats)
    {
        int min = 255;
        int max = 0;
        long sum = 0;
        int[] histogram = new int[256];

        for (int i = 0; i < raw.Length; i++)
        {
            int value = raw[i];

            if (value < min)
                min = value;

            if (value > max)
                max = value;

            sum += value;
            histogram[value]++;
        }

        int percentileLow =
            FindHistogramPercentile(
                histogram,
                raw.Length,
                0.01);

        int percentileHigh =
            FindHistogramPercentile(
                histogram,
                raw.Length,
                0.99);

        stats = new FrameStats
        {
            Min = min,
            Max = max,
            PercentileLow = percentileLow,
            PercentileHigh = percentileHigh,
            Histogram = histogram,
            SaturatedPercent =
                raw.Length > 0
                ? histogram[255] * 100.0 / raw.Length
                : 0.0,
            Mean =
                raw.Length > 0
                ? (double)sum /
                  raw.Length
                : 0.0
        };

        byte[] display = raw;

        if (
            autoStretch &&
            percentileHigh > percentileLow)
        {
            display =
                new byte[raw.Length];

            double scale =
                255.0 /
                (percentileHigh - percentileLow);

            for (
                int i = 0;
                i < raw.Length;
                i++)
            {
                int value =
                    (int)Math.Round(
                        (raw[i] - percentileLow) *
                        scale);

                if (value < 0)
                    value = 0;

                if (value > 255)
                    value = 255;

                display[i] =
                    (byte)value;
            }
        }

        Bitmap bitmap =
            new Bitmap(
                width,
                height,
                PixelFormat.Format8bppIndexed);

        ColorPalette palette =
            bitmap.Palette;

        for (int i = 0; i < 256; i++)
        {
            palette.Entries[i] =
                Color.FromArgb(
                    i, i, i);
        }

        bitmap.Palette = palette;

        Rectangle rect =
            new Rectangle(
                0,
                0,
                width,
                height);

        BitmapData data =
            bitmap.LockBits(
                rect,
                ImageLockMode.WriteOnly,
                PixelFormat.Format8bppIndexed);

        try
        {
            int targetStride =
                data.Stride;

            for (
                int row = 0;
                row < height;
                row++)
            {
                Marshal.Copy(
                    display,
                    row * width,
                    IntPtr.Add(
                        data.Scan0,
                        row * targetStride),
                    width);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static int FindHistogramPercentile(
        int[] histogram,
        int total,
        double percentile)
    {
        if (histogram == null || histogram.Length == 0 || total <= 0)
            return 0;

        int target = Math.Max(
            1,
            (int)Math.Ceiling(total * percentile));

        int cumulative = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= target)
                return i;
        }

        return histogram.Length - 1;
    }

    private struct FrameStats
    {
        public int Min;
        public int Max;
        public int PercentileLow;
        public int PercentileHigh;
        public int[] Histogram;
        public double SaturatedPercent;
        public double Mean;
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(
                new PreviewForm());
        }
        catch (Exception ex)
        {
            string logPath =
                Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "camera_workstation_v4_startup_error.log");

            try
            {
                File.WriteAllText(
                    logPath,
                    ex.ToString(),
                    new UTF8Encoding(true));
            }
            catch
            {
            }

            MessageBox.Show(
                ex.ToString() +
                "\r\n\r\nLog: " +
                logPath,
                "NEFU-China iDEC camera error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
