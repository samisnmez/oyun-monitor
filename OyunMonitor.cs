// Oyun Monitörü: oyun oynarken GPU/CPU sıcaklık ve kullanımını, FPS, %1 low ve
// gecikmeyi küçük, her zaman üstte duran bir pencerede gösterir.
//
// Kaynak tüketimini düşük tutmak için:
//  - Ekran saniyede bir yenilenir; çizim yalnız değer değişince yapılır.
//  - GPU verisi doğrudan NVIDIA sürücüsünün nvml.dll'inden okunur (ek program yok).
//  - CPU kullanımı Windows'un GetSystemTimes çağrısıyla hesaplanır.
//  - FPS ve gecikme, Intel PresentMon'un ETW çıktısından okunur (bin\PresentMon.exe).
//  - CPU sıcaklığı PawnIO sürücüsüyle işlemcinin kendi sensöründen okunur (bin\IntelMSR.bin).
//
// Derleme: build.cmd (Windows'taki .NET Framework 4.8 derleyicisi, csc.exe).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // --onizleme <dosya.png>: örnek değerlerle pencere resmini kaydeder (tasarım kontrolü).
        if (args.Length == 2 && args[0] == "--onizleme")
        {
            using (var f = new MonitorForm(true))
            using (var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.ClientSize));
                bmp.Save(args[1]);
            }
            return;
        }
        bool yeni;
        using (var tekKopya = new Mutex(true, "OyunMonitor_TekKopya", out yeni))
        {
            if (!yeni) return; // Zaten açık.
            Application.EnableVisualStyles();
            Application.Run(new MonitorForm());
        }
    }
}

// ---------------------------------------------------------------- GPU (NVML)
static class Nvml
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization { public uint Gpu; public uint Memory; }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] static extern int Init();
    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")] static extern int Shutdown();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    static extern int GetHandle(uint index, out IntPtr device);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    static extern int GetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    static extern int GetUtilization(IntPtr device, out Utilization util);

    static IntPtr device = IntPtr.Zero;

    public static bool Start()
    {
        try { return Init() == 0 && GetHandle(0, out device) == 0; }
        catch (DllNotFoundException) { return false; }
    }

    public static void Stop() { if (device != IntPtr.Zero) { try { Shutdown(); } catch { } } }

    public static int Temperature()
    {
        uint t;
        return device != IntPtr.Zero && GetTemperature(device, 0, out t) == 0 ? (int)t : -1;
    }

    public static int Usage()
    {
        Utilization u;
        return device != IntPtr.Zero && GetUtilization(device, out u) == 0 ? (int)u.Gpu : -1;
    }
}

// ---------------------------------------------------------------- CPU kullanımı
class CpuUsage
{
    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    long lastIdle, lastTotal;

    public CpuUsage() { Sample(); }

    // Son çağrıdan bu yana ortalama kullanım (%). Çekirdek süresi boşta süresini de içerir.
    public int Sample()
    {
        long idle, kernel, user;
        if (!GetSystemTimes(out idle, out kernel, out user)) return -1;
        long total = kernel + user;
        long dTotal = total - lastTotal, dIdle = idle - lastIdle;
        lastIdle = idle; lastTotal = total;
        if (dTotal <= 0) return 0;
        return (int)Math.Round(100.0 * (dTotal - dIdle) / dTotal);
    }
}

// ---------------------------------------------------------------- FPS ve gecikme (PresentMon)
class FrameStats
{
    struct Frame { public long At; public double Ms; }
    struct Sample { public long At; public double Ms; }

    readonly object kilit = new object();
    readonly Queue<Frame> frames = new Queue<Frame>();      // son 10 sn, ön plandaki oyun
    readonly Queue<Sample> latency = new Queue<Sample>();   // son 2 sn
    string latencyKind = "";
    long latencyKindAt;
    Process proc;
    volatile int targetPid;
    public volatile string Error;

    // Sütun sırası başlık satırından okunur; PresentMon sürümleri arasında değişebilir.
    int colPid = -1, colFrame = -1, colPcLat = -1, colInputLat = -1, colClickLat = -1, colDisplayed = -1;

    public int TargetPid { set { targetPid = value; } }

