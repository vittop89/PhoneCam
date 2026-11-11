using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;

namespace PhoneCam;

sealed record StreamOptions(string CameraId, string Size, int Fps, int BitRate, bool Torch);

sealed record VideoPacket(byte[] Data, long Pts, bool KeyFrame, bool HasConfig);

/// <summary>
/// One camera stream from the phone, produced by scrcpy-server (v4.1 protocol) through an adb forward tunnel.
/// </summary>
sealed class CameraSession(Adb adb, string serial) : IDisposable
{
    const string ServerVersion = "4.1";
    const string DeviceJar = "/data/local/tmp/phonecam-server.jar";
    const int CodecH264 = 0x68323634; // "h264"

    const byte ControlResetVideo = 17;
    const byte ControlSetTorch = 18;

    readonly CancellationTokenSource cts = new();
    readonly object sendLock = new();
    readonly Queue<string> serverLines = new();
    Process? serverProcess;
    TcpClient? videoClient;
    TcpClient? controlClient;
    int forwardPort;
    int finished;
    long lastKeyFrameRequest;

    public event Action<int, int>? SessionStarted;
    public event Action<VideoPacket>? PacketReceived;
    public event Action<string>? ServerLine;
    /// <summary>Raised once when the stream stops on its own; the argument is the error text.</summary>
    public event Action<string>? Failed;

    // Pushed before every stream: with cleanup=true the server deletes its own jar when it starts.
    public static async Task PushServerAsync(Adb adb, string serial, string localJar)
    {
        var (exitCode, output) = await adb.RunAsync($"-s {serial} push \"{localJar}\" {DeviceJar}", 30000);
        if (exitCode != 0)
            throw new InvalidOperationException("errors.serverCopyFailed:" + output.Trim());
    }

    public static async Task<List<CameraInfo>> ListCamerasAsync(Adb adb, string serial)
    {
        // cleanup=false: in list mode the server would otherwise delete its own jar.
        var (_, output) = await adb.RunAsync(
            $"-s {serial} shell CLASSPATH={DeviceJar} app_process / com.genymobile.scrcpy.Server {ServerVersion} " +
            "log_level=info video=false audio=false control=false cleanup=false list_cameras=true list_camera_sizes=true",
            30000);
        var cameras = CameraInfo.Parse(output);
        if (cameras.Count == 0)
            throw new InvalidOperationException(FirstErrorLine(output.Split('\n')) ?? "errors.noCameraAvailable");
        return cameras;
    }

    public async Task StartAsync(StreamOptions options)
    {
        int scid = Random.Shared.Next(0, int.MaxValue);
        var (exitCode, output) = await adb.RunAsync($"-s {serial} forward tcp:0 localabstract:scrcpy_{scid:x8}");
        if (exitCode != 0 || !int.TryParse(output.Trim(), out forwardPort))
            throw new InvalidOperationException("errors.adbForwardFailed:" + output.Trim());

        string command = string.Join(' ',
            $"CLASSPATH={DeviceJar}", "app_process", "/", "com.genymobile.scrcpy.Server", ServerVersion,
            $"scid={scid:x8}", "log_level=info", "tunnel_forward=true",
            "audio=false", "control=true", "video_codec=h264",
            "video_source=camera", $"camera_id={options.CameraId}", $"camera_size={options.Size}",
            $"camera_fps={options.Fps}", $"video_bit_rate={options.BitRate}",
            $"camera_torch={(options.Torch ? "true" : "false")}",
            "camera_zoom=1",
            "send_device_meta=false", "clipboard_autosync=false", "power_on=false", "cleanup=true");
        serverProcess = adb.StartShell(serial, command, OnServerLine);

        videoClient = await ConnectAsync(cts.Token);
        controlClient = new TcpClient { NoDelay = true };
        await controlClient.ConnectAsync("127.0.0.1", forwardPort, cts.Token);

        _ = Task.Run(() => ReadVideoAsync(videoClient.GetStream()));
        _ = Task.Run(() => DrainControlAsync(controlClient.GetStream()));
    }

    public void SetTorch(bool on) => SendControl(ControlSetTorch, on ? (byte)1 : (byte)0);

