using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VowenLite;

// Records the mic and the system output (WASAPI loopback) as two 16 kHz mono 16-bit WAVs,
// streamed straight to disk. Both tracks share one clock and silence is padded in, so the
// files line up even though loopback delivers nothing while nothing is playing.
sealed class Recorder : IDisposable
{
    public const int Rate = 16000;
    readonly Track mic, system;
    readonly MMDeviceEnumerator enumerator = new();
    readonly DeviceWatcher watcher;
    readonly Stopwatch clock = new();

    public event Action<string> Problem;
    public TimeSpan Elapsed => clock.Elapsed;

    public Recorder(string folder, string micDeviceId)
    {
        mic = new Track("mic", Path.Combine(folder, "mic.wav"), false, micDeviceId, clock, enumerator);
        system = new Track("system", Path.Combine(folder, "system.wav"), true, "", clock, enumerator);
        mic.Problem += s => Problem?.Invoke(s);
        system.Problem += s => Problem?.Invoke(s);
        watcher = new DeviceWatcher(this);
    }

    public void Start()
    {
        clock.Start();
        mic.Start();
        system.Start();
        enumerator.RegisterEndpointNotificationCallback(watcher);
    }

    public void Stop()
    {
        try { enumerator.UnregisterEndpointNotificationCallback(watcher); } catch { }
        mic.Stop();
        system.Stop();
        clock.Stop();
    }

    public void Dispose()
    {
        Stop();
        enumerator.Dispose();
    }

    public static List<(string Id, string Name)> Mics()
    {
        var list = new List<(string, string)>();
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            list.Add((d.ID, d.FriendlyName));
            d.Dispose();
        }
        return list;
    }

    // Default output or default mic changed (e.g. Bluetooth headset connected): re-attach.
    sealed class DeviceWatcher : IMMNotificationClient
    {
        readonly Recorder r;
        public DeviceWatcher(Recorder r) { this.r = r; }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id)
        {
            if (role != Role.Multimedia) return;
            if (flow == DataFlow.Render) r.system.RestartSoon(0);
            else if (flow == DataFlow.Capture && r.mic.UsesDefault) r.mic.RestartSoon(0);
        }
        public void OnDeviceStateChanged(string id, DeviceState state) { }
        public void OnDeviceAdded(string id) { }
        public void OnDeviceRemoved(string id) { }
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
    }
}

sealed class Track
{
    readonly string name;
    readonly bool loopback;
    string deviceId;
    readonly Stopwatch clock;
    readonly MMDeviceEnumerator enumerator;
    readonly WaveFileWriter writer;
    readonly object gate = new();

    IWaveIn capture;
    BufferedWaveProvider buffered;
    ISampleProvider resampled;
    long written;            // output samples written so far
    long lastFlushMs;
    bool running;
    float[] fbuf = new float[Recorder.Rate];
    byte[] bbuf = new byte[Recorder.Rate * 2];

    public volatile float Level;   // RMS of the latest chunk, for the dictation overlay
    public event Action<string> Problem;
    public bool UsesDefault => string.IsNullOrEmpty(deviceId);

    public Track(string name, string path, bool loopback, string deviceId, Stopwatch clock, MMDeviceEnumerator enumerator)
    {
        this.name = name;
        this.loopback = loopback;
        this.deviceId = deviceId;
        this.clock = clock;
        this.enumerator = enumerator;
        writer = new WaveFileWriter(path, new WaveFormat(Recorder.Rate, 16, 1));
    }

    public void Start()
    {
        lock (gate)
        {
            running = true;
            Attach();
        }
    }

    public void Stop()
    {
        IWaveIn old;
        lock (gate)
        {
            if (!running) return;
            running = false;
            old = capture;
            capture = null;
        }
        Detach(old);
        lock (gate)
        {
            Drain();
            PadTo((long)(clock.Elapsed.TotalSeconds * Recorder.Rate));
            writer.Dispose();
        }
    }