    public void Start(string exe)
    {
        if (!File.Exists(exe)) { Error = "PresentMon yok"; return; }
        var psi = new ProcessStartInfo(exe,
            "--output_stdout --no_console_stats --stop_existing_session " +
            "--session_name OyunMonitor --track_pc_latency --exclude dwm.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            proc = Process.Start(psi);
            Job.Attach(proc); // Monitör kapanınca (çökse bile) PresentMon da kapanır.
            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) { Error = "PresentMon başlamadı: " + e.Message; return; }

        proc.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null && e.Data.StartsWith("error")) Error = e.Data;
        };
        proc.BeginErrorReadLine();
        var t = new Thread(ReadLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    public void Stop()
    {
        try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
    }

    void ReadLoop()
    {
        var reader = proc.StandardOutput;
        string line;
        bool header = false;
        while ((line = reader.ReadLine()) != null)
        {
            var c = line.Split(',');
            if (!header)
            {
                if (Array.IndexOf(c, "Application") < 0) continue;
                colPid = Array.IndexOf(c, "ProcessID");
                colFrame = Array.IndexOf(c, "MsBetweenAppStart");
                if (colFrame < 0) colFrame = Array.IndexOf(c, "MsBetweenPresents");
                colPcLat = Array.IndexOf(c, "MsPCLatency");
                colInputLat = Array.IndexOf(c, "MsAllInputToPhotonLatency");
                colClickLat = Array.IndexOf(c, "MsClickToPhotonLatency");
                colDisplayed = Array.IndexOf(c, "MsUntilDisplayed");
                header = true;
                continue;
            }
            int pid;
            if (colPid < 0 || colPid >= c.Length || !int.TryParse(c[colPid], out pid) || pid != targetPid) continue;

            long now = Stopwatch.GetTimestamp();
            double ms;
            lock (kilit)
            {
                if (Num(c, colFrame, out ms) && ms > 0 && ms < 1000)
                    frames.Enqueue(new Frame { At = now, Ms = ms });

                // Gecikme kaynağı, öncelik sırasıyla:
                //  PC  : NVIDIA Reflex'li oyunlarda gerçek sistem gecikmesi
                //  giriş: fare/klavye girişinden ekrana (girişin olduğu karelerde)
                //  render: karenin işlenmeye başlamasından ekrana çıkışına
                if (Num(c, colPcLat, out ms)) AddLatency(now, ms, "PC");
                else if (Num(c, colInputLat, out ms) || Num(c, colClickLat, out ms)) AddLatency(now, ms, "giriş");
                else if (Num(c, colDisplayed, out ms) && Stopwatch.GetTimestamp() - latencyKindAt > Stopwatch.Frequency * 2)
                    AddLatency(now, ms, "render");
            }
        }
    }

    // Daha iyi bir kaynak varken (son 2 sn içinde) render tahmini karışmaz.
    void AddLatency(long now, double ms, string kind)
    {
        if (ms <= 0 || ms > 1000) return;
        if (kind != "render") latencyKindAt = now;
        if (kind != latencyKind) { latency.Clear(); latencyKind = kind; }
        latency.Enqueue(new Sample { At = now, Ms = ms });
    }

    static bool Num(string[] c, int i, out double v)
    {
        v = 0;
        return i >= 0 && i < c.Length && double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    // FPS: son 1 sn; %1 low: son 30 sn'deki kare sürelerinin 99. yüzdeliğinden (tek bir
    // takılma değeri dibe çekmesin diye en yavaş %1'in ortalaması yerine).
    public void Read(out double fps, out double low1, out double lat, out string latKind)
    {
        long now = Stopwatch.GetTimestamp(), f = Stopwatch.Frequency;
        fps = low1 = lat = -1; latKind = "";
        lock (kilit)
        {
            while (frames.Count > 0 && now - frames.Peek().At > f * 30) frames.Dequeue();
            while (latency.Count > 0 && now - latency.Peek().At > f * 2) latency.Dequeue();

            double sum = 0; int n = 0;
            var all = new List<double>(frames.Count);
            foreach (var fr in frames)
            {
                all.Add(fr.Ms);
                if (now - fr.At <= f) { sum += fr.Ms; n++; }
            }
            if (n > 0) fps = 1000.0 * n / sum;
            if (all.Count >= 100)
            {
                all.Sort();
                low1 = 1000.0 / all[(int)(all.Count * 0.99)];
            }
            if (latency.Count > 0)
            {
                var l = new List<double>(latency.Count);
                foreach (var s in latency) l.Add(s.Ms);
                l.Sort();
                lat = l[l.Count / 2];
                latKind = latencyKind;
            }
        }
    }
}

// Alt süreci bir Windows iş nesnesine bağlar; monitör kapanınca alt süreç de kapanır.
static class Job
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr a, string name);
    [DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, int size);
    [DllImport("kernel32.dll")]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong R, W, O, RB, WB, OB; }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    static IntPtr job;

    public static void Attach(Process p)
    {
        if (job == IntPtr.Zero)
        {
            job = CreateJobObject(IntPtr.Zero, null);
            var info = new ExtendedLimit();
            info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(ExtendedLimit)));
        }
        AssignProcessToJobObject(job, p.Handle);
    }
}

