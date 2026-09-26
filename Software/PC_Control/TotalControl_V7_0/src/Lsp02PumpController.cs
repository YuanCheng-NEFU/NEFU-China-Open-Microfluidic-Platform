using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;

internal sealed class Lsp02PumpParameters
{
    public byte Channel;
    public byte WorkMode;
    public byte SyringeCode;
    public ushort VolumeValue;
    public byte VolumeUnit;
    public ushort InfusionTimeValue;
    public byte InfusionTimeUnit;
    public ushort WithdrawalTimeValue;
    public byte WithdrawalTimeUnit;
    public ushort RepeatCount;
    public ushort IntervalValue;

    public string Describe()
    {
        return string.Format(
            "CH{0} Mode={1} Syringe=0x{2:X2} Volume={3} Unit={4} " +
            "InfuseTime={5}/{6} WithdrawTime={7}/{8} Repeat={9} Interval={10}",
            Channel,
            WorkMode,
            SyringeCode,
            VolumeValue,
            VolumeUnit,
            InfusionTimeValue,
            InfusionTimeUnit,
            WithdrawalTimeValue,
            WithdrawalTimeUnit,
            RepeatCount,
            IntervalValue);
    }
}

internal sealed class Lsp02PumpStatus
{
    public byte SystemStatus;
    public byte FastStatus;

    public string Describe()
    {
        return string.Format(
            "System=0x{0:X2}, Fast=0x{1:X2}",
            SystemStatus,
            FastStatus);
    }
}