    public void RestartSoon(int delayMs)
    {
        Task.Run(async () =>
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            IWaveIn old;
            lock (gate)
            {
                if (!running) return;
                old = capture;
                capture = null;
            }
            Detach(old);
            lock (gate)
            {
                if (!running) return;
                Drain();
                try { Attach(); }
                catch (Exception ex) { Problem?.Invoke(name + ": " + ex.Message); RestartSoon(2000); }
            }
        });
    }

    // Caller holds the lock.
    void Attach()
    {
        MMDevice dev = null;
        if (loopback) dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        else if (!UsesDefault)
        {
            try { dev = enumerator.GetDevice(deviceId); if (dev.State != DeviceState.Active) dev = null; } catch { dev = null; }
            if (dev == null) { Problem?.Invoke("Picked mic is gone, using the default mic"); deviceId = ""; }
        }
        dev ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

        var c = loopback ? new WasapiLoopbackCapture(dev) : (IWaveIn)new WasapiCapture(dev, true, 100);
        buffered = new BufferedWaveProvider(c.WaveFormat) { ReadFully = false, BufferDuration = TimeSpan.FromSeconds(10), DiscardOnBufferOverflow = true };
        ISampleProvider sp = buffered.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1) sp = new Downmix(sp);
        resampled = sp.WaveFormat.SampleRate == Recorder.Rate ? sp : new WdlResamplingSampleProvider(sp, Recorder.Rate);

        c.DataAvailable += (s, e) => OnData(c, e);
        c.RecordingStopped += (s, e) =>
        {
            if (e.Exception != null && ReferenceEquals(capture, c))
            {
                Problem?.Invoke(name + " lost its device, re-attaching");
                RestartSoon(1000);
            }
        };
        capture = c;
        c.StartRecording();
    }

    static void Detach(IWaveIn c)
    {
        if (c == null) return;
        try { c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
    }

    void OnData(IWaveIn source, WaveInEventArgs e)
    {
        lock (gate)
        {
            if (!running || !ReferenceEquals(source, capture) || e.BytesRecorded == 0) return;
            // Fill any gap (loopback is silent-by-absence) before this chunk's audio.
            var fmt = buffered.WaveFormat;
            long incoming = (long)e.BytesRecorded / fmt.BlockAlign * Recorder.Rate / fmt.SampleRate;
            long expected = (long)(clock.Elapsed.TotalSeconds * Recorder.Rate);
            if (expected - written - incoming > Recorder.Rate / 5) PadTo(expected - incoming);

            buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Drain();

            long now = clock.ElapsedMilliseconds;
            if (now - lastFlushMs > 5000) { writer.Flush(); lastFlushMs = now; } // keeps the header valid if killed
        }
    }

    void Drain()
    {
        if (resampled == null) return;
        int n;
        while ((n = resampled.Read(fbuf, 0, fbuf.Length)) > 0)
        {
            double sq = 0;
            for (int i = 0; i < n; i++)
            {
                float f = Math.Clamp(fbuf[i], -1f, 1f);
                sq += f * f;
                short v = (short)(f * 32767);
                bbuf[2 * i] = (byte)v;
                bbuf[2 * i + 1] = (byte)(v >> 8);
            }
            Level = (float)Math.Sqrt(sq / n);
            writer.Write(bbuf, 0, n * 2);
            written += n;
        }
    }

    void PadTo(long target)
    {
        while (written < target)
        {
            int n = (int)Math.Min(target - written, bbuf.Length / 2);
            Array.Clear(bbuf, 0, n * 2);
            writer.Write(bbuf, 0, n * 2);
            written += n;
        }
    }
}

// Averages N channels into one.
sealed class Downmix : ISampleProvider
{
    readonly ISampleProvider src;
    readonly int ch;
    float[] buf = Array.Empty<float>();
    public WaveFormat WaveFormat { get; }

    public Downmix(ISampleProvider src)
    {
        this.src = src;
        ch = src.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int need = count * ch;
        if (buf.Length < need) buf = new float[need];
        int got = src.Read(buf, 0, need);
        int frames = got / ch;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++) sum += buf[f * ch + c];
            buffer[offset + f] = sum / ch;
        }
        return frames;
    }
}
