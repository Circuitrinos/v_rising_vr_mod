using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VRisingVR;

/// <summary>
/// Receives the headset's camera view from the VRisingVR passthrough server on a Steam Frame
/// (headset/vrisingvr-passthrough.py) on a background thread: a token handshake, then length-prefixed JPEG
/// frames over TCP. Only the newest frame is kept. Nothing here touches Unity or the log, because BepInEx's
/// log writer isn't thread-safe: the main thread reads the status and the frames.
/// </summary>
internal sealed class PassthroughFeed
{
    private const int MaxFrameBytes = 8 << 20;
    private const int ConnectTimeoutMs = 5000;

    private readonly object _lock = new();
    private Thread _thread;
    private volatile bool _stop;
    private TcpClient _client; // closed by Stop() to unblock a read
    private byte[] _frame;
    private int _seq;
    private long _frameTicks = long.MinValue / 2;
    private string _status = "off";
    private int _statusVersion;
    private int _framesReceived;
    private long _bytesReceived;

    private string _host;
    private int _port;
    private string _token;
    private int _width;
    private int _fps;

    public bool Running => _thread != null;

    public bool Matches(string host, int port, string token, int width, int fps) =>
        _host == host && _port == port && _token == token && _width == width && _fps == fps;

    public void Start(string host, int port, string token, int width, int fps)
    {
        Stop();
        _host = host;
        _port = port;
        _token = token;
        _width = width;
        _fps = fps;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "VRisingVR passthrough" };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread == null)
            return;
        _stop = true;
        lock (_lock)
        {
            try { _client?.Close(); } catch { }
        }
        _thread.Join(2000);
        _thread = null;
        lock (_lock)
        {
            _frame = null;
            _frameTicks = long.MinValue / 2;
        }
        SetStatus("off");
    }

    /// <summary>The current status text and a counter that changes whenever the text does.</summary>
    public (string Text, int Version) Status
    {
        get { lock (_lock) return (_status, _statusVersion); }
    }

    /// <summary>The newest frame, if it is newer than <paramref name="seenSeq"/> (which is then updated).</summary>
    public bool TryTakeFrame(ref int seenSeq, out byte[] jpeg)
    {
        lock (_lock)
        {
            jpeg = _frame;
            if (jpeg == null || _seq == seenSeq)
                return false;
            seenSeq = _seq;
            return true;
        }
    }

    public long MillisecondsSinceFrame
    {
        get { lock (_lock) return Environment.TickCount64 - _frameTicks; }
    }

    /// <summary>Frames and bytes received since the last call.</summary>
    public (int Frames, long Bytes) TakeStats()
    {
        lock (_lock)
        {
            var stats = (_framesReceived, _bytesReceived);
            _framesReceived = 0;
            _bytesReceived = 0;
            return stats;
        }
    }

    private void SetStatus(string status)
    {
        lock (_lock)
        {
            if (_status == status)
                return;
            _status = status;
            _statusVersion++;
        }
    }

    private void Run()
    {
        int retryMs = 2000;
        SetStatus($"connecting to {_host}:{_port}");
        while (!_stop)
        {
            bool streamed = false;
            try
            {
                using var client = new TcpClient { NoDelay = true, ReceiveTimeout = 10000, SendTimeout = 5000 };
                lock (_lock)
                {
                    if (_stop)
                        break;
                    _client = client;
                }
                if (!client.ConnectAsync(_host, _port).Wait(ConnectTimeoutMs))
                    throw new IOException("timed out");

                NetworkStream stream = client.GetStream();
                byte[] hello = Encoding.UTF8.GetBytes(
                    $"{{\"token\": {JsonString(_token)}, \"width\": {_width}, \"fps\": {_fps}, \"quality\": 5}}\n");
                stream.Write(hello, 0, hello.Length);

                string reply = ReadLine(stream);
                if (!reply.StartsWith("OK ", StringComparison.Ordinal))
                {
                    // A wrong token won't fix itself: check again only now and then.
                    SetStatus($"the headset refused the connection ({reply}); check Passthrough.Token");
                    retryMs = 30000;
                }
                else
                {
                    SetStatus($"streaming {reply.Substring(3).Replace(' ', 'x')} from {_host}");
                    streamed = true;
                    retryMs = 2000;
                    var header = new byte[4];
                    while (!_stop)
                    {
                        ReadExactly(stream, header, 4);
                        int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                        if (length == 0)
                            continue; // keep-alive: the headset's cameras are paused (standby), nothing new to show
                        if (length < 0 || length > MaxFrameBytes)
                            throw new IOException($"bad frame length {length}");
                        var frame = new byte[length];
                        ReadExactly(stream, frame, length);
                        lock (_lock)
                        {
                            _frame = frame;
                            _seq++;
                            _frameTicks = Environment.TickCount64;
                            _framesReceived++;
                            _bytesReceived += length;
                        }
                    }
                }
            }
            catch (Exception e) when (!_stop)
            {
                string reason = e is AggregateException { InnerException: { } inner } ? inner.Message : e.Message;
                SetStatus(streamed
                    ? $"lost the connection to {_host} ({reason}); reconnecting"
                    : $"can't reach {_host}:{_port} ({reason}); retrying");
            }
            catch
            {
                // stopping
            }
            finally
            {
                lock (_lock)
                    _client = null;
            }

            for (int waited = 0; waited < retryMs && !_stop; waited += 100)
                Thread.Sleep(100);
        }
    }

    private static string ReadLine(Stream stream)
    {
        var line = new StringBuilder();
        while (line.Length < 256)
        {
            int b = stream.ReadByte();
            if (b < 0)
                throw new IOException("closed during the handshake");
            if (b == '\n')
                break;
            line.Append((char)b);
        }
        return line.ToString().Trim();
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
                throw new IOException("connection closed");
            offset += read;
        }
    }

    private static string JsonString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
