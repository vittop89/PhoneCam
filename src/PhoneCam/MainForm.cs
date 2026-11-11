using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PhoneCam;

sealed class MainForm : Form
{
    static readonly Color Background = Color.FromArgb(17, 17, 19);
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    readonly System.Windows.Forms.Timer pollTimer = new() { Interval = 2000 };
    readonly Settings settings = Settings.Load();
    readonly Adb adb = new(AppFiles.AdbExe);
    readonly string serverJar = AppFiles.ServerJar;
    readonly string logPath = Path.Combine(Settings.Folder, "phonecam.log");
    readonly object logLock = new();
    readonly string? selfTestInput;
    readonly string? selfTestOutput;
    readonly bool debug;

    CameraSession? session;
    string? sessionCameraId;
    string? sessionSize;
    bool sessionHasFrames;
    // Lens health for the connected phone, used to tell a lens the phone refuses from a general problem.
    string? lastWorkingCameraId;
    string? suspectCameraId;
    readonly HashSet<string> camerasWithFrames = new();
    readonly Dictionary<string, int> cameraStrikes = new();
    string? serial;
    string? camerasSerial;
    List<CameraInfo> cameras = [];
    bool pageReady;
    bool polling;
    bool torch;
    bool startedAdbServer;
    int failures;
    DateTime nextStartAttempt = DateTime.MinValue;
    int pendingPackets;
    bool droppingUntilKeyFrame;
    bool fullscreen;
    FormWindowState windowStateBeforeFullscreen;
    string lastStatusText = "";
    string lastStats = "";
    int statsCount;

