using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Installa Coprifuoco")]
[assembly: AssemblyProduct("Coprifuoco")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace CoprifuocoSetup
{
    // Installa Coprifuoco per l'utente corrente: non servono permessi di amministratore.
    class SetupForm : Form
    {
        static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Coprifuoco");
        static readonly string ExeTarget = Path.Combine(InstallDir, "Coprifuoco.exe");
        static readonly string IcoTarget = Path.Combine(InstallDir, "Coprifuoco.ico");
        static readonly string SetupTarget = Path.Combine(InstallDir, "Installa Coprifuoco.exe");
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Coprifuoco";

        static readonly Color Bg = Color.FromArgb(42, 38, 34);
        static readonly Color Rail = Color.FromArgb(33, 30, 26);
        static readonly Color Txt = Color.FromArgb(230, 220, 202);
        static readonly Color Muted = Color.FromArgb(163, 153, 133);
        static readonly Color Accent = Color.FromArgb(190, 149, 97);

        CheckBox chkDesktop, chkStart, chkAuto, chkData;
        Button go, close;
        Label status;
        bool installed;

        public SetupForm(bool uninstallMode)
        {
            installed = File.Exists(ExeTarget);
            Text = "Coprifuoco";
            ClientSize = new Size(480, 360);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Txt;
            Font = new Font("Segoe UI", 10f);
            try { Icon = new Icon(Resource("Coprifuoco.ico"), 32, 32); } catch { }

            Label title = new Label { Text = "Coprifuoco", Font = new Font("Segoe UI Semibold", 20f), AutoSize = true, Left = 28, Top = 20, ForeColor = Txt };
            Label sub = new Label { Text = installed ? "È già installato. Puoi reinstallarlo o toglierlo." : "Il PC rientra all'ora che dici tu.", AutoSize = true, Left = 31, Top = 62, ForeColor = Muted };
            Controls.AddRange(new Control[] { title, sub });

            chkDesktop = Check("Crea il collegamento sul Desktop", 104, true);
            chkStart = Check("Aggiungi al menu Start", 136, true);
            chkAuto = Check("Avvia Coprifuoco quando accendo il PC (resta nascosto vicino all'orologio)", 168, true);
            chkAuto.Width = 430; chkAuto.Height = 44;
            chkData = Check("Quando la tolgo, elimina anche le impostazioni", 222, false);
            chkData.Visible = installed;

            status = new Label { Left = 31, Top = 262, Width = 420, Height = 36, ForeColor = Muted, Text = "Solo per il tuo utente, senza permessi di amministratore." };
            Controls.Add(status);

            go = Btn(installed ? "Reinstalla" : "Installa", 28, 304, true);
            go.Click += delegate { DoInstall(false); };
            close = Btn("Chiudi", 160, 304, false);
            close.Click += delegate { Close(); };
            Controls.Add(go); Controls.Add(close);
            if (installed)
            {
                Button un = Btn("Disinstalla", 292, 304, false);
                un.Click += delegate { DoUninstall(); };
                Controls.Add(un);
            }
            if (uninstallMode) Shown += delegate { DoUninstall(); };
        }

        CheckBox Check(string text, int top, bool on)
        {
            CheckBox c = new CheckBox { Text = text, Left = 31, Top = top, Width = 420, Height = 28, Checked = on, ForeColor = Txt, BackColor = Bg, FlatStyle = FlatStyle.Flat };
            Controls.Add(c);
            return c;
        }

        Button Btn(string text, int left, int top, bool primary)
        {
            Button b = new Button { Text = text, Left = left, Top = top, Width = 120, Height = 36, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 82, 70);
            b.BackColor = primary ? Accent : Rail;
            b.ForeColor = primary ? Color.FromArgb(30, 27, 23) : Txt;
            b.Font = new Font("Segoe UI Semibold", 10f);
            return b;
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Barra del titolo scura, come il resto della finestra
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int v = 1;
            DwmSetWindowAttribute(Handle, 20, ref v, 4);
            DwmSetWindowAttribute(Handle, 19, ref v, 4);
        }

        // Registro dell'installazione: sta accanto all'installer, così si legge facilmente se qualcosa va storto
        static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installazione.log");
        static void Log(string m)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + "  " + m + "\r\n"); } catch { }
        }

        static Stream Resource(string name) { return Assembly.GetExecutingAssembly().GetManifestResourceStream(name); }

        static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Coprifuoco.lnk"); } }
        static string StartLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Coprifuoco.lnk"); } }

        static void MakeLink(string path, string args, string description)
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic l = sh.CreateShortcut(path);
            l.TargetPath = ExeTarget;
            l.Arguments = args;
            l.WorkingDirectory = InstallDir;
            l.IconLocation = IcoTarget;
            l.Description = description;
            l.Save();
        }

        // Toglie la vecchia installazione col nome precedente (Nanna), se c'è
        static void RemoveLegacy()
        {
            foreach (Process p in Process.GetProcessesByName("Nanna"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            Remove(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Nanna.lnk"));
            Remove(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Nanna.lnk"));
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (k != null) k.DeleteValue("Nanna", false); } } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nanna", false); } catch { }
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nanna");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }

        static void StopRunningCoprifuoco()
        {
            foreach (Process p in Process.GetProcessesByName("Coprifuoco"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        // silent = senza finestre: serve per installare da script o per provare l'installer
        public void DoInstall(bool silent)
        {
            try
            {
                go.Enabled = false;
                Log("Installazione avviata. Cartella: " + InstallDir);
                StopRunningCoprifuoco();
                RemoveLegacy();
                Log("Pulizia vecchia versione fatta");
                Directory.CreateDirectory(InstallDir);
                using (Stream s = Resource("Coprifuoco.exe"))
                using (FileStream f = new FileStream(ExeTarget, FileMode.Create, FileAccess.Write))
                    s.CopyTo(f);
                using (Stream si = Resource("Coprifuoco.ico"))
                using (FileStream fi = new FileStream(IcoTarget, FileMode.Create, FileAccess.Write))
                    si.CopyTo(fi);
                Log("File copiati. Coprifuoco.exe presente: " + File.Exists(ExeTarget) + ", " + (File.Exists(ExeTarget) ? new FileInfo(ExeTarget).Length : 0) + " byte");
                string self = Assembly.GetExecutingAssembly().Location;
                if (!string.Equals(self, SetupTarget, StringComparison.OrdinalIgnoreCase)) File.Copy(self, SetupTarget, true);

                if (chkDesktop.Checked) MakeLink(DesktopLink, "", "Coprifuoco - spegnimento programmato del PC"); else Remove(DesktopLink);
                if (chkStart.Checked) MakeLink(StartLink, "", "Coprifuoco - spegnimento programmato del PC"); else Remove(StartLink);
                Log("Collegamenti fatti. Desktop: " + File.Exists(DesktopLink) + ", Start: " + File.Exists(StartLink));
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (chkAuto.Checked) k.SetValue("Coprifuoco", "\"" + ExeTarget + "\" --background");
                    else k.DeleteValue("Coprifuoco", false);
                }
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", "Coprifuoco");
                    k.SetValue("DisplayVersion", "1.0");
                    k.SetValue("Publisher", "Coprifuoco");
                    k.SetValue("DisplayIcon", ExeTarget);
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("UninstallString", "\"" + SetupTarget + "\" --uninstall");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
                Log("Registro aggiornato (avvio con Windows, disinstallazione)");
                status.Text = "Installato. Lo avvio ora.";
                Process.Start(new ProcessStartInfo(ExeTarget, silent ? "--background" : "") { UseShellExecute = false });
                Log("Programma avviato. Fine.");
                if (silent) return;
                MessageBox.Show("Coprifuoco è installato.\n\nLo trovi vicino all'orologio (a volte dentro la freccia ^) e sul Desktop.", "Coprifuoco", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                go.Enabled = true;
                status.Text = "Qualcosa non ha funzionato.";
                Log("ERRORE: " + ex);
                if (silent) throw;
                MessageBox.Show("Non è stato possibile installare Coprifuoco.\n\n" + ex.Message, "Coprifuoco", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void DoUninstall()
        {
            if (MessageBox.Show("Vuoi togliere Coprifuoco da questo PC?", "Coprifuoco", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                StopRunningCoprifuoco();
                Remove(DesktopLink);
                Remove(StartLink);
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (k != null) k.DeleteValue("Coprifuoco", false); }
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                if (chkData.Checked)
                {
                    string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Coprifuoco");
                    if (Directory.Exists(data)) Directory.Delete(data, true);
                }
                if (File.Exists(ExeTarget)) File.Delete(ExeTarget);
                // il programma di disinstallazione non può cancellare sé stesso: lo toglie una riga di comando nascosta, dopo
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + InstallDir + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
                MessageBox.Show("Coprifuoco è stato tolto.", "Coprifuoco", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Non è stato possibile togliere tutto.\n\n" + ex.Message, "Coprifuoco", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static void Remove(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--silent"))
            {
                new SetupForm(false).DoInstall(true);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log("ERRORE nella finestra: " + e.Exception);
                MessageBox.Show("Qualcosa è andato storto.\n\n" + e.Exception.Message + "\n\nI dettagli sono in installazione.log, accanto all'installer.", "Coprifuoco", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Log("Installer aperto");
            Application.Run(new SetupForm(args.Contains("--uninstall")));
        }
    }
}