// ---------------------------------------------------------------- CPU sıcaklığı (PawnIO)
// Intel işlemcinin paket sıcaklığı MSR'den okunur: sıcaklık = TjMax - okunan fark.
// PawnIO sürücüsü ve bin\IntelMSR.bin modülü gerekir; yoksa satır "—" kalır.
static class CpuTemp
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const uint DeviceType = 41394u << 16;
    const uint LoadBinary = DeviceType | (0x821 << 2);
    const uint Execute = DeviceType | (0x841 << 2);
    const uint PackageThermStatus = 0x1B1, TemperatureTarget = 0x1A2;

    static IntPtr handle = IntPtr.Zero;
    static int tjMax;
    static readonly byte[] input = new byte[32 + 8];
    static readonly byte[] output = new byte[8];

    public static bool Start(string module)
    {
        if (!File.Exists(module)) return false;
        IntPtr h = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
        var bin = File.ReadAllBytes(module);
        int read;
        if (!DeviceIoControl(h, LoadBinary, bin, bin.Length, null, 0, out read, IntPtr.Zero)) { CloseHandle(h); return false; }
        handle = h;
        Encoding_Ascii("ioctl_read_msr", input);
        ulong target;
        if (!ReadMsr(TemperatureTarget, out target)) { Stop(); return false; }
        tjMax = (int)((target >> 16) & 0xFF);
        if (tjMax == 0) tjMax = 100;
        return true;
    }

    static void Encoding_Ascii(string s, byte[] dest)
    {
        for (int i = 0; i < s.Length && i < 31; i++) dest[i] = (byte)s[i];
    }

    static bool ReadMsr(uint msr, out ulong value)
    {
        value = 0;
        BitConverter.GetBytes((ulong)msr).CopyTo(input, 32);
        int read;
        if (!DeviceIoControl(handle, Execute, input, input.Length, output, output.Length, out read, IntPtr.Zero) || read < 8) return false;
        value = BitConverter.ToUInt64(output, 0);
        return true;
    }

    public static int Read()
    {
        ulong status;
        if (handle == IntPtr.Zero || !ReadMsr(PackageThermStatus, out status)) return -1;
        return tjMax - (int)((status >> 16) & 0x7F);
    }

    public static int TjMax { get { return tjMax; } }

    public static void Stop()
    {
        if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }
    }
}