    /// <summary>Asks the encoder for a fresh keyframe, at most once every two seconds.</summary>
    public void RequestKeyFrame()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref lastKeyFrameRequest) < 2000)
            return;
        Interlocked.Exchange(ref lastKeyFrameRequest, now);
        SendControl(ControlResetVideo);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref finished, 1) == 1)
            return;
        Cleanup();
    }

    // With a forward tunnel, connect() succeeds on the adb side even before the server listens:
    // only the dummy byte proves that the phone end is really there.
    async Task<TcpClient> ConnectAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (serverProcess is { HasExited: true })
            {
                serverProcess.WaitForExit(); // flushes the last output lines into serverLines
                throw new InvalidOperationException(LastServerError() ?? "errors.cameraServerExited");
            }
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync("127.0.0.1", forwardPort, ct);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(2000);
                var dummy = new byte[1];
                if (await client.GetStream().ReadAsync(dummy, readTimeout.Token) == 1)
                    return client;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
            client.Dispose();
            await Task.Delay(100, ct);
        }
        throw new TimeoutException(LastServerError() ?? "errors.cameraDidNotOpen");
    }

    async Task ReadVideoAsync(NetworkStream stream)
    {
        try
        {
            var header = new byte[12];
            await stream.ReadExactlyAsync(header, 0, 4, cts.Token);
            int codec = BinaryPrimitives.ReadInt32BigEndian(header);
            if (codec is 0 or 1)
                throw new InvalidOperationException(LastServerError() ?? "errors.phoneRejectedConfig");
            if (codec != CodecH264)
                throw new InvalidOperationException($"errors.unexpectedCodec:0x{codec:x8}");

            byte[]? config = null;
            while (true)
            {
                await stream.ReadExactlyAsync(header, 0, 12, cts.Token);
                if ((header[0] & 0x80) != 0)
                {
                    SessionStarted?.Invoke(
                        BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)),
                        BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8)));
                    continue;
                }

                long ptsAndFlags = BinaryPrimitives.ReadInt64BigEndian(header);
                int size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
                if (size is < 0 or > 64 * 1024 * 1024)
                    throw new InvalidDataException("errors.invalidVideoPacket");
                var data = new byte[size];
                await stream.ReadExactlyAsync(data, 0, size, cts.Token);

                if ((ptsAndFlags & (1L << 62)) != 0)
                {
                    // SPS/PPS: the decoder wants them in front of the next keyframe.
                    config = data;
                    continue;
                }
                bool hasConfig = config != null;
                if (config != null)
                {
                    data = [.. config, .. data];
                    config = null;
                }
                PacketReceived?.Invoke(new VideoPacket(data, ptsAndFlags & ((1L << 61) - 1), (ptsAndFlags & (1L << 61)) != 0, hasConfig));
            }
        }
        catch (Exception e)
        {
            string reason = e is EndOfStreamException or IOException or ObjectDisposedException or OperationCanceledException
                ? LastServerError() ?? "Connessione con il telefono interrotta"
                : e.Message;
            Fail(reason);
        }
    }

    async Task DrainControlAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer, cts.Token) > 0)
            {
            }
        }
        catch
        {
        }
    }

    void SendControl(params byte[] message)
    {
        var client = controlClient;
        if (client == null || Volatile.Read(ref finished) == 1)
            return;
        try
        {
            lock (sendLock)
                client.GetStream().Write(message);
        }
        catch
        {
        }
    }

    void OnServerLine(string line)
    {
        lock (serverLines)
        {
            serverLines.Enqueue(line);
            while (serverLines.Count > 40)
                serverLines.Dequeue();
        }
        ServerLine?.Invoke(line);
    }

    string? LastServerError()
    {
        string[] lines;
        lock (serverLines)
            lines = [.. serverLines];
        return FirstErrorLine(lines);
    }

    /// <summary>Turns a raw server failure into a sentence a user can act on; the raw text stays in the log.</summary>
    public static (string? Key, string? Detail) FriendlyError(string raw)
    {
        if (raw.Contains("CAMERA_IN_USE") || raw.Contains("Higher-priority client") || raw.Contains("MAX_CAMERAS_IN_USE"))
            return ("messages.cameraBusy", null);
        if (raw.Contains("CAMERA_DISABLED"))
            return ("messages.cameraDisabled", null);
        if (raw.Contains("CAMERA_DISCONNECTED") || raw.Contains("CAMERA_ERROR"))
            return ("messages.cameraInterrupted", null);
        if (raw.Contains("Camera configuration error"))
            return ("messages.settingsRejected", null);
        if (raw.Contains("does not match the client"))
            return ("messages.incompatibleServer", null);
        if (raw.Contains("Video encoding error") || raw.Contains("MediaCodec") || raw.Contains("encoder"))
            return ("messages.encoderRejected", null);
        if (raw.StartsWith("errors.serverCopyFailed:", StringComparison.Ordinal))
            return ("messages.serverCopyFailed", raw["errors.serverCopyFailed:".Length..]);
        if (raw.StartsWith("errors.adbForwardFailed:", StringComparison.Ordinal))
            return ("messages.adbForwardFailed", raw["errors.adbForwardFailed:".Length..]);
        if (raw.StartsWith("errors.unexpectedCodec:", StringComparison.Ordinal))
            return ("messages.unexpectedCodec", raw["errors.unexpectedCodec:".Length..]);
        if (raw.StartsWith("errors.", StringComparison.Ordinal))
            return raw switch
            {
                "errors.noCameraAvailable" => ("messages.noCameraAvailable", null),
                "errors.cameraServerExited" => ("messages.cameraServerExited", null),
                "errors.cameraDidNotOpen" => ("messages.cameraDidNotOpen", null),
                "errors.phoneRejectedConfig" => ("messages.phoneRejectedConfig", null),
                "errors.invalidVideoPacket" => ("messages.invalidVideoPacket", null),
                "errors.noUsableLens" => ("messages.noUsableLens", null),
                "errors.noCameraFrame" => ("messages.noCameraFrame", null),
                _ => (null, raw),
            };
        return (null, raw);
    }

    static string? FirstErrorLine(IEnumerable<string> lines)
    {
        var errorLines = lines.Select(l => l.Trim()).ToList();
        int index = errorLines.FindIndex(l => l.Contains("ERROR:") || l.Contains("Exception"));
        if (index < 0)
            return null;
        string text = errorLines[index].Replace("[server] ", "").Replace("ERROR: ", "");
        // The exception message is usually on the next line when the first one is generic.
        if (index + 1 < errorLines.Count && errorLines[index + 1].Contains("Exception"))
            text += " — " + errorLines[index + 1];
        return text;
    }

    void Fail(string reason)
    {
        if (Interlocked.Exchange(ref finished, 1) == 1)
            return;
        Cleanup();
        Failed?.Invoke(reason);
    }

    void Cleanup()
    {
        cts.Cancel();
        videoClient?.Dispose();
        controlClient?.Dispose();
        try
        {
            if (serverProcess is { HasExited: false })
                serverProcess.Kill(true);
        }
        catch
        {
        }
        serverProcess?.Dispose();
        if (forwardPort != 0)
            _ = adb.RunAsync($"-s {serial} forward --remove tcp:{forwardPort}", 5000).ContinueWith(_ => { });
    }
}
