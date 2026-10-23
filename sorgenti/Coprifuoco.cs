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

        // Primo momento, dopo "now", in cui scatta l'ora "tod" in uno dei giorni scelti (bit 0 = lunedì ... bit 6 = domenica)
        public static DateTime? NextOccurrence(DateTime now, TimeSpan tod, int mask)
        {
            if ((mask & 127) == 0) return null;
            for (int i = 0; i <= 7; i++)
            {
                DateTime day = now.Date.AddDays(i);
                int idx = ((int)day.DayOfWeek + 6) % 7;
                if ((mask & (1 << idx)) == 0) continue;
                DateTime t = day.Add(tod);
                if (t > now) return t;
            }
            return null;
        }

        static readonly string[] DayNames = { "lunedì", "martedì", "mercoledì", "giovedì", "venerdì", "sabato", "domenica" };

        public static string DaysText(int mask)
        {
            mask &= 127;
            if (mask == 127) return "ogni giorno";
            if (mask == 31) return "dal lunedì al venerdì";
            if (mask == 96) return "il sabato e la domenica";
            List<string> n = new List<string>();
            for (int i = 0; i < 7; i++) if ((mask & (1 << i)) != 0) n.Add(DayNames[i]);
            if (n.Count == 1) return "ogni " + n[0];
            return "ogni " + string.Join(", ", n.Take(n.Count - 1).ToArray()) + " e " + n[n.Count - 1];
        }

        public static string WhenText(DateTime now, DateTime t)
        {
            string hm = t.ToString("HH:mm");
            if (t.Date == now.Date) return "oggi alle " + hm;
            if (t.Date == now.Date.AddDays(1)) return "domani alle " + hm;
            return t.ToString("dddd d MMMM", new CultureInfo("it-IT")) + " alle " + hm;
        }

        public static string Capital(string s) { return s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1); }

        // Una scadenza è "vecchia" se è passata da più di 5 minuti: il PC era spento o in sospensione
        public static bool IsStale(DateTime now, DateTime target) { return (now - target).TotalSeconds > 300; }

        // Quale scadenza riprendere dopo un riavvio: quella salvata se è ancora futura, altrimenti la prossima della ripetizione
        public static DateTime? ResolveRestore(DateTime now, DateTime saved, bool repeat, TimeSpan tod, int mask)
        {
            if (saved > now) return saved;
            return repeat ? NextOccurrence(now, tod, mask) : (DateTime?)null;
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
        // COPRIFUOCO_DATA serve solo alle prove: manda impostazioni e registro in una cartella separata
        public static string DataDir
        {
            get { return Environment.GetEnvironmentVariable("COPRIFUOCO_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Coprifuoco"); }
        }
        static string LogFile { get { return Path.Combine(DataDir, "coprifuoco.log"); } }
        static string SettingsFile { get { return Path.Combine(DataDir, "settings.ini"); } }
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
        bool repeat;                 // ripetizione giornaliera attiva
        int daysMask = 127;          // giorni scelti: bit 0 = lunedì ... bit 6 = domenica
        TimeSpan atTod;              // l'ora scelta per la ripetizione
        DispatcherTimer watchdog;    // se Windows resta fermo su una conferma, dopo un minuto forza
        public bool NoPersist;       // solo per --selftest: non scrivere le impostazioni vere
        public static bool TestMode; // --instance=nome: prove isolate, senza suoni e con le finestre fuori schermo

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
        CheckBox repeatBox;
        StackPanel daysPanel;
        CheckBox[] dayBoxes;
        Button[] dayQuick;

        int DaysMask()
        {
            int m = 0;
            for (int i = 0; i < dayBoxes.Length; i++) if (dayBoxes[i].IsChecked == true) m |= 1 << i;
            return m;
        }

        void SetDays(int mask)
        {
            for (int i = 0; i < dayBoxes.Length; i++) dayBoxes[i].IsChecked = (mask & (1 << i)) != 0;
            UpdateExplain();
        }

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
            repeatBox = F<CheckBox>("RepeatBox"); daysPanel = F<StackPanel>("DaysPanel");
            dayBoxes = new CheckBox[] { F<CheckBox>("DayMon"), F<CheckBox>("DayTue"), F<CheckBox>("DayWed"), F<CheckBox>("DayThu"), F<CheckBox>("DayFri"), F<CheckBox>("DaySat"), F<CheckBox>("DaySun") };
            dayQuick = new Button[] { F<Button>("DaysAll"), F<Button>("DaysWork"), F<Button>("DaysWeekend") };
            repeatBox.Click += delegate
            {
                daysPanel.Visibility = repeatBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                ShowMode();
            };
            foreach (CheckBox d in dayBoxes) d.Click += delegate { UpdateExplain(); };
            F<Button>("DaysAll").Click += delegate { SetDays(127); };
            F<Button>("DaysWork").Click += delegate { SetDays(31); };
            F<Button>("DaysWeekend").Click += delegate { SetDays(96); };

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
            daysPanel.Visibility = repeatBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
            startBtn.Content = m == "idle" ? "Attiva il controllo" : (m == "at" && repeatBox.IsChecked == true) ? "Attiva la ripetizione" : "Avvia timer";
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
                        if (repeatBox.IsChecked == true)
                        {
                            TimeSpan tod;
                            int mask = DaysMask();
                            if (!Logic.TryTimeOfDay(atTime.Text, out tod)) { explain.Text = "Scrivi un'ora valida in formato 24 ore, ad esempio 00:01."; break; }
                            if (mask == 0) { explain.Text = "Scegli almeno un giorno."; break; }
                            DateTime? nx = Logic.NextOccurrence(now, tod, mask);
                            explain.Text = string.Format("{0} alle {1:00}:{2:00} il computer farà l'azione scelta ({3}), senza che tu debba riaprire Coprifuoco. Il prossimo è {4}. Se spegni o riavvii il PC prima, la ripetizione riparte da sola con Windows. Cinque minuti prima compare un avviso: se non premi niente, procedo comunque.",
                                Logic.Capital(Logic.DaysText(mask)), tod.Hours, tod.Minutes, act, Logic.WhenText(now, nx.Value));
                            break;
                        }
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
            cancelBtn.Content = kind == "idle" ? "Ferma il controllo" : repeat ? "Ferma la ripetizione" : "Annulla";
            List<Control> locked = new List<Control> { modeIn, modeAt, modeIdle, hours, minutes, atTime, idleHours, idleMinutes, mediaGuardBox, autoIdleBox, useWindowBox, winFromBox, winToBox, forceBox, repeatBox };
            locked.AddRange(quick);
            locked.AddRange(dayBoxes.Cast<Control>());
            locked.AddRange(dayQuick.Cast<Control>());
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
                if (NoPersist) return;
                List<string> lines = new List<string>
                {
                    "action=" + SelectedAction(), "mode=" + Mode(), "hours=" + hours.Text, "minutes=" + minutes.Text, "at=" + atTime.Text,
                    "idleHours=" + idleHours.Text, "idleMinutes=" + idleMinutes.Text, "mediaGuard=" + (mediaGuardBox.IsChecked == true),
                    "autoIdle=" + (autoIdleBox.IsChecked == true), "useWindow=" + (useWindowBox.IsChecked == true),
                    "winFrom=" + winFromBox.Text, "winTo=" + winToBox.Text, "force=" + (forceBox.IsChecked == true),
                    "repeat=" + (repeatBox.IsChecked == true), "days=" + DaysMask()
                };
                // il timer in corso viene salvato: un riavvio del PC non lo cancella
                if (kind == "timer")
                {
                    lines.Add("run.kind=timer");
                    lines.Add("run.action=" + action);
                    lines.Add("run.force=" + force);
                    lines.Add("run.target=" + target.ToString("o"));
                    lines.Add("run.repeat=" + repeat);
                    lines.Add("run.days=" + daysMask);
                    lines.Add("run.tod=" + string.Format("{0:00}:{1:00}", atTod.Hours, atTod.Minutes));
                }
                File.WriteAllLines(SettingsFile, lines.ToArray(), Encoding.UTF8);
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
            if (s.TryGetValue("repeat", out v)) repeatBox.IsChecked = v == "True";
            if (s.TryGetValue("days", out v))
            {
                int dm;
                if (int.TryParse(v, out dm)) for (int i = 0; i < dayBoxes.Length; i++) dayBoxes[i].IsChecked = (dm & (1 << i)) != 0;
            }
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
                repeat = false;
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
                bool rep = mode == "at" && repeatBox.IsChecked == true;
                TimeSpan tod = TimeSpan.Zero;
                int mask = 127;
                DateTime? t;
                if (rep)
                {
                    mask = DaysMask();
                    if (!Logic.TryTimeOfDay(atTime.Text, out tod)) { message.Text = "Ora non valida. Usa il formato 24 ore, ad esempio 00:01."; return; }
                    if (mask == 0) { message.Text = "Scegli almeno un giorno."; return; }
                    t = Logic.NextOccurrence(DateTime.Now, tod, mask);
                }
                else t = Logic.TargetTime(mode, hours.Text, minutes.Text, atTime.Text, DateTime.Now);
                if (!t.HasValue)
                {
                    message.Text = mode == "in" ? "Inserisci almeno 1 minuto (ore 0-99, minuti 0-999)." : "Ora non valida. Usa il formato 24 ore, ad esempio 23:30.";
                    return;
                }
                kind = "timer";
                repeat = rep;
                daysMask = mask;
                atTod = tod;
                target = t.Value;
                Log(rep
                    ? string.Format("Ripetizione avviata: {0} {1} alle {2:00}:{3:00} (prossimo {4:yyyy-MM-dd HH:mm})", action, Logic.DaysText(mask), tod.Hours, tod.Minutes, target)
                    : string.Format("Timer avviato: {0} alle {1:yyyy-MM-dd HH:mm:ss}", action, target));
                stateLabel.Text = TimerLabel();
                tiles.Visibility = Visibility.Collapsed;
                message.Text = "";
            }
            // un timer deve sopravvivere a un riavvio: serve che Coprifuoco parta con Windows
            string extra = "";
            if (kind == "timer" && !AutoStartEnabled())
            {
                SetAutoStart(true);
                if (menuAuto != null) menuAuto.Checked = true;
                extra = "Ho attivato «Avvia con Windows»: serve a tenere il timer valido anche dopo un riavvio.";
            }
            SaveSettings();
            countdown.Foreground = Res("Text");
            SetRunning(true);
            OnTick(null, null);
            if (kind != null) tick.Start();
            if (extra.Length > 0) message.Text = extra;
            UpdateTray();
        }

        string TimerLabel()
        {
            string a = Info(action).Label.ToLower();
            if (!repeat) return string.Format("{0} alle {1:HH:mm}", a, target);
            return string.Format("{0} alle {1:00}:{2:00}: {3}. Prossimo: {4}", Logic.Capital(Logic.DaysText(daysMask)), atTod.Hours, atTod.Minutes, a, Logic.WhenText(DateTime.Now, target));
        }

        public void StopRun(string msg)
        {
            tick.Stop();
            ClosePopup();
            Log("Timer/controllo fermato dall'utente");
            CloseNotice();
            kind = null;
            SetRunning(false);
            repeat = false;
            SaveSettings();
            ResetDisplay();
            if (msg != null) message.Text = msg;
            UpdateTray();
        }

        public bool Running { get { return tick != null && tick.IsEnabled; } }

        void CompleteAction()
        {
            tick.Stop();
            ClosePopup();
            // con la ripetizione, prima di agire preparo e salvo la prossima scadenza: se il PC si spegne, al riavvio riparte
            bool again = false;
            if (kind == "timer" && repeat)
            {
                DateTime now = DateTime.Now;
                DateTime? nx = Logic.NextOccurrence(now, atTod, daysMask);
                if (nx.HasValue) { target = nx.Value; start = now; popupShown = false; warned = false; again = true; Log("Ripetizione: prossimo " + target.ToString("yyyy-MM-dd HH:mm")); }
            }
            if (!again) kind = null;
            SaveSettings();
            message.Text = "Esecuzione in corso...";
            PowerAction(action, force);
            ArmShutdownWatchdog(action);
            if (again)
            {
                stateLabel.Text = TimerLabel();
                message.Text = dryRun ? "Prova: azione non eseguita. Prossimo: " + Logic.WhenText(DateTime.Now, target) : "Fatto. Prossimo: " + Logic.WhenText(DateTime.Now, target);
                tick.Start();
                UpdateTray();
                return;
            }
            repeat = false;
            SetRunning(false);
            ResetDisplay();
            if (dryRun) message.Text = "Prova: azione non eseguita (modalità test).";
            UpdateTray();
        }

        // Se Windows resta fermo su una conferma («questa app impedisce l'arresto») e nessuno risponde,
        // dopo un minuto forzo la chiusura: di notte non c'è nessuno a premere il tasto.
        void ArmShutdownWatchdog(string key)
        {
            if (dryRun || force || (key != "shutdown" && key != "restart")) return;
            if (watchdog != null) watchdog.Stop();
            watchdog = new DispatcherTimer();
            watchdog.Interval = TimeSpan.FromSeconds(60);
            watchdog.Tick += delegate
            {
                watchdog.Stop();
                Log("Windows non ha completato l'operazione in 60 secondi (app che chiedono conferma?): forzo la chiusura");
                Run("shutdown.exe", (key == "restart" ? "/r" : "/s") + " /t 0 /f");
            };
            watchdog.Start();
        }

        // Scadenza passata da più di 5 minuti (PC spento o in sospensione): non spengo di colpo al risveglio
        void SkipMissed(DateTime now)
        {
            Log(string.Format("Scadenza saltata: prevista {0:yyyy-MM-dd HH:mm}, ma sono le {1:HH:mm} (PC spento o in sospensione)", target, now));
            ClosePopup();
            popupShown = false;
            if (repeat)
            {
                DateTime? nx = Logic.NextOccurrence(now, atTod, daysMask);
                if (nx.HasValue)
                {
                    target = nx.Value; start = now;
                    SaveSettings();
                    stateLabel.Text = TimerLabel();
                    message.Text = "Scadenza saltata perché il PC non era attivo. Prossimo: " + Logic.WhenText(now, target);
                    UpdateTray();
                    return;
                }
            }
            tick.Stop();
            kind = null;
            repeat = false;
            SetRunning(false);
            ResetDisplay();
            SaveSettings();
            message.Text = "Scadenza saltata: il PC non era attivo all'ora prevista.";
            UpdateTray();
            if (tray != null) tray.ShowBalloonTip(6000, "Coprifuoco", "Il timer era scaduto mentre il PC non era attivo: non l'ho eseguito.", WF.ToolTipIcon.Info);
        }

        // ---------- dopo un riavvio: riprende il timer salvato e chiede conferma ----------
        Window notice;
        DispatcherTimer noticeTimer;

        public void RestoreRun()
        {
            Dictionary<string, string> s = ReadSettings();
            string v;
            if (!s.TryGetValue("run.kind", out v) || v != "timer") return;
            try
            {
                DateTime saved = DateTime.Parse(s["run.target"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                bool rep = s.TryGetValue("run.repeat", out v) && v == "True";
                int mask = 127;
                int parsedMask;
                if (s.TryGetValue("run.days", out v) && int.TryParse(v, out parsedMask)) mask = parsedMask;
                TimeSpan tod = TimeSpan.Zero;
                if (s.TryGetValue("run.tod", out v)) Logic.TryTimeOfDay(v, out tod);
                string act = s.ContainsKey("run.action") ? s["run.action"] : "shutdown";
                DateTime now = DateTime.Now;
                DateTime? t = Logic.ResolveRestore(now, saved, rep, tod, mask);
                if (!t.HasValue)
                {
                    Log(string.Format("Timer salvato per {0:yyyy-MM-dd HH:mm} scaduto mentre il PC non era attivo: non lo riprendo", saved));
                    kind = null;
                    SaveSettings();
                    if (tray != null) tray.ShowBalloonTip(8000, "Coprifuoco", string.Format("Il timer delle {0:HH:mm} era scaduto mentre il PC non era attivo: non l'ho eseguito.", saved), WF.ToolTipIcon.Info);
                    return;
                }
                action = act;
                force = s.TryGetValue("run.force", out v) && v == "True";
                repeat = rep; daysMask = mask; atTod = tod; target = t.Value;
                kind = "timer";
                start = now;
                warned = false; popupShown = false;
                stateLabel.Text = TimerLabel();
                tiles.Visibility = Visibility.Collapsed;
                message.Text = "";
                SetRunning(true);
                OnTick(null, null);
                if (kind != null) tick.Start();
                UpdateTray();
                SaveSettings();
                Log(string.Format("Timer ripreso dopo l'avvio: {0}, prossima scadenza {1:yyyy-MM-dd HH:mm}", action, target));
                ShowRestartNotice();
            }
            catch (Exception ex) { Log("Non sono riuscito a riprendere il timer salvato: " + ex.Message); }
        }

        // Banner dopo il riavvio: "Ti ricordi di quel timer? Lo tengo valido?" Se non premi niente, resta valido.
        void ShowRestartNotice()
        {
            CloseNotice();
            Window w = (Window)XamlReader.Load(Resource("Riavvio.xaml"));
            DateTime now = DateTime.Now;
            string what = Info(action).Label.ToLower();
            ((TextBlock)w.FindName("Head")).Text = "Ti ricordi di quel timer?";
            ((TextBlock)w.FindName("Body")).Text = repeat
                ? string.Format("Il computer è stato riavviato. Avevi messo: {0} alle {1:00}:{2:00}, {3}. Il prossimo è {4}. Lo tengo valido?", what, atTod.Hours, atTod.Minutes, Logic.DaysText(daysMask), Logic.WhenText(now, target))
                : string.Format("Il computer è stato riavviato. Avevi messo: {0} {1}. Lo tengo valido?", what, Logic.WhenText(now, target));
            ((Button)w.FindName("Keep")).Click += delegate { Log("Dopo il riavvio: timer confermato dall'utente"); CloseNotice(); };
            ((Button)w.FindName("Cancel")).Click += delegate
            {
                Log("Dopo il riavvio: timer annullato dall'utente");
                CloseNotice();
                StopRun("Annullato dopo il riavvio. Non succederà nulla.");
            };
            w.Closed += delegate { if (notice == w) notice = null; };
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            Rect wa = SystemParameters.WorkArea;
            w.Left = wa.Right - w.Width - 16;
            w.Top = wa.Bottom - w.Height - 16;
            if (TestMode) { w.Left = -32000; w.Top = -32000; }
            notice = w;
            w.Show();
            if (!TestMode) System.Media.SystemSounds.Asterisk.Play();
            // niente risposta = va bene: dopo 3 minuti il banner sparisce e il timer resta valido
            noticeTimer = new DispatcherTimer();
            noticeTimer.Interval = TimeSpan.FromMinutes(3);
            noticeTimer.Tick += delegate { Log("Dopo il riavvio: nessuna risposta, il timer resta valido"); CloseNotice(); };
            noticeTimer.Start();
        }

        void CloseNotice()
        {
            if (noticeTimer != null) { noticeTimer.Stop(); noticeTimer = null; }
            if (notice == null) return;
            Window w = notice;
            notice = null;
            try { w.Close(); } catch { }
        }

        public bool NoticeOpen { get { return notice != null && notice.IsVisible; } }
        public void ShowNoticeOffscreenForTest()
        {
            Window w = (Window)XamlReader.Load(Resource("Riavvio.xaml"));
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -32000; w.Top = -32000;
            notice = w;
            w.Show();
        }
        public void CloseNoticeForTest() { CloseNotice(); }
        public bool HasNoticeButtons()
        {
            Window w = (Window)XamlReader.Load(Resource("Riavvio.xaml"));
            return w.FindName("Keep") != null && w.FindName("Cancel") != null && w.FindName("Head") != null && w.FindName("Body") != null;
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
                if (remaining <= 0)
                {
                    if (Logic.IsStale(now, target)) { SkipMissed(now); return; }
                    countdown.Text = "00:00:00";
                    CompleteAction();
                    return;
                }
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
                if (!warned) { warned = true; if (!TestMode) System.Media.SystemSounds.Exclamation.Play(); }
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
            if (TestMode) { w.Left = -32000; w.Top = -32000; }
            popup = w;
            w.Show();
            if (!TestMode) System.Media.SystemSounds.Asterisk.Play();
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
            SaveSettings();
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
            if (Running) return;
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
            // il test non deve scrivere nel registro e nelle impostazioni vere
            Environment.SetEnvironmentVariable("COPRIFUOCO_DATA", Path.Combine(Path.GetTempPath(), "coprifuoco-selftest-dati"));
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

            // ripetizione giornaliera (giovedì 22 ottobre 2026, ore 22:00)
            DateTime thu = new DateTime(2026, 10, 22, 22, 0, 0);
            TimeSpan t0001 = new TimeSpan(0, 1, 0);
            t = Logic.NextOccurrence(thu, t0001, 127);
            check(t.HasValue && t.Value == new DateTime(2026, 10, 23, 0, 1, 0), "ogni giorno alle 00:01: il prossimo è domani");
            t = Logic.NextOccurrence(new DateTime(2026, 10, 23, 0, 0, 30), t0001, 127);
            check(t.HasValue && t.Value == new DateTime(2026, 10, 23, 0, 1, 0), "30 secondi prima delle 00:01: scatta oggi");
            t = Logic.NextOccurrence(new DateTime(2026, 10, 23, 0, 1, 0), t0001, 127);
            check(t.HasValue && t.Value == new DateTime(2026, 10, 24, 0, 1, 0), "alle 00:01 in punto: il prossimo è il giorno dopo");
            t = Logic.NextOccurrence(new DateTime(2026, 10, 23, 1, 0, 0), t0001, 31);
            check(t.HasValue && t.Value == new DateTime(2026, 10, 26, 0, 1, 0), "solo lunedì-venerdì: venerdì dopo le 00:01 salta al lunedì");
            t = Logic.NextOccurrence(thu, t0001, 96);
            check(t.HasValue && t.Value == new DateTime(2026, 10, 24, 0, 1, 0), "solo weekend: giovedì sera, il prossimo è sabato 00:01");
            check(!Logic.NextOccurrence(thu, t0001, 0).HasValue, "nessun giorno scelto");
            check(Logic.DaysText(127) == "ogni giorno" && Logic.DaysText(31) == "dal lunedì al venerdì" && Logic.DaysText(96) == "il sabato e la domenica", "testo dei giorni");
            check(Logic.DaysText(1 | 4 | 16) == "ogni lunedì, mercoledì e venerdì", "testo di giorni sparsi");
            check(Logic.WhenText(thu, new DateTime(2026, 10, 23, 0, 1, 0)) == "domani alle 00:01", "testo: domani");
            // dopo un riavvio
            DateTime midnight = new DateTime(2026, 10, 23, 0, 0, 0);
            check(Logic.ResolveRestore(new DateTime(2026, 10, 22, 17, 0, 0), midnight, false, TimeSpan.Zero, 127) == midnight, "dopo il riavvio la scadenza di mezzanotte resta valida");
            check(!Logic.ResolveRestore(new DateTime(2026, 10, 23, 8, 0, 0), midnight, false, TimeSpan.Zero, 127).HasValue, "scadenza singola passata: non si riprende");
            t = Logic.ResolveRestore(new DateTime(2026, 10, 23, 8, 0, 0), midnight, true, TimeSpan.Zero, 127);
            check(t.HasValue && t.Value == new DateTime(2026, 10, 24, 0, 0, 0), "ripetizione passata: si passa alla prossima");
            check(Logic.IsStale(new DateTime(2026, 10, 23, 7, 0, 0), midnight) && !Logic.IsStale(new DateTime(2026, 10, 23, 0, 2, 0), midnight), "scadenza vecchia solo dopo 5 minuti");

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
            na.NoPersist = true;   // il test non deve toccare le impostazioni vere
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
                    check(na.HasNoticeButtons(), "banner dopo il riavvio: interfaccia");
                    na.ShowNoticeOffscreenForTest();
                    check(na.NoticeOpen, "il banner dopo il riavvio si apre");
                    na.CloseNoticeForTest();
                    check(!na.NoticeOpen, "il banner dopo il riavvio si chiude");
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
            // --instance=nome: copia di prova separata dall'app vera (altro nome di mutex, niente icona, niente suoni)
            string instance = "";
            foreach (string a in args) if (a.StartsWith("--instance=")) instance = a.Substring(11);
            CoprifuocoApp.TestMode = instance.Length > 0;

            if (args.Contains("--selftest"))
            {
                string result = SelfTest();
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "coprifuoco-selftest.txt"), result, Encoding.UTF8);
                return result.Contains("SELFTEST OK") ? 0 : 1;
            }

            // una sola copia alla volta: se è già aperta, la richiama in primo piano
            Mutex mutex = new Mutex(false, "Local\\CoprifuocoApp" + instance);
            bool owner;
            try { owner = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owner = true; }
            if (!owner)
            {
                try { EventWaitHandle.OpenExisting("Local\\CoprifuocoApp" + instance + ".show").Set(); } catch { }
                return 0;
            }
            EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CoprifuocoApp" + instance + ".show");

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
                if (!CoprifuocoApp.TestMode) na.BuildTray();
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
            na.RestoreRun();
            if (background) { na.ResumeIdleIfWanted(); Native.TrimMemory(); }
            else win.Show();

            app.Run();
            try { mutex.ReleaseMutex(); } catch { }
            return 0;
        }
    }
}
