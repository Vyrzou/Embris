// EmbrisSetup - installs Embris for the current user (no admin rights needed).
// build.bat embeds Embris.exe into this installer; copied into the install folder it doubles as the uninstaller.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Embris
{
    class BigLogo : UiControl
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(Ui.LogoImage, ClientRectangle);
        }
    }

    class SetupForm : Form
    {
        static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Embris");
        static readonly string ExePath = Path.Combine(InstallDir, "Embris.exe");
        static readonly string UninstallerPath = Path.Combine(InstallDir, "Uninstall Embris.exe");
        static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Embris.lnk");
        static readonly string DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Embris.lnk");
        static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Embris");
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Embris";

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        Toggle chkDesktop, chkLaunch;
        RoundButton btnInstall, btnUninstall;
        TextLabel lblStatus;
        bool done; // install finished; the main button now opens the app

        static T Add<T>(Control parent, T c, int x, int y, int w, int h) where T : Control
        {
            c.SetBounds(x, y, w, h);
            parent.Controls.Add(c);
            return c;
        }

        public SetupForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Embris Setup";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 350);
            Font = Ui.F(9.75f);
            Icon = Ui.AppIcon;
            BackColor = Theme.Current.Bg;

            var page = Add(this, new Box(BoxRole.Page), 0, 0, 520, 350);
            Add(page, new BigLogo(), 24, 24, 76, 76);
            Add(page, new TextLabel { Text = "Embris", Font = Ui.Semi(22f) }, 116, 28, 380, 44);
            Add(page, new TextLabel { Text = "Key & click macro for Roblox", Sub = true }, 118, 72, 380, 22);
            Add(page, new TextLabel { Text = "Version " + Ui.Version, Faint = true, Font = Ui.F(8f) }, 118, 94, 380, 18);

            var card = Add(page, new Box(BoxRole.Card) { Title = "Install" }, 24, 120, 472, 150);
            Add(card, new TextLabel { Text = "Installs for this Windows user only. No admin needed.", Sub = true, Font = Ui.F(9f) }, 18, 46, 436, 20);
            Add(card, new TextLabel { Text = InstallDir, Sub = true, Font = Ui.F(8.5f) }, 18, 68, 436, 20);
            chkDesktop = Add(card, new Toggle { Text = "Create a desktop shortcut", Checked = true }, 18, 98, 220, 30);
            chkLaunch = Add(card, new Toggle { Text = "Open Embris when done", Checked = true }, 248, 98, 210, 30);

            bool already = File.Exists(ExePath);
            lblStatus = Add(page, new TextLabel
            {
                Sub = true, Wrap = true, Font = Ui.F(9f),
                Text = already ? "Embris is already installed. Update replaces it with this version." : "Ready to install."
            }, 24, 288, 200, 44);
            btnUninstall = Add(page, new RoundButton { Text = "Uninstall", Visible = already }, 236, 290, 112, 40);
            btnUninstall.Click += (s, e) => { if (Uninstall()) Close(); };
            btnInstall = Add(page, new RoundButton { Style = BtnStyle.Primary, Icon = "", Text = already ? "Update" : "Install", Font = Ui.Semi(10f) }, 360, 290, 136, 40);
            btnInstall.Click += (s, e) => Install();

            ResumeLayout(false);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var t = Theme.Current;
            int dark = 1, caption = t.Sidebar.R | (t.Sidebar.G << 8) | (t.Sidebar.B << 16);
            DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            DwmSetWindowAttribute(Handle, 35, ref caption, 4);
        }

        void Install()
        {
            if (done) { Process.Start(ExePath); Close(); return; }
            btnInstall.Enabled = btnUninstall.Enabled = false;
            lblStatus.Text = "Installing...";
            lblStatus.Update();
            try
            {
                InstallFiles(chkDesktop.Checked);
                if (chkLaunch.Checked) { Process.Start(ExePath); Close(); return; }
                done = true;
                lblStatus.Text = "Installed! Open it from the Start menu" + (chkDesktop.Checked ? " or your desktop." : ".");
                btnInstall.Text = "Open Embris";
                btnUninstall.Visible = true;
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Install failed: " + ex.Message;
            }
            btnInstall.Enabled = btnUninstall.Enabled = true;
        }

        // Copies Embris in, makes the shortcuts and registers it with Windows.
        static void InstallFiles(bool desktopShortcut)
        {
            CloseRunningEmbris();
            Directory.CreateDirectory(InstallDir);
            Retry(() =>
            {
                MakeWritable(ExePath);
                using (var src = typeof(SetupForm).Assembly.GetManifestResourceStream("Embris.exe"))
                using (var dst = File.Create(ExePath))
                    src.CopyTo(dst);
            });
            if (!SamePath(Application.ExecutablePath, UninstallerPath))
            {
                // Run straight from inside a zip, this installer is a read-only file, and a copy
                // keeps that flag. Clear it, or the next update can't replace the uninstaller.
                try
                {
                    Retry(() =>
                    {
                        MakeWritable(UninstallerPath);
                        File.Copy(Application.ExecutablePath, UninstallerPath, true);
                        MakeWritable(UninstallerPath);
                    });
                }
                catch (Exception) { } // an older uninstaller still uninstalls; not worth failing the install over
            }

            CreateShortcut(StartMenuLink, ExePath);
            if (desktopShortcut) CreateShortcut(DesktopLink, ExePath);
            else File.Delete(DesktopLink);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Embris");
                k.SetValue("DisplayIcon", ExePath);
                k.SetValue("DisplayVersion", Ui.Version);
                k.SetValue("Publisher", "Embris");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + UninstallerPath + "\" /uninstall");
                k.SetValue("NoModify", 1);
                k.SetValue("NoRepair", 1);
                k.SetValue("EstimatedSize", (int)(new FileInfo(ExePath).Length / 1024));
            }

            // .emb macro files open with Embris.
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.emb")) k.SetValue("", "Embris.Script");
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Embris.Script"))
            {
                k.SetValue("", "Embris macro");
                using (var icon = k.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + ExePath + "\",0");
                using (var cmd = k.CreateSubKey(@"shell\open\command")) cmd.SetValue("", "\"" + ExePath + "\" \"%1\"");
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }

        // "EmbrisSetup /update": what Embris runs after downloading a new version. Installs with no
        // window (keeping the desktop shortcut as it was) and reopens Embris.
        static void SilentUpdate()
        {
            try
            {
                InstallFiles(File.Exists(DesktopLink));
                Process.Start(ExePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't update Embris: " + ex.Message, "Embris Setup");
            }
        }

        public static bool Uninstall()
        {
            if (MessageBox.Show("Remove Embris and its settings from this PC?", "Embris Setup",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            CloseRunningEmbris();
            try
            {
                File.Delete(StartMenuLink);
                File.Delete(DesktopLink);
                if (File.Exists(ExePath)) { MakeWritable(ExePath); File.Delete(ExePath); }
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.emb", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Embris.Script", false);
                if (Directory.Exists(SettingsDir)) Directory.Delete(SettingsDir, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Some files couldn't be removed: " + ex.Message, "Embris Setup");
            }
            // The uninstaller lives in the install folder, so delete the folder after this process exits.
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false
            });
            MessageBox.Show("Embris has been removed.", "Embris Setup");
            return true;
        }

        static void CloseRunningEmbris()
        {
            foreach (var p in Process.GetProcessesByName("Embris"))
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }
        }

        static void MakeWritable(string path)
        {
            if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        // Embris may take a moment to let go of its file after closing (or antivirus may be scanning
        // it), so keep trying for a few seconds before giving up.
        static void Retry(Action write)
        {
            for (int i = 0; ; i++)
            {
                try { write(); return; }
                catch (Exception ex)
                {
                    if (i >= 25 || !(ex is IOException || ex is UnauthorizedAccessException)) throw;
                    System.Threading.Thread.Sleep(200);
                }
            }
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        // Uses the Windows Script Host COM object, available on every Windows install.
        static void CreateShortcut(string linkPath, string target)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            object link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            var lt = link.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Embris macro" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            Marshal.ReleaseComObject(link);
            Marshal.ReleaseComObject(shell);
        }

        [STAThread]
        static void Main(string[] args)
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0].Equals("/uninstall", StringComparison.OrdinalIgnoreCase)) Uninstall();
            else if (args.Length > 0 && args[0].Equals("/update", StringComparison.OrdinalIgnoreCase)) SilentUpdate();
            else Application.Run(new SetupForm());
        }
    }
}