    public MainForm(string[] args)
    {
        Text = "PhoneCam";
        BackColor = Background;
        ClientSize = new Size(1280, 780);
        MinimumSize = new Size(820, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(web);

        int selfTest = Array.IndexOf(args, "--selftest");
        if (selfTest >= 0 && selfTest + 2 < args.Length)
        {
            selfTestInput = args[selfTest + 1];
            selfTestOutput = args[selfTest + 2];
        }
        debug = args.Contains("--debug");
        pollTimer.Tick += (_, _) => _ = PollAsync();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            // Must come before any other CoreWebView2Environment call.
            if (AppFiles.WebView2LoaderFolder != null)
                CoreWebView2Environment.SetLoaderDllFolderPath(AppFiles.WebView2LoaderFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Folder, "WebView2"));
            await web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                UiLocale.Get(settings.Language, "native.webViewRuntimeRequired", ("error", ex.Message)),
                "PhoneCam", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = debug;
        core.Settings.AreDefaultContextMenusEnabled = debug;
        core.Settings.AreBrowserAcceleratorKeysEnabled = debug;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping("phonecam.example", AppFiles.WebRoot, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;
        settings.Language = UiLocale.Normalize(settings.Language);
        core.Navigate($"https://phonecam.example/index.html?lang={Uri.EscapeDataString(settings.Language)}");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        pollTimer.Stop();
        StopSession();
        if (startedAdbServer)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(adb.ExePath, "kill-server") { UseShellExecute = false, CreateNoWindow = true });
                process?.WaitForExit(3000);
            }
            catch
            {
            }
        }
        base.OnFormClosing(e);
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }
        if (message == null)
            return;

        switch ((string?)message["type"])
        {
            case "ready":
                if (!pageReady)
                {
                    pageReady = true;
                    _ = OnPageReadyAsync();
                }
                break;
            case "apply":
                ApplyStreamSettings(message["settings"]);
                break;
            case "view":
                SaveViewSettings(message["settings"]);
                break;
            case "torch":
                torch = (bool?)message["on"] ?? false;
                session?.SetTorch(torch);
                break;
            case "keyframe":
                session?.RequestKeyFrame();
                break;
            case "restoreCameras":
                RestoreCameras();
                break;
            case "language":
                string? language = (string?)message["language"];
                if (!UiLocale.IsSupported(language) || settings.Language == language)
                    break;
                settings.Language = language!;
                if (!settings.TrySave(out var saveError))
                {
                    Log("language preference save failed: " + saveError!.Message);
                    PostJson(new
                    {
                        type = "notice",
                        key = "messages.languageSaveFailed",
                        args = new { detail = saveError.Message },
                    });
                }
                break;
            case "fullscreen":
                SetFullscreen((bool?)message["on"] ?? !fullscreen);
                break;
            case "stats":
                lastStats = message.ToJsonString();
                if (session != null && ++statsCount % 10 == 0)
                    Log("stats: " + lastStats);
                break;
            case "log":
                Log("page: " + (string?)message["text"]);
                break;
        }
    }

    async Task OnPageReadyAsync()
    {
        PostJson(new { type = "settings", settings });
        if (selfTestInput != null)
        {
            await RunSelfTestAsync();
            return;
        }
        if (!File.Exists(adb.ExePath) || !File.Exists(serverJar))
        {
            SetStatus("error", "status.missingTools");
            return;
        }

        SetStatus("waiting", "status.startingAdb");
        try
        {
            if (!Adb.IsServerRunning())
            {
                await adb.StartServerAsync();
                startedAdbServer = true;
            }
        }
        catch (Exception ex)
        {
            Log("adb start-server: " + ex.Message);
        }
        pollTimer.Start();
        await PollAsync();
    }

    async Task PollAsync()
    {
        if (polling)
            return;
        polling = true;
        try
        {
            var devices = await adb.DevicesAsync();
            var ready = devices.FirstOrDefault(d => d.State == "device" && d.Serial == serial)
                ?? devices.FirstOrDefault(d => d.State == "device");
            if (ready == null)
            {
                if (serial != null)
                {
                    StopSession();
                    serial = null;
                    camerasSerial = null;
                    PostJson(new { type = "device", model = (string?)null });
                }
                if (devices.Any(d => d.State == "unauthorized"))
                    SetStatus("unauthorized", "status.waitingForPermission");
                else if (devices.Count > 0)
                    SetStatus("waiting", "status.deviceNotReady");
                else
                    SetStatus("waiting", "status.connectPhone");
                return;
            }

            if (ready.Serial != serial)
            {
                StopSession();
                serial = ready.Serial;
                camerasSerial = null;
                lastWorkingCameraId = null;
                suspectCameraId = null;
                camerasWithFrames.Clear();
                cameraStrikes.Clear();
                failures = 0;
                nextStartAttempt = DateTime.MinValue;
                PostJson(new { type = "device", model = ready.Model });
            }
            if (session == null && DateTime.UtcNow >= nextStartAttempt)
                await StartSessionAsync(ready.Serial);
        }
        catch (Exception ex)
        {
            Log("poll: " + ex.Message);
        }
        finally
        {
            polling = false;
        }
    }

    async Task StartSessionAsync(string deviceSerial)
    {
        CameraSession? starting = null;
        try
        {
            SetStatus("starting", "status.preparingPhone");
            await CameraSession.PushServerAsync(adb, deviceSerial, serverJar);
            if (camerasSerial != deviceSerial)
            {
                cameras = await CameraSession.ListCamerasAsync(adb, deviceSerial);
                camerasSerial = deviceSerial;
                Log("cameras: " + JsonSerializer.Serialize(cameras.Select(c => new { c.Id, c.Facing, c.Sizes.Length })));
                PostCameras();
            }

            var visible = VisibleCameras();
            if (visible.Count == 0)
                throw new InvalidOperationException("errors.noUsableLens");
            var camera = visible.FirstOrDefault(c => c.Id == settings.CameraId) ?? visible[0];
            settings.CameraId = camera.Id;
            settings.Size = camera.PickSize(settings.Size);
            PostJson(new { type = "settings", settings });
            sessionCameraId = camera.Id;
            sessionSize = settings.Size;
            sessionHasFrames = false;

            SetStatus("starting", "status.startingCamera");
            var s = new CameraSession(adb, deviceSerial);
            starting = s;
            s.SessionStarted += (width, height) => Ui(() =>
            {
                if (session != s)
                    return;
                PostJson(new { type = "session", width, height });
                SetStatus("starting", "status.waitingForFrames");
                _ = WatchFirstFrameAsync(s);
            });
            // Only real frames prove that a lens works: the session header comes before the camera is configured.
            bool frameSeen = false;
            s.PacketReceived += packet =>
            {
                if (!frameSeen)
                {
                    frameSeen = true;
                    Ui(() =>
                    {
                        if (session == s)
                            OnFirstFrame(camera.Id);
                    });
                }
                OnPacket(s, packet);
            };
            s.ServerLine += line => Ui(() => OnServerLine(line));
            s.Failed += reason => Ui(() => OnSessionFailed(s, reason));
            droppingUntilKeyFrame = false;
            session = s;
            await s.StartAsync(new StreamOptions(camera.Id, settings.Size, settings.Fps, settings.BitRate, torch));
        }
        catch (Exception ex)
        {
            // A session replaced or stopped on purpose while starting is not a failure.
            if (starting != null && session != starting)
                return;
            Log("start: " + ex);
            StopSession();
            HandleFailure(ex.Message);
        }
    }

    List<CameraInfo> VisibleCameras() =>
        cameras.Where(c => !settings.UnusableCameras.Contains($"{serial}/{c.Id}")).ToList();

    void PostCameras() => PostJson(new
    {
        type = "cameras",
        items = VisibleCameras(),
        hidden = cameras.Count(c => settings.UnusableCameras.Contains($"{serial}/{c.Id}")),
    });

    async Task WatchFirstFrameAsync(CameraSession s)
    {
        // Some lenses open a session and then never deliver a frame (and may not even disconnect).
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (session != s || sessionHasFrames)
            return;
        Log("stream: nessun fotogramma in 8 s");
        StopSession();
        HandleFailure("errors.noCameraFrame");
    }

    void OnFirstFrame(string cameraId)
    {
        sessionHasFrames = true;
        failures = 0;
        lastWorkingCameraId = cameraId;
        camerasWithFrames.Add(cameraId);
        cameraStrikes.Remove(cameraId);
        SetStatus("streaming", "status.live");
        if (suspectCameraId != null && suspectCameraId != cameraId)
        {
            // Another lens works right away, so the suspect really is refused by the phone.
            ExcludeCamera(suspectCameraId);
            suspectCameraId = null;
        }
    }

    static bool IsCameraBusy(string reason) =>
        reason.Contains("CAMERA_IN_USE") || reason.Contains("Higher-priority client")
        || reason.Contains("MAX_CAMERAS_IN_USE") || reason.Contains("CAMERA_DISABLED");

    void HandleFailure(string reason)
    {
        // Failures after the image has started, or caused by another app holding the camera, are not the lens's fault.
        if (sessionHasFrames || sessionCameraId == null || serial == null || IsCameraBusy(reason))
        {
            ScheduleRetry(reason);
            return;
        }

        string cameraId = sessionCameraId;
        int strikes = cameraStrikes[cameraId] = cameraStrikes.GetValueOrDefault(cameraId) + 1;
        if (strikes == 1)
        {
            // Some lenses refuse only particular sizes: retry once, at the default size if another one was asked.
            string? defaultSize = cameras.FirstOrDefault(c => c.Id == cameraId)?.PickSize(null);
            if (defaultSize != null && sessionSize != defaultSize)
            {
                Notice("messages.lensTryingDefaultSize", new { cameraId, size = sessionSize, defaultSize });
                settings.Size = defaultSize;
                settings.Save();
                PostJson(new { type = "settings", settings });
            }
            RestartSoon();
            return;
        }

        if (suspectCameraId != null && suspectCameraId != cameraId)
        {
            // A second lens fails too: the lenses are not the problem (phone locked, cable…).
            // Go back to the original choice and keep retrying without excluding anything.
            SwitchCamera(suspectCameraId);
            suspectCameraId = null;
            cameraStrikes.Clear();
            ScheduleRetry(reason);
            return;
        }

        var others = VisibleCameras().Where(c => c.Id != cameraId).ToList();
        var fallback = others.FirstOrDefault(c => c.Id == lastWorkingCameraId) ?? others.FirstOrDefault();
        if (fallback == null || camerasWithFrames.Contains(cameraId))
        {
            cameraStrikes.Clear();
            ScheduleRetry(reason);
            return;
        }

        if (camerasWithFrames.Contains(fallback.Id))
        {
            ExcludeCamera(cameraId);
        }
        else
        {
            // Nothing is known to work yet: exclude this lens only if the next one delivers frames.
            suspectCameraId = cameraId;
            Notice("messages.lensTryingFallback", new { cameraId, fallbackId = fallback.Id });
        }
        SwitchCamera(fallback.Id);
        RestartSoon();
    }

    void SwitchCamera(string cameraId)
    {
        settings.CameraId = cameraId;
        settings.Size = cameras.FirstOrDefault(c => c.Id == cameraId)?.PickSize(settings.Size) ?? settings.Size;
        settings.Save();
        PostJson(new { type = "settings", settings });
    }

    void ExcludeCamera(string cameraId)
    {
        // On the realme GT Neo 3, id 4 never streams: the phone keeps it for its own camera app.
        settings.UnusableCameras.Add($"{serial}/{cameraId}");
        settings.Save();
        cameraStrikes.Remove(cameraId);
        PostCameras();
        Notice("messages.lensExcluded", new { cameraId });
    }

    void RestoreCameras()
    {
        if (serial == null)
            return;
        settings.UnusableCameras.RemoveAll(entry => entry.StartsWith(serial + "/", StringComparison.Ordinal));
        settings.Save();
        cameraStrikes.Clear();
        suspectCameraId = null;
        PostCameras();
        PostJson(new { type = "settings", settings });
    }

    void RestartSoon()
    {
        failures = 0;
        nextStartAttempt = DateTime.MinValue;
        SetStatus("starting", "status.restartingCamera");
    }

    void Notice(string key, object? args = null)
    {
        Log($"notice: {key} {JsonSerializer.Serialize(args, JsonOptions)}");
        PostJson(new { type = "notice", key, args });
    }

    void OnPacket(CameraSession s, VideoPacket packet)
    {
        // Runs on the socket thread. If the page falls behind, skip to the next keyframe instead of piling up delay.
        if (droppingUntilKeyFrame)
        {
            if (!packet.KeyFrame)
                return;
            droppingUntilKeyFrame = false;
        }
        if (Volatile.Read(ref pendingPackets) > 30)
        {
            droppingUntilKeyFrame = true;
            s.RequestKeyFrame();
            return;
        }
        Interlocked.Increment(ref pendingPackets);
        string message = VideoMessage(packet);
        Ui(() =>
        {
            Interlocked.Decrement(ref pendingPackets);
            if (session == s)
                web.CoreWebView2?.PostWebMessageAsString(message);
        });
    }

    static string VideoMessage(VideoPacket packet) =>
        $"V{(packet.KeyFrame ? 1 : 0) | (packet.HasConfig ? 2 : 0)},{packet.Pts},{Convert.ToBase64String(packet.Data)}";

    void OnServerLine(string line)
    {
        Log("server: " + line);
    }

    void OnSessionFailed(CameraSession s, string reason)
    {
        if (session != s)
            return;
        session = null;
        PostJson(new { type = "streamEnded" });
        Log("stream: " + reason);
        HandleFailure(reason);
    }

    void ScheduleRetry(string reason)
    {
        failures++;
        // Most causes are fixed by the user within seconds (closing the camera app, unlocking the phone).
        var delay = TimeSpan.FromSeconds(Math.Min(10, 3 * Math.Pow(2, failures - 1)));
        nextStartAttempt = DateTime.UtcNow + delay;
        var error = CameraSession.FriendlyError(reason);
        SetStatus("error", "status.retry", new
        {
            error = new { error.Key, error.Detail },
            seconds = delay.TotalSeconds,
        });
    }

    void StopSession()
    {
        var s = session;
        session = null;
        if (s == null)
            return;
        s.Dispose();
        PostJson(new { type = "streamEnded" });
    }

    void ApplyStreamSettings(JsonNode? node)
    {
        if (node == null)
            return;
        var cameraId = (string?)node["cameraId"];
        settings.CameraId = cameraId ?? settings.CameraId;
        settings.Size = (string?)node["size"] ?? settings.Size;
        settings.Fps = (int?)node["fps"] ?? settings.Fps;
        settings.BitRate = (int?)node["bitRate"] ?? settings.BitRate;
        settings.Save();
        StopSession();
        failures = 0;
        nextStartAttempt = DateTime.MinValue;
        _ = PollAsync();
    }

    void SaveViewSettings(JsonNode? node)
    {
        if (node == null)
            return;
        settings.Rotation = (int?)node["rotation"] ?? settings.Rotation;
        settings.Mirror = (bool?)node["mirror"] ?? settings.Mirror;
        settings.Fit = (string?)node["fit"] ?? settings.Fit;
        settings.PanelCollapsed = (bool?)node["panelCollapsed"] ?? settings.PanelCollapsed;
        settings.PanelWidth = Math.Clamp((int?)node["panelWidth"] ?? settings.PanelWidth, 340, 600);
        settings.Save();
    }

    void SetFullscreen(bool on)
    {
        if (on == fullscreen)
            return;
        fullscreen = on;
        if (on)
        {
            windowStateBeforeFullscreen = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            // Going through Normal makes the maximized borderless window cover the taskbar too.
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = windowStateBeforeFullscreen == FormWindowState.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }
        PostJson(new { type = "fullscreen", on });
    }

    void SetStatus(string state, string key, object? args = null)
    {
        string value = key + JsonSerializer.Serialize(args, JsonOptions);
        if (value != lastStatusText)
            Log($"status {state}: {value}");
        lastStatusText = value;
        PostJson(new { type = "status", state, key, args });
    }

    void PostJson(object message)
    {
        if (IsDisposed || web.IsDisposed)
            return;
        web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    void Ui(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window is closing.
        }
    }

    void Log(string text)
    {
        lock (logLock)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var file = new FileInfo(logPath);
                if (file.Exists && file.Length > 1_000_000)
                    File.Move(logPath, logPath + ".old", true);
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }

    /// <summary>Plays an H.264 file through the page decoder and writes what the page reports.</summary>
    async Task RunSelfTestAsync()
    {
        var packets = DemoStream.ReadAnnexB(selfTestInput!);
        PostJson(new { type = "device", key = "device.testFile" });
        PostJson(new { type = "cameras", items = new[] { new CameraInfo("0", "back", 1920, 1080, [30], 1, 1, ["1920x1080"]) } });
        SetStatus("streaming", "status.decoderTest");
        PostJson(new { type = "session", width = 1920, height = 1080 });

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < packets.Count; i++)
        {
            web.CoreWebView2.PostWebMessageAsString(VideoMessage(packets[i]));
            var wait = TimeSpan.FromSeconds((i + 1) / 30.0) - clock.Elapsed;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);
        }
        await Task.Delay(3000);
        await File.WriteAllTextAsync(selfTestOutput!,
            $"packets={packets.Count} keyframes={packets.Count(p => p.KeyFrame)} withConfig={packets.Count(p => p.HasConfig)}{Environment.NewLine}" +
            $"stats={lastStats}{Environment.NewLine}");
        Close();
    }
}
