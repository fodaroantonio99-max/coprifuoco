using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using DR = System.Drawing;
using WF = System.Windows.Forms;

[assembly: AssemblyTitle("Coprifuoco")]
[assembly: AssemblyProduct("Coprifuoco")]
[assembly: AssemblyDescription("Spegnimento programmato del PC")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace Coprifuoco
{
    // ------------------------------------------------------------------
    // Chiamate a Windows: inattività di mouse/tastiera, audio, memoria
    // ------------------------------------------------------------------
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO p);

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static double IdleSeconds()
        {
            LASTINPUTINFO i = new LASTINPUTINFO();
            i.cbSize = (uint)Marshal.SizeOf(i);
            if (!GetLastInputInfo(ref i)) return 0;
            uint tick = unchecked((uint)Environment.TickCount);
            return unchecked(tick - i.dwTime) / 1000.0;
        }

        // Libera la memoria che non serve mentre la finestra è nascosta
        public static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }

        // Barra del titolo scura, per non avere una striscia bianca di notte
        public static void DarkTitleBar(IntPtr hwnd)
        {
            int v = 1;
            DwmSetWindowAttribute(hwnd, 20, ref v, 4);
            DwmSetWindowAttribute(hwnd, 19, ref v, 4);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioMeterInformation
        {
            int GetPeakValue(out float peak);
        }

        // Livello dell'audio in uscita sul dispositivo predefinito (0..1)
        public static float AudioPeak()
        {
            try
            {
                IMMDeviceEnumerator en = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0) return 0;
                Guid iid = typeof(IAudioMeterInformation).GUID;
                object o;
                dev.Activate(ref iid, 23, IntPtr.Zero, out o);
                float p;
                ((IAudioMeterInformation)o).GetPeakValue(out p);
                Marshal.ReleaseComObject(o);
                Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(en);
                return p;
            }
            catch { return 0; }
        }
    }

    // ------------------------------------------------------------------
    // Capisce se sta passando un video o dell'audio
    // ------------------------------------------------------------------
    class MediaSensor
    {
        object manager;
        bool smtc;
        DateTime lastAudio = DateTime.MinValue;
        DateTime lastSmtcPoll = DateTime.MinValue;
        int busy;

        public bool Smtc { get { return smtc; } }
        public bool ManagerReady { get { return manager != null; } }

        public void Reset()
        {
            smtc = false;
            lastAudio = DateTime.MinValue;
            lastSmtcPoll = DateTime.MinValue;
        }

        public void PollAudio(DateTime now)
        {
            if (Native.AudioPeak() > 0.002f) lastAudio = now;
        }

        public bool AudioRecent(DateTime now)
        {
            return (now - lastAudio).TotalSeconds < 15;
        }

        // Interroga Windows (YouTube, Netflix, Spotify, VLC...) senza bloccare l'interfaccia
        public void PollSmtcAsync(DateTime now)
        {
            if ((now - lastSmtcPoll).TotalSeconds < 5) return;
            lastSmtcPoll = now;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { smtc = QuerySmtc(); }
                finally { busy = 0; }
            });
        }

        public bool QuerySmtc()
        {
            try
            {
                if (manager == null)
                {
                    Type t = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime");
                    if (t == null) return false;
                    object op = t.GetMethod("RequestAsync").Invoke(null, null);
                    MethodInfo asTask = null;
                    foreach (MethodInfo m in typeof(System.WindowsRuntimeSystemExtensions).GetMethods())
                    {
                        if (m.Name == "AsTask" && m.GetParameters().Length == 1 &&
                            m.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1")
                        { asTask = m; break; }
                    }
                    if (asTask == null) return false;
                    object task = asTask.MakeGenericMethod(t).Invoke(null, new object[] { op });
                    ((System.Threading.Tasks.Task)task).Wait(3000);
                    manager = task.GetType().GetProperty("Result").GetValue(task, null);
                }
                dynamic mgr = manager;
                foreach (dynamic s in mgr.GetSessions())
                {
                    dynamic info = s.GetPlaybackInfo();
                    if (info.PlaybackStatus.ToString() == "Playing") return true;
                }
                return false;
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------
    // Calcoli senza interfaccia (provati da --selftest)
    // ------------------------------------------------------------------
    static class Logic
    {
        public static bool TryTimeOfDay(string s, out TimeSpan t)
        {
            DateTime d;
            t = TimeSpan.Zero;
            if (DateTime.TryParseExact((s ?? "").Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            {
                t = d.TimeOfDay;
                return true;
            }
            return false;
        }

        public static int? Minutes(string h, string m, int maxH, int maxM)
        {
            int hv, mv;
            if (!int.TryParse(h, out hv) || !int.TryParse(m, out mv)) return null;
            if (hv < 0 || mv < 0 || hv > maxH || mv > maxM) return null;
            return hv * 60 + mv;
        }

        public static DateTime? TargetTime(string mode, string h, string m, string at, DateTime now)
        {
            if (mode == "in")
            {
                int? total = Minutes(h, m, 99, 999);
                if (!total.HasValue || total.Value < 1) return null;
                return now.AddMinutes(total.Value);
            }
            TimeSpan t;
            if (!TryTimeOfDay(at, out t)) return null;
            DateTime target = now.Date.Add(t);
            if (target <= now) target = target.AddDays(1);
            return target;
        }

        public static int? IdleThresholdSeconds(string h, string m)
        {
            int? total = Minutes(h, m, 99, 999);
            if (!total.HasValue || total.Value < 1) return null;
            return total.Value * 60;
        }

        // Vero se "now" cade nella fascia da-a (anche a cavallo di mezzanotte)
        public static bool InWindow(DateTime now, TimeSpan from, TimeSpan to)
        {
            TimeSpan t = now.TimeOfDay;
            if (from == to) return true;
            if (from < to) return t >= from && t < to;
            return t >= from || t < to;
        }

        public static string FormatSpan(double sec)
        {
            if (sec < 0) sec = 0;
            return string.Format("{0:00}:{1:00}:{2:00}", (int)Math.Floor(sec / 3600), (int)Math.Floor((sec % 3600) / 60), (int)Math.Floor(sec % 60));
        }

        public static string HumanSpan(int totalMin)
        {
            int h = totalMin / 60, m = totalMin % 60;
            List<string> p = new List<string>();
            if (h > 0) p.Add(h == 1 ? "1 ora" : h + " ore");
            if (m > 0) p.Add(m + " min");
            return p.Count == 0 ? "0 min" : string.Join(" e ", p.ToArray());
        }
    }

    class ActionInfo
    {
        public string Key, Label, Hint, Verb;
        public ActionInfo(string key, string label, string hint, string verb) { Key = key; Label = label; Hint = hint; Verb = verb; }
    }

    // Colori del menu nell'area di notifica: scuro e caldo, come il resto
    class WarmColors : WF.ProfessionalColorTable
    {
        static readonly DR.Color Bg = DR.Color.FromArgb(42, 38, 34);
        static readonly DR.Color Hi = DR.Color.FromArgb(62, 56, 47);
        static readonly DR.Color Edge = DR.Color.FromArgb(90, 82, 70);
        public override DR.Color ToolStripDropDownBackground { get { return Bg; } }
        public override DR.Color ImageMarginGradientBegin { get { return Bg; } }
        public override DR.Color ImageMarginGradientMiddle { get { return Bg; } }
        public override DR.Color ImageMarginGradientEnd { get { return Bg; } }
        public override DR.Color MenuBorder { get { return Edge; } }
        public override DR.Color MenuItemBorder { get { return Hi; } }
        public override DR.Color MenuItemSelected { get { return Hi; } }
        public override DR.Color MenuItemSelectedGradientBegin { get { return Hi; } }
        public override DR.Color MenuItemSelectedGradientEnd { get { return Hi; } }
        public override DR.Color SeparatorDark { get { return Edge; } }
        public override DR.Color SeparatorLight { get { return Edge; } }
        public override DR.Color CheckBackground { get { return Hi; } }
        public override DR.Color CheckSelectedBackground { get { return Hi; } }
        public override DR.Color CheckPressedBackground { get { return Hi; } }
    }

    // ------------------------------------------------------------------
    // L'applicazione
    // ------------------------------------------------------------------
    class CoprifuocoApp
    {
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Coprifuoco");
        static readonly string LogFile = Path.Combine(DataDir, "coprifuoco.log");
        static readonly string SettingsFile = Path.Combine(DataDir, "settings.ini");
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly bool dryRun;
        readonly Application app;
        Window win, popup;
        DispatcherTimer tick, memTimer;
        WF.NotifyIcon tray;
        WF.ToolStripMenuItem menuStop, menuAuto;
        readonly Dictionary<string, RadioButton> radios = new Dictionary<string, RadioButton>();
        readonly MediaSensor sensor = new MediaSensor();
        readonly ActionInfo[] actions = new ActionInfo[]
        {
            new ActionInfo("shutdown",  "Spegni",      "arresta il PC",            "si spegne"),
            new ActionInfo("restart",   "Riavvia",     "riavvia il PC",            "si riavvia"),
            new ActionInfo("hibernate", "Iberna",      "salva su disco e spegne",  "va in ibernazione"),
            new ActionInfo("sleep",     "Sospendi",    "risparmio energetico",     "va in sospensione"),
            new ActionInfo("logoff",    "Disconnetti", "esce dall'account",        "ti disconnette"),
            new ActionInfo("lock",      "Blocca",      "blocca lo schermo",        "si blocca")
        };

        // stato del timer / controllo in corso
        string kind;
        DateTime target, start, lastMedia = DateTime.MinValue;
        string action = "shutdown";
        bool force, mediaGuard = true, useWindow, warned, popupShown, balloonShown;
        int idleThreshold;
        TimeSpan winFrom, winTo;

        public CoprifuocoApp(Application application, bool dryRun)
        {
            this.app = application;
            this.dryRun = dryRun;
        }

        // ---------- utilità ----------
        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                FileInfo fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 200000) File.Delete(LogFile);
                File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        static Stream Resource(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        }

        T F<T>(string name) where T : class
        {
            T el = win.FindName(name) as T;
            if (el == null) throw new InvalidOperationException("Elemento mancante nell'interfaccia: " + name);
            return el;
        }

        System.Windows.Media.Brush Res(string key) { return (System.Windows.Media.Brush)win.FindResource(key); }

        static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }

        // ---------- interfaccia ----------
        RadioButton modeIn, modeAt, modeIdle;
        StackPanel panelIn, panelAt, panelIdle, windowRow;
        TextBox hours, minutes, atTime, idleHours, idleMinutes, winFromBox, winToBox;
        CheckBox mediaGuardBox, autoIdleBox, useWindowBox, forceBox;
        TextBlock explain, stateLabel, countdown, tileIdle, tileMedia, tileWindow, message;
        ProgressBar bar;
        UniformGrid actionGrid, tiles;
        Button startBtn, cancelBtn;
        Button[] quick;

        public Window Build()
        {
            win = (Window)XamlReader.Load(Resource("Main.xaml"));
            modeIn = F<RadioButton>("ModeIn"); modeAt = F<RadioButton>("ModeAt"); modeIdle = F<RadioButton>("ModeIdle");
            panelIn = F<StackPanel>("PanelIn"); panelAt = F<StackPanel>("PanelAt"); panelIdle = F<StackPanel>("PanelIdle"); windowRow = F<StackPanel>("WindowRow");
            hours = F<TextBox>("Hours"); minutes = F<TextBox>("Minutes"); atTime = F<TextBox>("AtTime");
            idleHours = F<TextBox>("IdleHours"); idleMinutes = F<TextBox>("IdleMinutes");
            winFromBox = F<TextBox>("WinFrom"); winToBox = F<TextBox>("WinTo");
            mediaGuardBox = F<CheckBox>("MediaGuard"); autoIdleBox = F<CheckBox>("AutoIdle");
            useWindowBox = F<CheckBox>("UseWindow"); forceBox = F<CheckBox>("Force");
            explain = F<TextBlock>("Explain"); stateLabel = F<TextBlock>("StateLabel"); countdown = F<TextBlock>("Countdown");
            tileIdle = F<TextBlock>("TileIdle"); tileMedia = F<TextBlock>("TileMedia"); tileWindow = F<TextBlock>("TileWindow");
            message = F<TextBlock>("Message");
            bar = F<ProgressBar>("Bar");
            actionGrid = F<UniformGrid>("ActionGrid"); tiles = F<UniformGrid>("Tiles");
            startBtn = F<Button>("StartBtn"); cancelBtn = F<Button>("CancelBtn");

            foreach (ActionInfo a in actions)
            {
                RadioButton rb = new RadioButton();
                rb.Content = a.Label + "  (" + a.Hint + ")";
                rb.GroupName = "action";
                rb.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, a.Label + ": " + a.Hint);
                rb.IsChecked = a.Key == "shutdown";
                rb.Click += delegate { UpdateExplain(); };
                actionGrid.Children.Add(rb);
                radios[a.Key] = rb;
            }

            RoutedEventHandler modeChanged = delegate
            {
                windowRow.Visibility = useWindowBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                ShowMode();
            };
            foreach (ToggleButton c in new ToggleButton[] { modeIn, modeAt, modeIdle, mediaGuardBox, useWindowBox }) c.Click += modeChanged;
            foreach (TextBox t in new TextBox[] { hours, minutes, atTime, idleHours, idleMinutes, winFromBox, winToBox }) t.TextChanged += delegate { UpdateExplain(); };

            WireQuick("P15", hours, minutes, "0", "15"); WireQuick("P30", hours, minutes, "0", "30");
            WireQuick("P60", hours, minutes, "1", "0"); WireQuick("P120", hours, minutes, "2", "0");
            WireQuick("I30", idleHours, idleMinutes, "0", "30"); WireQuick("I60", idleHours, idleMinutes, "1", "0");
            WireQuick("I120", idleHours, idleMinutes, "2", "0"); WireQuick("I180", idleHours, idleMinutes, "3", "0");
            quick = new Button[] { F<Button>("P15"), F<Button>("P30"), F<Button>("P60"), F<Button>("P120"), F<Button>("I30"), F<Button>("I60"), F<Button>("I120"), F<Button>("I180") };

            startBtn.Click += delegate { StartRun(); };
            cancelBtn.Click += delegate { StopRun("Fermato. Non succederà nulla."); };

            win.SourceInitialized += delegate { Native.DarkTitleBar(new WindowInteropHelper(win).Handle); };
            win.Closing += OnWindowClosing;
            win.IsVisibleChanged += delegate
            {
                if (tick == null) return;
                if (win.IsVisible) tick.Interval = TimeSpan.FromMilliseconds(250);
                else { tick.Interval = TimeSpan.FromMilliseconds(1000); Native.TrimMemory(); }
            };
            try
            {
                IconBitmapDecoder dec = new IconBitmapDecoder(Resource("Coprifuoco.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                win.Icon = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
            }
            catch { }

            tick = new DispatcherTimer();
            tick.Interval = TimeSpan.FromMilliseconds(250);
            tick.Tick += OnTick;

            MigrateLegacyData();
            RestoreSettings();
            windowRow.Visibility = useWindowBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ShowMode();
            return win;
        }

        // Se c'erano impostazioni salvate col vecchio nome (Nanna), le recupera una volta sola
        static void MigrateLegacyData()
        {
            try
            {
                string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nanna", "settings.ini");
                if (!File.Exists(SettingsFile) && File.Exists(legacy))
                {
                    Directory.CreateDirectory(DataDir);
                    File.Copy(legacy, SettingsFile);
                }
            }
            catch { }
        }

        void WireQuick(string name, TextBox h, TextBox m, string hv, string mv)
        {
            F<Button>(name).Click += delegate { h.Text = hv; m.Text = mv; };
        }

        string Mode() { return modeIdle.IsChecked == true ? "idle" : modeAt.IsChecked == true ? "at" : "in"; }

        string SelectedAction()
        {
            foreach (KeyValuePair<string, RadioButton> kv in radios) if (kv.Value.IsChecked == true) return kv.Key;
            return "shutdown";
        }

        ActionInfo Info(string key) { return actions.First(a => a.Key == key); }

        void ShowMode()
        {
            string m = Mode();
            panelIn.Visibility = m == "in" ? Visibility.Visible : Visibility.Collapsed;
            panelAt.Visibility = m == "at" ? Visibility.Visible : Visibility.Collapsed;
            panelIdle.Visibility = m == "idle" ? Visibility.Visible : Visibility.Collapsed;
            startBtn.Content = m == "idle" ? "Attiva il controllo" : "Avvia timer";
            UpdateExplain();
        }

        void UpdateExplain()
        {
            if (explain == null) return;
            string act = Info(SelectedAction()).Label.ToLower();
            switch (Mode())
            {
                case "in":
                    {
                        DateTime now = DateTime.Now;
                        DateTime? t = Logic.TargetTime("in", hours.Text, minutes.Text, "", now);
                        explain.Text = t.HasValue
                            ? string.Format("Premi Avvia: tra {0} ({1:HH:mm}) il computer farà l'azione scelta ({2}). Fino ad allora puoi usarlo normalmente.", Logic.HumanSpan((int)Math.Round((t.Value - now).TotalMinutes)), t.Value, act)
                            : "Scrivi quante ore e minuti devono passare prima di agire.";
                        break;
                    }
                case "at":
                    {
                        DateTime now = DateTime.Now;
                        DateTime? t = Logic.TargetTime("at", "", "", atTime.Text, now);
                        explain.Text = t.HasValue
                            ? string.Format("Premi Avvia: il computer farà l'azione scelta ({0}) {1} alle {2:HH:mm}.", act, t.Value.Date == now.Date ? "oggi" : "domani", t.Value)
                            : "Scrivi un'ora valida in formato 24 ore, ad esempio 23:30.";
                        break;
                    }
                default:
                    {
                        int? thr = Logic.IdleThresholdSeconds(idleHours.Text, idleMinutes.Text);
                        if (!thr.HasValue) { explain.Text = "Scrivi dopo quanto tempo di inattività agire (almeno 1 minuto)."; break; }
                        StringBuilder sb = new StringBuilder();
                        sb.AppendFormat("Se per {0} non tocchi mouse e tastiera, il computer farà l'azione scelta ({1}).", Logic.HumanSpan(thr.Value / 60), act);
                        if (mediaGuardBox.IsChecked == true) sb.Append(" Se però sta passando un video o dell'audio (per esempio YouTube dal letto), il conto si ferma e riparte da zero: non ti spegne il film.");
                        else sb.Append(" Attenzione: anche se stai guardando un video senza toccare nulla, si spegnerà.");
                        if (useWindowBox.IsChecked == true) sb.AppendFormat(" Il controllo è attivo solo tra le {0} e le {1}.", winFromBox.Text, winToBox.Text);
                        sb.Append(" Appena muovi il mouse o premi un tasto, il conto riparte.");
                        explain.Text = sb.ToString();
                        break;
                    }
            }
        }

        void SetRunning(bool running)
        {
            startBtn.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            cancelBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            cancelBtn.Content = kind == "idle" ? "Ferma il controllo" : "Annulla";
            List<Control> locked = new List<Control> { modeIn, modeAt, modeIdle, hours, minutes, atTime, idleHours, idleMinutes, mediaGuardBox, autoIdleBox, useWindowBox, winFromBox, winToBox, forceBox };
            locked.AddRange(quick);
            locked.AddRange(radios.Values.Cast<Control>());
            foreach (Control c in locked) c.IsEnabled = !running;
        }

        void ResetDisplay()
        {
            stateLabel.Text = "Nessun timer attivo";
            countdown.Text = "--:--:--";
            countdown.Foreground = Res("Text");
            bar.Value = 0;
            tiles.Visibility = Visibility.Collapsed;
            message.Text = "";
        }

        // ---------- impostazioni ----------
        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                string[] lines = new string[]
                {
                    "action=" + SelectedAction(), "mode=" + Mode(), "hours=" + hours.Text, "minutes=" + minutes.Text, "at=" + atTime.Text,
                    "idleHours=" + idleHours.Text, "idleMinutes=" + idleMinutes.Text, "mediaGuard=" + (mediaGuardBox.IsChecked == true),
                    "autoIdle=" + (autoIdleBox.IsChecked == true), "useWindow=" + (useWindowBox.IsChecked == true),
                    "winFrom=" + winFromBox.Text, "winTo=" + winToBox.Text, "force=" + (forceBox.IsChecked == true)
                };
                File.WriteAllLines(SettingsFile, lines, Encoding.UTF8);
            }
            catch { }
        }

        Dictionary<string, string> ReadSettings()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            try
            {
                if (File.Exists(SettingsFile))
                    foreach (string line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch { }
            return d;
        }

        void RestoreSettings()
        {
            Dictionary<string, string> s = ReadSettings();
            string v;
            if (s.TryGetValue("action", out v) && radios.ContainsKey(v)) radios[v].IsChecked = true;
            if (s.TryGetValue("hours", out v)) hours.Text = v;
            if (s.TryGetValue("minutes", out v)) minutes.Text = v;
            if (s.TryGetValue("at", out v)) atTime.Text = v;
            if (s.TryGetValue("idleHours", out v)) idleHours.Text = v;
            if (s.TryGetValue("idleMinutes", out v)) idleMinutes.Text = v;
            if (s.TryGetValue("mediaGuard", out v)) mediaGuardBox.IsChecked = v == "True";
            if (s.TryGetValue("autoIdle", out v)) autoIdleBox.IsChecked = v == "True";
            if (s.TryGetValue("useWindow", out v)) useWindowBox.IsChecked = v == "True";
            if (s.TryGetValue("winFrom", out v)) winFromBox.Text = v;
            if (s.TryGetValue("winTo", out v)) winToBox.Text = v;
            if (s.TryGetValue("force", out v)) forceBox.IsChecked = v == "True";
            if (s.TryGetValue("mode", out v)) { if (v == "at") modeAt.IsChecked = true; else if (v == "idle") modeIdle.IsChecked = true; }
        }

        // ---------- avvio / stop ----------
        public void StartRun()
        {
            string mode = Mode();
            action = SelectedAction();
            force = forceBox.IsChecked == true;
            start = DateTime.Now;
            warned = false;
            popupShown = false;
            ClosePopup();

            if (mode == "idle")
            {
                int? thr = Logic.IdleThresholdSeconds(idleHours.Text, idleMinutes.Text);
                if (!thr.HasValue) { message.Text = "Inserisci almeno 1 minuto di inattività (ore 0-99, minuti 0-999)."; return; }
                useWindow = useWindowBox.IsChecked == true;
                if (useWindow)
                {
                    if (!Logic.TryTimeOfDay(winFromBox.Text, out winFrom) || !Logic.TryTimeOfDay(winToBox.Text, out winTo))
                    { message.Text = "Fascia oraria non valida. Usa il formato 24 ore, ad esempio 00:00 e 07:00."; return; }
                }
                kind = "idle";
                idleThreshold = thr.Value;
                mediaGuard = mediaGuardBox.IsChecked == true;
                lastMedia = DateTime.MinValue;
                sensor.Reset();
                Log(string.Format("Controllo inattività avviato: {0} dopo {1} min (video={2}, fascia={3})", action, thr.Value / 60, mediaGuard, useWindow));
                stateLabel.Text = string.Format("Sorveglianza attiva: {0} dopo {1} di inattività", Info(action).Label.ToLower(), Logic.HumanSpan(thr.Value / 60));
                tiles.Visibility = Visibility.Visible;
            }
            else
            {
                DateTime? t = Logic.TargetTime(mode, hours.Text, minutes.Text, atTime.Text, DateTime.Now);
                if (!t.HasValue)
                {
                    message.Text = mode == "in" ? "Inserisci almeno 1 minuto (ore 0-99, minuti 0-999)." : "Ora non valida. Usa il formato 24 ore, ad esempio 23:30.";
                    return;
                }
                kind = "timer";
                target = t.Value;
                Log(string.Format("Timer avviato: {0} alle {1:yyyy-MM-dd HH:mm:ss}", action, target));
                stateLabel.Text = string.Format("{0} alle {1:HH:mm}", Info(action).Label.ToLower(), target);
                tiles.Visibility = Visibility.Collapsed;
                message.Text = "";
            }
            SaveSettings();
            countdown.Foreground = Res("Text");
            SetRunning(true);
            OnTick(null, null);
            if (kind != null) tick.Start();
            UpdateTray();
        }

        public void StopRun(string msg)
        {
            tick.Stop();
            ClosePopup();
            Log("Timer/controllo fermato dall'utente");
            kind = null;
            SetRunning(false);
            ResetDisplay();
            if (msg != null) message.Text = msg;
            UpdateTray();
        }

        public bool Running { get { return tick != null && tick.IsEnabled; } }

        void CompleteAction()
        {
            tick.Stop();
            ClosePopup();
            message.Text = "Esecuzione in corso...";
            PowerAction(action, force);
            kind = null;
            SetRunning(false);
            ResetDisplay();
            if (dryRun) message.Text = "Prova: azione non eseguita (modalità test).";
            UpdateTray();
        }

        void PowerAction(string key, bool forceClose)
        {
            Log(string.Format("Esecuzione azione: {0} (forza={1}, prova={2})", key, forceClose, dryRun));
            if (dryRun) return;
            switch (key)
            {
                case "shutdown": Run("shutdown.exe", "/s /t 0" + (forceClose ? " /f" : "")); break;
                case "restart": Run("shutdown.exe", "/r /t 0" + (forceClose ? " /f" : "")); break;
                case "hibernate": Run("shutdown.exe", "/h"); break;
                case "sleep": WF.Application.SetSuspendState(WF.PowerState.Suspend, false, false); break;
                case "logoff": Run("shutdown.exe", "/l"); break;
                case "lock": Run("rundll32.exe", "user32.dll,LockWorkStation"); break;
            }
        }

        static void Run(string file, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
        }

        // ---------- il battito: aggiorna conto alla rovescia e controlla l'attività ----------
        void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            if (kind == "timer")
            {
                double remaining = (target - now).TotalSeconds;
                if (remaining <= 0) { countdown.Text = "00:00:00"; CompleteAction(); return; }
                double total = (target - start).TotalSeconds;
                bar.Value = Math.Min(1, Math.Max(0, 1 - remaining / total));
                countdown.Text = Logic.FormatSpan(remaining);
                PopupLogic(remaining, total);
                if (remaining <= 60)
                {
                    countdown.Foreground = Res("Danger");
                    message.Text = "Manca meno di un minuto. Premi Annulla per fermare.";
                }
                return;
            }
            if (kind != "idle") return;

            bool inWin = !useWindow || Logic.InWindow(now, winFrom, winTo);
            DateTime lastInput = now.AddSeconds(-Native.IdleSeconds());
            bool mediaNow = false;
            string why = "";
            if (mediaGuard)
            {
                sensor.PollAudio(now);
                sensor.PollSmtcAsync(now);
                if (sensor.Smtc) { mediaNow = true; why = "Video o musica in riproduzione"; }
                else if (sensor.AudioRecent(now)) { mediaNow = true; why = "Audio in uscita"; }
            }
            if (mediaNow || !inWin) lastMedia = now;

            DateTime lastActivity = start;
            if (lastInput > lastActivity) lastActivity = lastInput;
            if (lastMedia > lastActivity) lastActivity = lastMedia;
            double idleSec = (now - lastActivity).TotalSeconds;
            double left = idleThreshold - idleSec;

            tileIdle.Text = Logic.FormatSpan(idleSec);
            tileMedia.Text = !mediaGuard ? "Ignorato" : mediaNow ? why : "Silenzio";
            tileMedia.Foreground = Res(mediaNow ? "Good" : "Text");
            tileWindow.Text = !useWindow ? "Sempre attivo" : inWin ? "Attivo ora" : "In attesa della fascia";

            if (!inWin)
            {
                countdown.Text = "--:--:--";
                countdown.Foreground = Res("Muted");
                bar.Value = 0;
                message.Text = "Fuori dalla fascia oraria: per ora non succede nulla.";
                ClosePopup(); popupShown = false; warned = false;
                return;
            }
            if (mediaNow)
            {
                countdown.Text = Logic.FormatSpan(idleThreshold);
                countdown.Foreground = Res("Good");
                bar.Value = 0;
                message.Text = "Stai guardando o ascoltando qualcosa: il conto è in pausa.";
                ClosePopup(); popupShown = false; warned = false;
                return;
            }
            if (left <= 0) { countdown.Text = "00:00:00"; CompleteAction(); return; }

            bar.Value = Math.Min(1, Math.Max(0, idleSec / idleThreshold));
            countdown.Text = Logic.FormatSpan(left);
            PopupLogic(left, idleThreshold);
            if (left <= 60)
            {
                countdown.Foreground = Res("Danger");
                message.Text = "Sto per agire. Muovi il mouse o premi un tasto per fermare tutto.";
                if (!warned) { warned = true; System.Media.SystemSounds.Exclamation.Play(); }
            }
            else
            {
                countdown.Foreground = Res("Text");
                message.Text = "Nessuna attività rilevata: il conto alla rovescia sta andando.";
                warned = false;
            }
        }

        // ---------- pop-up dei 5 minuti ----------
        void PopupLogic(double remainSec, double totalSec)
        {
            if (totalSec <= 300) return;
            if (remainSec <= 300)
            {
                if (!popupShown) { popupShown = true; ShowPopup(); }
                if (popup != null) ((TextBlock)popup.FindName("Left")).Text = Logic.FormatSpan(remainSec);
            }
            else if (popup != null || popupShown)
            {
                ClosePopup();
                popupShown = false;
            }
        }

        public void ShowPopup()
        {
            Window w = (Window)XamlReader.Load(Resource("Popup.xaml"));
            ((TextBlock)w.FindName("Head")).Text = "Il PC " + Info(action).Verb + " tra poco";
            ((Button)w.FindName("Ok")).Click += delegate { ClosePopup(); };
            foreach (string n in new string[] { "P30", "P60", "P240" })
            {
                Button b = (Button)w.FindName(n);
                b.Click += delegate { Postpone(int.Parse((string)b.Tag)); };
            }
            w.Closed += delegate { if (popup == w) popup = null; };
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            Rect wa = SystemParameters.WorkArea;
            w.Left = wa.Right - w.Width - 16;
            w.Top = wa.Bottom - w.Height - 16;
            popup = w;
            w.Show();
            System.Media.SystemSounds.Asterisk.Play();
        }

        public void ShowPopupOffscreen()
        {
            Window w = (Window)XamlReader.Load(Resource("Popup.xaml"));
            ((TextBlock)w.FindName("Head")).Text = "prova";
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -32000; w.Top = -32000;
            popup = w;
            w.Show();
        }

        public bool PopupOpen { get { return popup != null && popup.IsVisible; } }
        public string PopupLeftText { get { return popup == null ? null : ((TextBlock)popup.FindName("Left")).Text; } }
        public void PopupLogicForTest(double r, double t) { PopupLogic(r, t); }

        public void ClosePopup()
        {
            if (popup == null) return;
            Window w = popup;
            popup = null;
            try { w.Close(); } catch { }
        }

        public void Postpone(int min)
        {
            if (kind == "idle") idleThreshold += min * 60; else target = target.AddMinutes(min);
            popupShown = false;
            Log("Rimandato di " + min + " minuti");
            ClosePopup();
        }

        public void SetStateForTest(string k, int idleThr, DateTime t, string act)
        {
            kind = k; idleThreshold = idleThr; target = t; start = DateTime.Now.AddMinutes(-1); action = act;
        }
        public DateTime TargetForTest { get { return target; } }
        public int IdleThresholdForTest { get { return idleThreshold; } }

        // ---------- area di notifica e avvio con Windows ----------
        public static bool AutoStartEnabled()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("Coprifuoco") != null; }
            catch { return false; }
        }

        public static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (on) k.SetValue("Coprifuoco", "\"" + ExePath + "\" --background");
                    else k.DeleteValue("Coprifuoco", false);
                }
                Log("Avvio con Windows: " + on);
            }
            catch (Exception ex) { Log("Avvio con Windows non modificato: " + ex.Message); }
        }

        public void BuildTray()
        {
            tray = new WF.NotifyIcon();
            try { tray.Icon = new DR.Icon(Resource("Coprifuoco.ico"), WF.SystemInformation.SmallIconSize); }
            catch { tray.Icon = DR.SystemIcons.Application; }

            WF.ContextMenuStrip menu = new WF.ContextMenuStrip();
            menu.Renderer = new WF.ToolStripProfessionalRenderer(new WarmColors());
            menu.BackColor = DR.Color.FromArgb(42, 38, 34);
            menu.ForeColor = DR.Color.FromArgb(230, 220, 202);
            menu.ShowImageMargin = false;
            WF.ToolStripItem open = menu.Items.Add("Apri");
            menuStop = new WF.ToolStripMenuItem("Ferma timer / controllo");
            menuAuto = new WF.ToolStripMenuItem("Avvia con Windows");
            menuAuto.CheckOnClick = true;
            menuAuto.Checked = AutoStartEnabled();
            menu.Items.Add(menuStop);
            menu.Items.Add(menuAuto);
            menu.Items.Add(new WF.ToolStripSeparator());
            WF.ToolStripItem exit = menu.Items.Add("Esci");
            tray.ContextMenuStrip = menu;

            open.Click += delegate { ShowMain(); };
            tray.DoubleClick += delegate { ShowMain(); };
            menuStop.Click += delegate { if (Running) StopRun("Fermato. Non succederà nulla."); };
            menuAuto.Click += delegate { SetAutoStart(menuAuto.Checked); };
            exit.Click += delegate { ExitApp(); };

            tray.Visible = true;
            memTimer = new DispatcherTimer();
            memTimer.Interval = TimeSpan.FromSeconds(60);
            memTimer.Tick += delegate { if (!win.IsVisible) Native.TrimMemory(); };
            memTimer.Start();
            UpdateTray();
        }

        void UpdateTray()
        {
            if (tray == null) return;
            string txt = "Coprifuoco - nessun timer attivo";
            if (Running) txt = kind == "idle" ? "Coprifuoco - controllo inattività attivo" : string.Format("Coprifuoco - {0} alle {1:HH:mm}", Info(action).Label.ToLower(), target);
            tray.Text = txt.Length > 63 ? txt.Substring(0, 63) : txt;
            menuStop.Enabled = Running;
        }

        public void ShowMain()
        {
            win.Show();
            win.WindowState = WindowState.Normal;
            win.Activate();
        }

        void OnWindowClosing(object s, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            win.Hide();
            if (!balloonShown && tray != null)
            {
                balloonShown = true;
                tray.ShowBalloonTip(4000, "Coprifuoco", "Resto attivo in background. Per riaprirmi fai doppio clic su questa icona.", WF.ToolTipIcon.Info);
            }
        }

        void ExitApp()
        {
            if (Running)
            {
                MessageBoxResult r = MessageBox.Show("C'è un timer o un controllo attivo. Uscendo, il PC non si spegnerà da solo. Uscire?", "Coprifuoco", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }
            Log("Uscita dall'app");
            tick.Stop();
            ClosePopup();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            app.Shutdown();
        }

        // All'accensione riprende da solo solo il controllo di inattività, e solo se l'hai chiesto
        public void ResumeIdleIfWanted()
        {
            if (modeIdle.IsChecked == true && autoIdleBox.IsChecked == true)
            {
                Log("Riavvio automatico del controllo di inattività");
                StartRun();
            }
        }

        public bool ShowWindowOnStart { get { return true; } }
    }

    static class Program
    {
        static string SelfTest()
        {
            StringBuilder log = new StringBuilder();
            bool ok = true;
            Action<bool, string> check = delegate(bool cond, string name)
            {
                if (!cond) { ok = false; log.AppendLine("FALLITO: " + name); }
            };
            DateTime now = new DateTime(2026, 10, 22, 15, 0, 0);

            DateTime? t = Logic.TargetTime("in", "0", "30", "", now);
            check(t.HasValue && t.Value == now.AddMinutes(30), "tra 30 min");
            check(!Logic.TargetTime("in", "0", "0", "", now).HasValue, "zero minuti");
            check(!Logic.TargetTime("in", "ab", "5", "", now).HasValue, "testo al posto dei numeri");
            check(!Logic.TargetTime("at", "", "", "25:99", now).HasValue, "ora invalida");
            t = Logic.TargetTime("at", "", "", "23:30", now);
            check(t.HasValue && t.Value == new DateTime(2026, 10, 22, 23, 30, 0), "alle 23:30 oggi");
            t = Logic.TargetTime("at", "", "", "09:00", now);
            check(t.HasValue && t.Value == new DateTime(2026, 10, 23, 9, 0, 0), "alle 09:00 vale per domani");
            check(Logic.IdleThresholdSeconds("2", "0") == 7200, "soglia 2 ore");
            check(!Logic.IdleThresholdSeconds("0", "0").HasValue, "soglia zero");
            TimeSpan a = TimeSpan.Zero, b = new TimeSpan(7, 0, 0);
            check(Logic.InWindow(new DateTime(2026, 10, 23, 3, 0, 0), a, b), "fascia notte: dentro");
            check(!Logic.InWindow(new DateTime(2026, 10, 23, 12, 0, 0), a, b), "fascia notte: fuori");
            a = new TimeSpan(22, 0, 0); b = new TimeSpan(6, 0, 0);
            check(Logic.InWindow(new DateTime(2026, 10, 23, 23, 30, 0), a, b), "fascia a cavallo: sera");
            check(Logic.InWindow(new DateTime(2026, 10, 23, 2, 0, 0), a, b), "fascia a cavallo: notte");
            check(!Logic.InWindow(new DateTime(2026, 10, 23, 12, 0, 0), a, b), "fascia a cavallo: fuori");
            check(Logic.FormatSpan(7265) == "02:01:05", "formato tempo");
            check(Logic.HumanSpan(90) == "1 ora e 30 min", "tempo a parole");

            double idle = Native.IdleSeconds();
            check(idle >= 0 && idle < 86400 * 30, "sensore tastiera/mouse");
            float peak = Native.AudioPeak();
            check(peak >= 0 && peak <= 1, "sensore audio");
            MediaSensor ms = new MediaSensor();
            bool playing = ms.QuerySmtc();
            log.AppendLine(string.Format("sensori: inattivo da {0:N0}s, audio {1:N3}, sessione media pronta: {2}, in riproduzione: {3}", idle, peak, ms.ManagerReady, playing));

            try
            {
                using (Stream icoStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Coprifuoco.ico"))
                {
                    DR.Icon trayIcon = new DR.Icon(icoStream, new DR.Size(16, 16));
                    check(trayIcon.Width == 16, "icona nell'area di notifica (16 px)");
                }
            }
            catch (Exception ex) { check(false, "icona nell'area di notifica: " + ex.Message); }

            Application app = new Application();
            CoprifuocoApp na = new CoprifuocoApp(app, true);
            Window w = null;
            try { w = na.Build(); } catch (Exception ex) { check(false, "interfaccia principale: " + ex.Message); }
            if (w != null)
            {
                na.SetStateForTest("timer", 0, DateTime.Now.AddMinutes(10), "shutdown");
                na.Postpone(30);
                check(Math.Abs((na.TargetForTest - DateTime.Now).TotalMinutes - 40) < 1, "rimanda il timer di 30 min");
                na.SetStateForTest("idle", 7200, DateTime.Now, "shutdown");
                na.Postpone(240);
                check(na.IdleThresholdForTest == 21600, "rimanda l'inattività di 4 ore");
                try
                {
                    na.ShowPopupOffscreen();
                    check(na.PopupOpen, "il pop-up si apre");
                    na.PopupLogicForTest(100, 7200);
                    check(na.PopupLeftText == "00:01:40", "il pop-up mostra il conto alla rovescia");
                    na.ClosePopup();
                    check(!na.PopupOpen, "il pop-up si chiude");
                }
                catch (Exception ex) { check(false, "pop-up: " + ex.Message); }
            }
            log.AppendLine(ok ? "SELFTEST OK" : "SELFTEST FALLITO");
            return log.ToString();
        }

        [STAThread]
        static int Main(string[] args)
        {
            bool background = args.Contains("--background");
            bool dryRun = args.Contains("--dryrun");

            if (args.Contains("--selftest"))
            {
                string result = SelfTest();
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "coprifuoco-selftest.txt"), result, Encoding.UTF8);
                return result.Contains("SELFTEST OK") ? 0 : 1;
            }

            // una sola copia alla volta: se è già aperta, la richiama in primo piano
            Mutex mutex = new Mutex(false, "Local\\CoprifuocoApp");
            bool owner;
            try { owner = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owner = true; }
            if (!owner)
            {
                try { EventWaitHandle.OpenExisting("Local\\CoprifuocoApp.show").Set(); } catch { }
                return 0;
            }
            EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CoprifuocoApp.show");

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                CoprifuocoApp.Log("Errore: " + e.Exception);
                e.Handled = true;
            };

            CoprifuocoApp na = new CoprifuocoApp(app, dryRun);
            Window win;
            try
            {
                win = na.Build();
                na.BuildTray();
            }
            catch (Exception ex)
            {
                CoprifuocoApp.Log("Avvio fallito: " + ex);
                MessageBox.Show("Coprifuoco non è riuscito ad avviarsi.\n\n" + ex.Message, "Coprifuoco");
                return 1;
            }

            Thread waiter = new Thread(delegate()
            {
                while (true)
                {
                    showEvent.WaitOne();
                    app.Dispatcher.BeginInvoke(new Action(na.ShowMain));
                }
            });
            waiter.IsBackground = true;
            waiter.Start();

            CoprifuocoApp.Log("App avviata (prova=" + dryRun + ", sfondo=" + background + ")");
            if (background) { na.ResumeIdleIfWanted(); Native.TrimMemory(); }
            else win.Show();

            app.Run();
            try { mutex.ReleaseMutex(); } catch { }
            return 0;
        }
    }
}