internal sealed class Lsp02PumpController : IDisposable
{
    private readonly object _sync = new object();
    private SerialPort _port;
    private byte _address = 1;

    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return _port != null && _port.IsOpen;
            }
        }
    }

    public string PortName
    {
        get
        {
            lock (_sync)
            {
                return _port == null ? "" : _port.PortName;
            }
        }
    }

    public static string[] GetAvailablePorts()
    {
        string[] ports = SerialPort.GetPortNames();
        Array.Sort(
            ports,
            delegate(string left, string right)
            {
                return string.Compare(
                    left,
                    right,
                    StringComparison.OrdinalIgnoreCase);
            });
        return ports;
    }

    public void Open(
        string portName,
        int baudRate,
        Parity parity,
        byte address)
    {
        if (string.IsNullOrEmpty(portName))
            throw new ArgumentException("Select the syringe-pump serial port.", "portName");

        if (address < 1 || address > 30)
            throw new ArgumentOutOfRangeException(
                "address",
                "Device address must be between 1 and 30.");

        lock (_sync)
        {
            CloseInternal();

            SerialPort port = new SerialPort(
                portName,
                baudRate,
                parity,
                8,
                StopBits.One);

            port.Handshake = Handshake.None;
            port.ReadTimeout = 120;
            port.WriteTimeout = 700;
            port.DtrEnable = false;
            port.RtsEnable = false;
            try
            {
                port.Open();

                // USB-RS485 adapters and the pump controller need a short period
                // after opening before the first half-duplex request. Sending RSE
                // immediately can trigger a driver-level semaphore timeout.
                Thread.Sleep(300);
                port.DiscardInBuffer();
                port.DiscardOutBuffer();

                _port = port;
                _address = address;
            }
            catch
            {
                try
                {
                    if (port.IsOpen)
                        port.Close();
                    port.Dispose();
                }
                catch
                {
                }

                throw;
            }
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            CloseInternal();
        }
    }

    public Lsp02PumpParameters ReadParameters(byte channel)
    {
        ValidateChannel(channel);

        byte[] response = Exchange(
            BuildFrame(
                _address,
                new byte[]
                {
                    (byte)'R',
                    (byte)'S',
                    (byte)'P',
                    channel
                }),
            "RSP");

        if (response.Length != 19)
            throw new InvalidDataException(
                "Invalid RSP response length; received PDU length: " +
                response.Length.ToString());

        Lsp02PumpParameters value =
            new Lsp02PumpParameters();

        value.Channel = response[3];
        value.WorkMode = response[4];
        value.SyringeCode = response[5];
        value.VolumeValue = ReadUInt16(response, 6);
        value.VolumeUnit = response[8];
        value.InfusionTimeValue = ReadUInt16(response, 9);
        value.InfusionTimeUnit = response[11];
        value.WithdrawalTimeValue = ReadUInt16(response, 12);
        value.WithdrawalTimeUnit = response[14];
        value.RepeatCount = ReadUInt16(response, 15);
        value.IntervalValue = ReadUInt16(response, 17);

        return value;
    }

    public void WriteParameters(Lsp02PumpParameters value)
    {
        if (value == null)
            throw new ArgumentNullException("value");

        ValidateChannel(value.Channel);

        List<byte> pdu = new List<byte>();
        pdu.Add((byte)'W');
        pdu.Add((byte)'S');
        pdu.Add((byte)'P');
        pdu.Add(value.Channel);
        pdu.Add(value.WorkMode);
        pdu.Add(value.SyringeCode);
        AddUInt16(pdu, value.VolumeValue);
        pdu.Add(value.VolumeUnit);
        AddUInt16(pdu, value.InfusionTimeValue);
        pdu.Add(value.InfusionTimeUnit);
        AddUInt16(pdu, value.WithdrawalTimeValue);
        pdu.Add(value.WithdrawalTimeUnit);
        AddUInt16(pdu, value.RepeatCount);
        AddUInt16(pdu, value.IntervalValue);

        byte[] response = Exchange(
            BuildFrame(
                _address,
                pdu.ToArray()),
            "WSP");

        RequireAck(response, "WSP");
    }

    public Lsp02PumpStatus ReadRunningStatus()
    {
        byte[] response = Exchange(
            BuildFrame(
                _address,
                Encoding.ASCII.GetBytes("RSE")),
            "RSE");

        if (response.Length != 5)
            throw new InvalidDataException(
                "Invalid RSE response length.");

        return new Lsp02PumpStatus
        {
            SystemStatus = response[3],
            FastStatus = response[4]
        };
    }

    public Lsp02PumpStatus ProbeRunningStatus(int attempts)
    {
        attempts = Math.Max(1, Math.Min(5, attempts));
        Exception last = null;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return ReadRunningStatus();
            }
            catch (TimeoutException ex)
            {
                last = ex;
            }
            catch (IOException ex)
            {
                last = ex;
            }

            if (attempt < attempts)
            {
                Thread.Sleep(250);
                lock (_sync)
                {
                    if (_port != null && _port.IsOpen)
                    {
                        try { _port.DiscardInBuffer(); }
                        catch { }
                    }
                }
            }
        }

        throw new IOException(
            "Pump read-only handshake failed " + attempts.ToString() +
            " consecutive times. The serial port opened, but no valid RSE status was returned. " +
            "Check the USB-RS485 driver, A/B polarity, pump address and serial settings. Last error: " +
            (last == null ? "Unknown" : last.Message),
            last);
    }

    public void StartDocumentedChannelOne()
    {
        // Manufacturer example explicitly defines 0x03/0x00 as channel 1 run.
        // Channel 2 is intentionally not guessed before bench verification.
        SetRunningStatus(0x03, 0x00);
    }

    public void StopAll()
    {
        SetRunningStatus(0x00, 0x00);
    }

    public void SetRunningStatus(
        byte systemStatus,
        byte fastStatus)
    {
        byte[] response = Exchange(
            BuildFrame(
                _address,
                new byte[]
                {
                    (byte)'W',
                    (byte)'S',
                    (byte)'E',
                    systemStatus,
                    fastStatus
                }),
            "WSE");

        RequireAck(response, "WSE");
    }

    private byte[] Exchange(
        byte[] request,
        string expectedCommand)
    {
        lock (_sync)
        {
            if (_port == null || !_port.IsOpen)
                throw new InvalidOperationException(
                    "The syringe-pump serial port is disconnected.");

            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
            string requestHex = FormatHex(request);

            try
            {
                _port.Write(request, 0, request.Length);
                // SerialPort.Write has already handed the frame to the Windows
                // driver. SerialStream.Flush adds no protocol guarantee here
                // and some Prolific/USB-RS485 drivers can block it until the
                // Windows "semaphore timeout" expires.
            }
            catch (IOException ex)
            {
                throw new IOException(
                    "USB-RS485 write failed (Windows serial driver error). Sent frame: " +
                    requestHex + ". Reconnect the adapter and close other software using this COM port, then retry.",
                    ex);
            }

            Stopwatch timeout = Stopwatch.StartNew();
            int first = -1;
            List<byte> received = new List<byte>();

            while (timeout.ElapsedMilliseconds < 1200)
            {
                try
                {
                    int value = _port.ReadByte();
                    if (value >= 0)
                        received.Add((byte)value);
                    if (value == 0xE9)
                    {
                        first = value;
                        break;
                    }
                }
                catch (TimeoutException)
                {
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        "USB-RS485 read failed (Windows serial driver error). Sent frame: " +
                        requestHex + "; received: " + FormatHex(received.ToArray()) +
                        ". Reconnect the adapter and check its driver.",
                        ex);
                }
            }

            if (first != 0xE9)
                throw new TimeoutException(
                    "No pump response. Sent frame: " + requestHex +
                    "; received: " + FormatHex(received.ToArray()) +
                    ". Check RS485 A/B wiring, address, baud rate and parity.");

            byte address = ReadByteWithDeadline(timeout, 1500);
            byte length = ReadByteWithDeadline(timeout, 1500);

            byte[] pdu = new byte[length];
            for (int i = 0; i < pdu.Length; i++)
                pdu[i] = ReadByteWithDeadline(timeout, 1500);

            byte checksum = ReadByteWithDeadline(timeout, 1500);

            List<byte> complete = new List<byte>();
            complete.Add(address);
            complete.Add(length);
            complete.AddRange(pdu);

            byte calculated = 0;
            for (int i = 0; i < complete.Count; i++)
                calculated ^= complete[i];

            if (calculated != checksum)
            {
                throw new InvalidDataException(
                    string.Format(
                        "Pump response checksum mismatch: calculated 0x{0:X2}, received 0x{1:X2}.",
                        calculated,
                        checksum));
            }

            if (address != _address)
                throw new InvalidDataException(
                    "Pump response address does not match the selected device.");

            if (pdu.Length < 3)
                throw new InvalidDataException(
                    "Pump response PDU is too short.");

            string command = Encoding.ASCII.GetString(pdu, 0, 3);
            if (!string.Equals(
                    command,
                    expectedCommand,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Pump response command mismatch; expected " +
                    expectedCommand +
                    ", received " +
                    command +
                    ".");
            }

            return pdu;
        }
    }

    private byte ReadByteWithDeadline(
        Stopwatch timeout,
        int deadlineMilliseconds)
    {
        while (timeout.ElapsedMilliseconds < deadlineMilliseconds)
        {
            try
            {
                int value = _port.ReadByte();
                if (value >= 0)
                    return (byte)value;
            }
            catch (TimeoutException)
            {
            }
        }

        throw new TimeoutException(
            "Incomplete pump response.");
    }

    private static byte[] BuildFrame(
        byte address,
        byte[] pdu)
    {
        if (pdu == null || pdu.Length > 255)
            throw new ArgumentException("Invalid PDU length.", "pdu");

        byte[] frame = new byte[pdu.Length + 4];
        frame[0] = 0xE9;
        frame[1] = address;
        frame[2] = (byte)pdu.Length;
        Buffer.BlockCopy(pdu, 0, frame, 3, pdu.Length);

        byte checksum = 0;
        for (int i = 1; i < frame.Length - 1; i++)
            checksum ^= frame[i];

        frame[frame.Length - 1] = checksum;
        return frame;
    }

    private static ushort ReadUInt16(
        byte[] buffer,
        int offset)
    {
        return (ushort)(
            (buffer[offset] << 8) |
            buffer[offset + 1]);
    }

    private static void AddUInt16(
        List<byte> target,
        ushort value)
    {
        target.Add((byte)(value >> 8));
        target.Add((byte)(value & 0xFF));
    }

    private static string FormatHex(byte[] data)
    {
        if (data == null || data.Length == 0)
            return "<empty>";

        StringBuilder text = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0)
                text.Append(' ');
            text.Append(data[i].ToString("X2"));
        }
        return text.ToString();
    }

    private static void ValidateChannel(byte channel)
    {
        if (channel != 1 && channel != 2)
            throw new ArgumentOutOfRangeException(
                "channel",
                "Channel must be 1 or 2.");
    }

    private static void RequireAck(
        byte[] pdu,
        string command)
    {
        if (pdu == null || pdu.Length != 3)
            throw new InvalidDataException(
                command + "Invalid acknowledgement frame length.");
    }

    private void CloseInternal()
    {
        if (_port == null)
            return;

        try
        {
            if (_port.IsOpen)
                _port.Close();
        }
        finally
        {
            _port.Dispose();
            _port = null;
        }
    }

    public void Dispose()
    {
        Close();
    }
}