// ---------------------------------------------------------------- Pencere
// Düzen (kullanıcının çizimi): solda GPU göstergesi (sol yay sıcaklık, sağ yay kullanım,
// ortada %1 low), ortada FPS, sağda aynı boyutta CPU göstergesi (sol yay sıcaklık, sağ yay
// kullanım, ortada gecikme). Yaylar alttan yukarı dolar, dolulukla yeşilden kırmızıya döner.
class MonitorForm : Form
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static readonly Color Zemin = Color.FromArgb(16, 16, 19);
    static readonly Color Ray = Color.FromArgb(40, 40, 46);
    static readonly Color Soluk = Color.FromArgb(125, 125, 135);
    static readonly Color Beyaz = Color.FromArgb(236, 236, 240);
    // Bar renk geçişi: yeşil → sarı → kırmızı.
    static readonly Color Yesil = Color.FromArgb(60, 200, 90);
    static readonly Color Sari = Color.FromArgb(240, 195, 50);
    static readonly Color Kirmizi = Color.FromArgb(220, 30, 30);

    const int Yaricap = 80;
    const float Kalinlik = 15f;
    const float Yay = 110f; // her yayın açısı (derece)

    readonly SolidBrush fSoluk = new SolidBrush(Soluk), fBeyaz = new SolidBrush(Beyaz);
    readonly Pen zeminKalem = new Pen(Ray, Kalinlik);
    readonly Font etiketFont = new Font("Bahnschrift", 8.5f);
    readonly Font degerFont = new Font("Bahnschrift SemiBold", 12f);
    readonly Font ortaFont = new Font("Bahnschrift SemiBold", 22f);
    readonly Font fpsFont = new Font("Bahnschrift SemiBold", 34f);
    readonly StringFormat ortala = new StringFormat { Alignment = StringAlignment.Center };
    readonly StringFormat sagaYasla = new StringFormat { Alignment = StringAlignment.Far };

    readonly string klasor = AppDomain.CurrentDomain.BaseDirectory;
    readonly CpuUsage cpu = new CpuUsage();
    readonly FrameStats frames = new FrameStats();
    // Saniyede 4 ölçüm; barlar ölçümlerin yumuşatılmış ortalamasını gösterir.
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
    // Yumuşatma katsayısı: her ölçümde yeni değerin payı. Büyürse hızlı tepki, küçülürse sakin bar.
    // 0,35 ile ani bir değişime ~1 sn'de yetişir.
    const double Yumusatma = 0.35;
    readonly bool gpuVar, cpuSicaklikVar;
    readonly int ownPid = Process.GetCurrentProcess().Id;

    double gpuT = -1, gpuU = -1, cpuT = -1, cpuU = -1;
    double fps = -1, low1 = -1, lat = -1;
    int oyunPid;
    string sonDurum = "";

    public MonitorForm(bool onizleme = false)
    {
        Text = "Oyun Monitörü";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { } // görev çubuğu ikonu
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        BackColor = Zemin;
        DoubleBuffered = true;
        ClientSize = new Size(540, 240);
        StartPosition = FormStartPosition.Manual;
        Location = KonumYukle();

        var menu = new ContextMenuStrip();
        var ustte = new ToolStripMenuItem("Her zaman üstte") { Checked = true };
        ustte.Click += (s, e) => { TopMost = !TopMost; ustte.Checked = TopMost; };
        menu.Items.Add(ustte);
        menu.Items.Add("Kapat", null, (s, e) => Close());
        ContextMenuStrip = menu;

        MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // başlıktan tutuyormuş gibi sürükle
        };

        if (onizleme)
        {
            gpuT = 71; gpuU = 97; cpuT = 52; cpuU = 38;
            fps = 144; low1 = 98; lat = 18.4;
            return;
        }

        gpuVar = Nvml.Start();
        cpuSicaklikVar = CpuTemp.Start(Path.Combine(klasor, "bin", "IntelMSR.bin"));
        frames.Start(Path.Combine(klasor, "bin", "PresentMon.exe"));
        timer.Tick += (s, e) => Yenile();
        timer.Start();
        Yenile();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int yuvarlak = 2; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND (Windows 11)
        try { DwmSetWindowAttribute(Handle, 33, ref yuvarlak, 4); } catch { }
    }

    void Yenile()
    {
        // Ön plandaki pencerenin süreci oyun kabul edilir; monitörün kendisine tıklanırsa son oyun korunur.
        int pid;
        GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        if (pid != 0 && pid != ownPid && pid != oyunPid)
        {
            oyunPid = pid;
            frames.TargetPid = pid;
        }

        gpuT = Yumusat(gpuT, gpuVar ? Nvml.Temperature() : -1);
        gpuU = Yumusat(gpuU, gpuVar ? Nvml.Usage() : -1);
        cpuT = Yumusat(cpuT, cpuSicaklikVar ? CpuTemp.Read() : -1);
        cpuU = Yumusat(cpuU, cpu.Sample());
        string latKind;
        frames.Read(out fps, out low1, out lat, out latKind);

        // Görünen hiçbir değer değişmediyse yeniden çizme (barlar yarım birimlik adımla karşılaştırılır).
        string durum = Math.Round(gpuT * 2) + "," + Math.Round(gpuU * 2) + "," + Math.Round(cpuT * 2) + "," +
            Math.Round(cpuU * 2) + "," + Math.Round(fps) + "," +
            Math.Round(low1) + "," + Math.Round(lat, 1) + frames.Error;
        if (durum == sonDurum) return;
        sonDurum = durum;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        string yok = frames.Error != null ? "yok" : "—";
        int tj = CpuTemp.TjMax > 0 ? CpuTemp.TjMax : 100;

        Gosterge(g, 118, 128,
            "GPU SICAKLIK", Tam(gpuT, "°"), Oran(gpuT, 0, 95), Oran(gpuT, 40, 85),
            "GPU KULLANIM", Tam(gpuU, "%"), Oran(gpuU, 0, 100),
            "%1 LOW", low1 < 0 ? yok : low1.ToString("0"));

        g.DrawString(fps < 0 ? yok : fps.ToString("0"), fpsFont, fBeyaz, new RectangleF(220, 128, 100, 50), ortala);
        g.DrawString("FPS", etiketFont, fSoluk, new RectangleF(220, 178, 100, 16), ortala);

        Gosterge(g, 422, 128,
            "CPU SICAKLIK", Tam(cpuT, "°"), Oran(cpuT, 0, tj), Oran(cpuT, 40, 85),
            "CPU KULLANIM", Tam(cpuU, "%"), Oran(cpuU, 0, 100),
            "GECİKME", lat < 0 ? yok : lat.ToString("0.0"));

        if (frames.Error != null)
            g.DrawString(frames.Error, etiketFont, fSoluk, new RectangleF(10, 220, 520, 16), sagaYasla);
    }

    // Üstel ortalama: yeni ölçüm Yumusatma oranında, eski değer kalanında etkili olur.
    static double Yumusat(double onceki, int yeni)
    {
        if (yeni < 0) return -1;
        if (onceki < 0) return yeni;
        return onceki + (yeni - onceki) * Yumusatma;
    }

    static string Tam(double v, string birim)
    {
        return v < 0 ? "—" : Math.Round(v).ToString("0") + birim;
    }

    // Değerin [alt, üst] aralığındaki yeri, 0..1 (değer yoksa -1).
    static float Oran(double v, int alt, int ust)
    {
        if (v < 0) return -1;
        return (float)Math.Max(0, Math.Min(1, (v - alt) / (ust - alt)));
    }

    // 0 → yeşil, 0,5 → sarı, 1 → kırmızı.
    static Color Renk(float t)
    {
        return t < 0.5f ? Karistir(Yesil, Sari, t * 2) : Karistir(Sari, Kirmizi, (t - 0.5f) * 2);
    }

    static Color Karistir(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    // İki yaylı gösterge: sol yay (sıcaklık) ve sağ yay (kullanım) alttan yukarı dolar.
    // Sıcaklık yayının rengi sıcaklığa (40→85 °C), kullanım yayınınki doluluğa göre değişir.
    void Gosterge(Graphics g, int cx, int cy,
        string solEtiket, string solDeger, float solDoluluk, float solRenk,
        string sagEtiket, string sagDeger, float sagDoluluk,
        string ortaEtiket, string ortaDeger)
    {
        int r = Yaricap;
        var kutu = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
        g.DrawArc(zeminKalem, kutu, 180 - Yay / 2, Yay);   // sol
        g.DrawArc(zeminKalem, kutu, -Yay / 2, Yay);        // sağ
        if (solDoluluk > 0)
            using (var p = new Pen(Renk(solRenk), Kalinlik))
                g.DrawArc(p, kutu, 180 - Yay / 2, Yay * solDoluluk);
        if (sagDoluluk > 0)
            using (var p = new Pen(Renk(sagDoluluk), Kalinlik))
                g.DrawArc(p, kutu, Yay / 2, -Yay * sagDoluluk);

        float uc = (float)(r * Math.Cos(Yay / 2 * Math.PI / 180));
        float ust = cy - (float)(r * Math.Sin(Yay / 2 * Math.PI / 180));
        float altY = cy + (float)(r * Math.Sin(Yay / 2 * Math.PI / 180));

        // Etiketler üstte, ortadan iki yana; değerler yayların alt uçlarında.
        float etiketY = ust - Kalinlik - 18;
        g.DrawString(solEtiket, etiketFont, fSoluk, new RectangleF(cx - 125, etiketY, 120, 14), sagaYasla);
        g.DrawString(sagEtiket, etiketFont, fSoluk, new RectangleF(cx + 5, etiketY, 120, 14));
        g.DrawString(solDeger, degerFont, fBeyaz, new RectangleF(cx - uc - 40, altY + Kalinlik / 2 + 2, 80, 22), ortala);
        g.DrawString(sagDeger, degerFont, fBeyaz, new RectangleF(cx + uc - 40, altY + Kalinlik / 2 + 2, 80, 22), ortala);

        g.DrawString(ortaEtiket, etiketFont, fSoluk, new RectangleF(cx - r, cy - 26, 2 * r, 14), ortala);
        g.DrawString(ortaDeger, ortaFont, fBeyaz, new RectangleF(cx - r, cy - 12, 2 * r, 40), ortala);
    }

    Point KonumYukle()
    {
        try
        {
            var p = File.ReadAllText(Path.Combine(klasor, "konum.txt")).Split(',');
            var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
            foreach (var s in Screen.AllScreens)
                if (s.WorkingArea.Contains(pt)) return pt;
        }
        catch { }
        var wa = Screen.PrimaryScreen.WorkingArea;
        return new Point(wa.Right - 560, wa.Top + 20);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { File.WriteAllText(Path.Combine(klasor, "konum.txt"), Location.X + "," + Location.Y); } catch { }
        timer.Stop();
        frames.Stop();
        CpuTemp.Stop();
        Nvml.Stop();
        base.OnFormClosing(e);
    }
}
