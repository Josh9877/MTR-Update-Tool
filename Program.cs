// MTR Manual Update Tool
// Copies the user-selected, UNMODIFIED (Authenticode-signed) MTR update script to a
// locked-down temp folder and runs:
//     powershell.exe -ExecutionPolicy Unrestricted "<path to script>"
// while showing live output and a progress bar. Written in C# 5 so it builds with the
// csc.exe that ships with Windows (no SDK / Visual Studio required).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace MtrUpdateTool
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class Milestone
    {
        public readonly string Key;
        public readonly int Percent;
        public readonly string Text;

        public Milestone(string key, int percent, string text)
        {
            Key = key;
            Percent = percent;
            Text = text;
        }
    }

    internal sealed class MainForm : Form
    {
        // Used only if the chosen file name has no usable characters.
        private const string FallbackScriptFileName = "MTR-Update.ps1";
        private const string DownloadPageUrl = "https://learn.microsoft.com/en-us/microsoftteams/rooms/manual-update";

        // The script has no real progress reporting, so progress is derived from its log lines.
        // Percentages only ever move forward. 0-10% is used for copying the script.
        private static readonly Milestone[] Milestones = new Milestone[]
        {
            new Milestone("Log initialized",                    14, "Initializing"),
            new Milestone("OS version:",                        16, "Checking prerequisites"),
            new Milestone("Latest:",                            22, "Checking installed MTR app"),
            new Milestone("Provisioned:",                       26, "Checking provisioned app"),
            new Milestone("All versions:",                      30, "Listing installed versions"),
            new Milestone("Skype outdated?",                    34, "Evaluating version status"),
            new Milestone("Your system already matches",       100, "Already up to date"),
            new Milestone("Package: Generating the MSI",        38, "Generating MSI package"),
            new Milestone("Package: MSI successfully written",  48, "MSI written and verified"),
            new Milestone("Package: Extracting the MSI",        52, "Extracting MSI"),
            new Milestone("Package: MSI successfully extracted",62, "MSI extracted"),
            new Milestone("de-provision app",                   66, "De-provisioning old app"),
            new Milestone("successfully de-provisioned",        70, "Old app de-provisioned"),
            new Milestone("Re-provisioning",                    74, "Provisioning new app version"),
            new Milestone("Package re-provisioned",             82, "New app provisioned"),
            new Milestone("Killing the watchdog",               86, "Preparing Skype user"),
            new Milestone("Impersonating user",                 90, "Registering app for Skype user"),
            new Milestone("Re-registration complete",           94, "App registered"),
            new Milestone("Impersonation dropped",              96, "Finishing up"),
            new Milestone("App state consistent, reboot",       99, "Rebooting")
        };

        // Branding colours (header band is dark so the light-gray logo stands out)
        private static readonly Color HeaderBack = Color.FromArgb(34, 34, 38);
        private static readonly Color StatusNormal = Color.Gainsboro;
        private static readonly Color StatusOk = Color.FromArgb(120, 220, 140);
        private static readonly Color StatusBad = Color.FromArgb(255, 125, 110);

        // UI
        private Label titleLabel;
        private Label statusLabel;
        private ProgressBar progress;
        private TextBox output;
        private TextBox inputBox;
        private Button sendBtn;
        private Button sendRBtn;
        private Button startBtn;
        private Button stopBtn;
        private Button saveBtn;
        private CheckBox autoR;
        private TextBox scriptBox;
        private Button browseBtn;
        private Button getBtn;

        // State
        private readonly object logLock = new object();
        private readonly object parseLock = new object();
        private readonly StringBuilder fullLog = new StringBuilder();
        private readonly StringBuilder lineBuf = new StringBuilder();
        private volatile bool autoAnswer = true;
        private volatile bool running;
        private volatile Process proc;
        private int currentProgress;
        private string workDir;
        private string scriptPath;      // the safe copy that is actually executed
        private string selectedScript;  // the file the user picked

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr changeInfo);

        // Windows blocks drag & drop from a normal Explorer window into an elevated window.
        // These message-filter exceptions lift that block for the controls that accept drops.
        private static void AllowDropFromNonElevated(Control c)
        {
            EventHandler apply = delegate(object sender, EventArgs e)
            {
                try
                {
                    ChangeWindowMessageFilterEx(c.Handle, 0x233, 1, IntPtr.Zero); // WM_DROPFILES
                    ChangeWindowMessageFilterEx(c.Handle, 0x4A, 1, IntPtr.Zero);  // WM_COPYDATA
                    ChangeWindowMessageFilterEx(c.Handle, 0x49, 1, IntPtr.Zero);  // WM_COPYGLOBALDATA
                }
                catch (Exception) { }
            };
            c.HandleCreated += apply;
            if (c.IsHandleCreated) apply(c, EventArgs.Empty);
        }

        public MainForm()
        {
            BuildUi();
            FormClosing += OnFormClosing;
            Shown += OnShown;
        }

        // ------------------------------------------------------------------ UI

        private void BuildUi()
        {
            Text = "MTR Manual Update Tool";
            ClientSize = new Size(920, 640);
            MinimumSize = new Size(720, 460);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            // Output (Fill) must be added first so it is docked last.
            output = new TextBox();
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Both;
            output.WordWrap = false;
            output.Dock = DockStyle.Fill;
            output.Font = new Font("Consolas", 9.5F);
            output.BackColor = Color.FromArgb(24, 24, 24);
            output.ForeColor = Color.Gainsboro;
            output.MaxLength = 0;
            Controls.Add(output);

            // Row: input
            TableLayoutPanel inputRow = new TableLayoutPanel();
            inputRow.Dock = DockStyle.Bottom;
            inputRow.Height = 38;
            inputRow.Padding = new Padding(10, 5, 10, 3);
            inputRow.ColumnCount = 4;
            inputRow.RowCount = 1;
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Label inLabel = new Label();
            inLabel.Text = "Send input:";
            inLabel.AutoSize = true;
            inLabel.Anchor = AnchorStyles.Left;

            inputBox = new TextBox();
            inputBox.Dock = DockStyle.Fill;
            inputBox.Enabled = false;
            inputBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SendTypedInput();
                }
            };

            sendBtn = new Button();
            sendBtn.Text = "Send";
            sendBtn.Enabled = false;
            sendBtn.AutoSize = true;
            sendBtn.Click += delegate { SendTypedInput(); };

            sendRBtn = new Button();
            sendRBtn.Text = "Send \"R\" (Run once)";
            sendRBtn.Enabled = false;
            sendRBtn.AutoSize = true;
            sendRBtn.Click += delegate { SendInput("R", false); };

            inputRow.Controls.Add(inLabel, 0, 0);
            inputRow.Controls.Add(inputBox, 1, 0);
            inputRow.Controls.Add(sendBtn, 2, 0);
            inputRow.Controls.Add(sendRBtn, 3, 0);
            Controls.Add(inputRow);

            // Row: main buttons
            FlowLayoutPanel buttonRow = new FlowLayoutPanel();
            buttonRow.Dock = DockStyle.Bottom;
            buttonRow.Height = 44;
            buttonRow.Padding = new Padding(10, 6, 10, 6);
            buttonRow.FlowDirection = FlowDirection.LeftToRight;

            startBtn = new Button();
            startBtn.Text = "Start Update";
            startBtn.Size = new Size(120, 30);
            startBtn.Enabled = false;
            startBtn.Click += delegate { StartRun(); };

            stopBtn = new Button();
            stopBtn.Text = "Stop";
            stopBtn.Size = new Size(80, 30);
            stopBtn.Enabled = false;
            stopBtn.Click += delegate { StopRun(); };

            saveBtn = new Button();
            saveBtn.Text = "Save output...";
            saveBtn.Size = new Size(110, 30);
            saveBtn.Click += delegate { SaveOutput(); };

            autoR = new CheckBox();
            autoR.Text = "Auto-answer \"Run once\" prompt with R";
            autoR.Checked = true;
            autoR.AutoSize = true;
            autoR.Margin = new Padding(14, 7, 0, 0);
            autoR.CheckedChanged += delegate { autoAnswer = autoR.Checked; };

            buttonRow.Controls.Add(startBtn);
            buttonRow.Controls.Add(stopBtn);
            buttonRow.Controls.Add(saveBtn);
            buttonRow.Controls.Add(autoR);
            Controls.Add(buttonRow);

            // Row: script picker (drop a .ps1 anywhere on the window, or browse)
            TableLayoutPanel scriptRow = new TableLayoutPanel();
            scriptRow.Dock = DockStyle.Top;
            scriptRow.Height = 40;
            scriptRow.Padding = new Padding(10, 7, 10, 3);
            scriptRow.ColumnCount = 4;
            scriptRow.RowCount = 1;
            scriptRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            scriptRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            scriptRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            scriptRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Label scriptLabel = new Label();
            scriptLabel.Text = "MTR update script (.ps1):";
            scriptLabel.AutoSize = true;
            scriptLabel.Anchor = AnchorStyles.Left;

            scriptBox = new TextBox();
            scriptBox.Dock = DockStyle.Fill;
            scriptBox.TextChanged += delegate { UpdateStartEnabled(); };

            browseBtn = new Button();
            browseBtn.Text = "Browse...";
            browseBtn.AutoSize = true;
            browseBtn.Click += delegate { BrowseForScript(); };

            scriptRow.Controls.Add(scriptLabel, 0, 0);
            scriptRow.Controls.Add(scriptBox, 1, 0);
            getBtn = new Button();
            getBtn.Text = "Get latest script...";
            getBtn.AutoSize = true;
            getBtn.Click += delegate { OpenDownloadPage(); };

            scriptRow.Controls.Add(browseBtn, 2, 0);
            scriptRow.Controls.Add(getBtn, 3, 0);
            Controls.Add(scriptRow);

            // Drag & drop of the .ps1 onto the window, the log area or the path box
            foreach (Control dz in new Control[] { this, output, scriptBox })
            {
                dz.AllowDrop = true;
                dz.DragEnter += OnDragEnterFiles;
                dz.DragDrop += OnDragDropFiles;
                AllowDropFromNonElevated(dz);
            }

            // Top: logo + title + status + progress (dark branded band)
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 108;
            top.BackColor = HeaderBack;

            PictureBox logo = new PictureBox();
            logo.Location = new Point(12, 6);
            logo.Size = new Size(84, 46);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.BackColor = HeaderBack;
            try
            {
                using (Stream ls = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
                {
                    if (ls != null) logo.Image = new Bitmap(ls);
                }
            }
            catch (Exception) { }

            titleLabel = new Label();
            titleLabel.Text = "Microsoft Teams Rooms - Manual Update";
            titleLabel.Font = new Font("Segoe UI Semibold", 13F);
            titleLabel.ForeColor = Color.White;
            titleLabel.BackColor = HeaderBack;
            titleLabel.AutoSize = true;
            titleLabel.Location = new Point(106, 16);

            statusLabel = new Label();
            statusLabel.Text = "Choose the MTR update script, then click \"Start Update\".";
            statusLabel.AutoSize = false;
            statusLabel.BackColor = HeaderBack;
            statusLabel.ForeColor = StatusNormal;
            statusLabel.Location = new Point(12, 60);
            statusLabel.Size = new Size(ClientSize.Width - 24, 18);
            statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            progress = new ProgressBar();
            progress.Minimum = 0;
            progress.Maximum = 100;
            progress.Location = new Point(12, 80);
            progress.Size = new Size(ClientSize.Width - 24, 20);
            progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            top.Controls.Add(logo);
            top.Controls.Add(titleLabel);
            top.Controls.Add(statusLabel);
            top.Controls.Add(progress);
            Controls.Add(top);
        }

        private void OnShown(object sender, EventArgs e)
        {
            if (!IsAdmin())
            {
                AppendLog("WARNING: This tool is not running as Administrator. " +
                          "The update will fail. Re-launch it with \"Run as administrator\".\n\n");
            }
            AppendLog("Drop the downloaded MTR update script (.ps1) onto this window, or click Browse.\n" +
                      "It is then run with:\n" +
                      "  powershell.exe -ExecutionPolicy Unrestricted \"<path to script>\"\n\n");
            AutoDetectScript();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (running)
            {
                DialogResult r = MessageBox.Show(this,
                    "The update is still running. Closing now will terminate it and may leave the " +
                    "system in an inconsistent state.\n\nClose anyway?",
                    "Update in progress", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                KillTree();
                Thread.Sleep(500);
            }
            CleanupWorkDir();
        }

        private void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a); else a();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void SetStatus(string text, Color color)
        {
            Ui(delegate
            {
                statusLabel.Text = text;
                statusLabel.ForeColor = color;
            });
        }

        private void SetProgress(int pct, string text)
        {
            if (pct > 100) pct = 100;
            Ui(delegate
            {
                if (pct > currentProgress)
                {
                    currentProgress = pct;
                    progress.Value = pct;
                }
                statusLabel.ForeColor = StatusNormal;
                statusLabel.Text = currentProgress.ToString(CultureInfo.InvariantCulture) + "%  -  " + text;
            });
        }

        private void AppendLog(string text)
        {
            lock (logLock) { fullLog.Append(text); }
            string display = text.Replace("\r", "").Replace("\n", "\r\n");
            Ui(delegate { output.AppendText(display); });
        }

        // ------------------------------------------------------------------ Run control

        private void StartRun()
        {
            if (running) return;
            string chosen = scriptBox.Text.Trim().Trim('"');
            if (!IsValidScript(chosen))
            {
                MessageBox.Show(this, "Please choose the MTR update script (.ps1) first.",
                    "No script selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            selectedScript = chosen;
            running = true;
            currentProgress = 0;
            lineBuf.Length = 0;
            lock (logLock) { fullLog.Length = 0; }
            output.Clear();
            progress.Value = 0;
            startBtn.Enabled = false;
            stopBtn.Enabled = true;
            inputBox.Enabled = true;
            sendBtn.Enabled = true;
            sendRBtn.Enabled = true;
            scriptBox.Enabled = false;
            browseBtn.Enabled = false;
            SetStatus("0%  -  Copying script...", StatusNormal);

            Thread t = new Thread(RunWorker);
            t.IsBackground = true;
            t.Start();
        }

        private void StopRun()
        {
            DialogResult r = MessageBox.Show(this,
                "Stopping the update part-way can leave the MTR app in an inconsistent state " +
                "(you may need to re-run the update).\n\nStop it now?",
                "Stop update", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (r == DialogResult.Yes)
            {
                AppendLog("\n[Stopping PowerShell at user request...]\n");
                KillTree();
            }
        }

        private void KillTree()
        {
            Process p = proc;
            if (p == null) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe",
                    "/PID " + p.Id.ToString(CultureInfo.InvariantCulture) + " /T /F");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                using (Process k = Process.Start(psi)) { k.WaitForExit(5000); }
            }
            catch (Exception)
            {
                try { p.Kill(); } catch (Exception) { }
            }
        }

        private void RunWorker()
        {
            int exitCode = int.MinValue;
            string error = null;
            try
            {
                PrepareScript();
                SetProgress(10, "Launching PowerShell...");
                exitCode = RunPowerShell();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppendLog("\n[ERROR] " + ex.Message + "\n");
            }
            CleanupWorkDir();
            Ui(delegate { Finish(exitCode, error); });
        }

        private void Finish(int exitCode, string error)
        {
            running = false;
            proc = null;
            startBtn.Enabled = true;
            startBtn.Text = "Run Again";
            stopBtn.Enabled = false;
            inputBox.Enabled = false;
            sendBtn.Enabled = false;
            sendRBtn.Enabled = false;
            scriptBox.Enabled = true;
            browseBtn.Enabled = true;
            UpdateStartEnabled();

            if (error != null)
            {
                statusLabel.ForeColor = StatusBad;
                statusLabel.Text = "Failed: " + error;
            }
            else if (exitCode == 0)
            {
                currentProgress = 100;
                progress.Value = 100;
                statusLabel.ForeColor = StatusOk;
                statusLabel.Text = "100%  -  Finished (exit code 0). Review the output above for the result.";
            }
            else
            {
                statusLabel.ForeColor = StatusBad;
                statusLabel.Text = "Finished with exit code " + exitCode.ToString(CultureInfo.InvariantCulture) +
                                   " - review the output above.";
            }
            AppendLog("\n[PowerShell exited" +
                      (exitCode == int.MinValue ? "" : " with code " + exitCode.ToString(CultureInfo.InvariantCulture)) +
                      "]\n");
        }

        // ------------------------------------------------------------------ Script picking / copying

        private void PrepareScript()
        {
            // Unique, ACL-locked folder (Administrators + SYSTEM only), no spaces in the path.
            // The folder is locked down because an elevated process executes the script from it.
            workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MTRUpdateTool_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            DirectorySecurity sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(workDir, sec);

            // File name made safe (letters, digits . - _ only) so the path has no spaces or odd characters.
            string fileName = Path.GetFileName(selectedScript);
            StringBuilder safe = new StringBuilder();
            foreach (char c in fileName)
            {
                bool ok = (c < 128 && char.IsLetterOrDigit(c)) || c == '.' || c == '-' || c == '_';
                safe.Append(ok ? c : '_');
            }
            fileName = safe.ToString();
            if (!fileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) fileName = FallbackScriptFileName;
            scriptPath = Path.Combine(workDir, fileName);
            AppendLog("Copying script to a protected temp folder:\n  " + scriptPath + "\n");

            // Byte-for-byte copy: the script is Authenticode-signed and must stay unmodified.
            // It is run from the copy (not the original) so nobody can swap the file while it runs elevated.
            using (FileStream src = new FileStream(selectedScript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long total = src.Length;
                long done = 0;
                int lastPct = -1;
                byte[] buf = new byte[1024 * 1024];
                using (FileStream dst = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        done += n;
                        int pct = total > 0 ? (int)(done * 10 / total) : 0;
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            SetProgress(pct, "Copying script...");
                        }
                    }
                }
            }
            AppendLog("Copy complete.\n\n");
        }

        private static bool IsValidScript(string p)
        {
            try
            {
                return !string.IsNullOrEmpty(p) &&
                       p.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) &&
                       File.Exists(p);
            }
            catch (Exception) { return false; }
        }

        private void UpdateStartEnabled()
        {
            startBtn.Enabled = !running && IsValidScript(scriptBox.Text.Trim().Trim('"'));
        }

        private void SetScriptPath(string path)
        {
            if (running) return;
            if (!IsValidScript(path))
            {
                MessageBox.Show(this, "That is not a PowerShell script (.ps1) file.",
                    "Wrong file type", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            scriptBox.Text = path;
            FileInfo fi = new FileInfo(path);
            AppendLog("Selected script: " + path + "\n  " +
                      (fi.Length / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB, modified " +
                      fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "\n\n");
        }

        private void OpenDownloadPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo(DownloadPageUrl) { UseShellExecute = true });
                AppendLog("Opened the Microsoft download page in your browser (Step 1 has the script link).\n" +
                          "After downloading, drop the .ps1 here or click Browse...\n\n");
            }
            catch (Exception ex)
            {
                AppendLog("Could not open the browser: " + ex.Message + "\nOpen this page manually:\n  " +
                          DownloadPageUrl + "\n\n");
            }
        }

        private void BrowseForScript()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Select the MTR update script";
                dlg.Filter = "PowerShell script (*.ps1)|*.ps1|All files (*.*)|*.*";
                string prof = Environment.GetEnvironmentVariable("USERPROFILE");
                if (!string.IsNullOrEmpty(prof) && Directory.Exists(Path.Combine(prof, "Downloads")))
                    dlg.InitialDirectory = Path.Combine(prof, "Downloads");
                if (dlg.ShowDialog(this) == DialogResult.OK) SetScriptPath(dlg.FileName);
            }
        }

        private void OnDragEnterFiles(object sender, DragEventArgs e)
        {
            e.Effect = (!running && e.Data.GetDataPresent(DataFormats.FileDrop))
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDropFiles(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            string f = files[0];
            // Handle after the drop completes so a message box never blocks Explorer.
            BeginInvoke(new Action(delegate { SetScriptPath(f); }));
        }

        // Pre-fills the newest MTR-Update-*.ps1 found next to the EXE or in Downloads.
        private void AutoDetectScript()
        {
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            List<string> dirs = new List<string>();
            dirs.Add(Application.StartupPath);
            string prof = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrEmpty(prof)) dirs.Add(Path.Combine(prof, "Downloads"));
            foreach (string d in dirs)
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (string f in Directory.GetFiles(d, "MTR-Update-*.ps1"))
                    {
                        DateTime t = File.GetLastWriteTime(f);
                        if (t > bestTime) { bestTime = t; best = f; }
                    }
                }
                catch (Exception) { }
            }
            if (best != null)
            {
                SetScriptPath(best);
                AppendLog("(Auto-detected the newest MTR-Update-*.ps1. Pick another file if that is not the right one.)\n\n");
            }
        }

        private void CleanupWorkDir()
        {
            string d = workDir;
            if (string.IsNullOrEmpty(d)) return;
            try
            {
                if (Directory.Exists(d)) Directory.Delete(d, true);
                workDir = null;
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ PowerShell process

        private int RunPowerShell()
        {
            // Environment.SystemDirectory is System32 in a 64-bit process => 64-bit PowerShell,
            // which the script requires.
            string psExe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            string args = "-ExecutionPolicy Unrestricted \"" + scriptPath + "\"";

            ProcessStartInfo psi = new ProcessStartInfo(psExe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = workDir;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            try
            {
                Encoding oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                psi.StandardOutputEncoding = oem;
                psi.StandardErrorEncoding = oem;
            }
            catch (Exception) { }

            AppendLog("> powershell.exe " + args + "\n\n");

            Process p = new Process();
            p.StartInfo = psi;
            p.Start();
            proc = p;

            Thread outT = new Thread(delegate() { ReadStdOut(p); });
            Thread errT = new Thread(delegate() { ReadStdErr(p); });
            outT.IsBackground = true;
            errT.IsBackground = true;
            outT.Start();
            errT.Start();

            p.WaitForExit();
            outT.Join(5000);
            errT.Join(5000);
            int code = p.ExitCode;
            p.Dispose();
            return code;
        }

        private void ReadStdOut(Process p)
        {
            // Read raw chunks (not lines) so prompts that end without a newline are seen immediately.
            char[] buf = new char[4096];
            try
            {
                int n;
                while ((n = p.StandardOutput.Read(buf, 0, buf.Length)) > 0)
                    HandleOutput(new string(buf, 0, n));
            }
            catch (Exception) { }
        }

        private void ReadStdErr(Process p)
        {
            try
            {
                string line;
                while ((line = p.StandardError.ReadLine()) != null)
                    HandleErrorLine(line);
            }
            catch (Exception) { }
        }

        private void HandleOutput(string chunk)
        {
            string clean = chunk.Replace("\r", "");
            AppendLog(clean);

            bool answer = false;
            lock (parseLock)
            {
                lineBuf.Append(clean);
                string all = lineBuf.ToString();
                int nl;
                while ((nl = all.IndexOf('\n')) >= 0)
                {
                    ScanLine(all.Substring(0, nl));
                    all = all.Substring(nl + 1);
                }
                lineBuf.Length = 0;
                lineBuf.Append(all);

                // PowerShell's "[D] Do not run  [R] Run once  [S] Suspend" security prompt
                // (shown for scripts that carry a downloaded-from-internet mark).
                if (autoAnswer && all.IndexOf("Run once", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    answer = true;
                    lineBuf.Length = 0;
                }
            }
            if (answer) SendInput("R", true);
        }

        private static readonly Regex ClixmlError =
            new Regex("<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline);

        private void HandleErrorLine(string line)
        {
            // With redirected streams PowerShell may wrap stderr in CLIXML. Unwrap the error
            // text and drop progress records so the output stays readable.
            if (line.StartsWith("#< CLIXML", StringComparison.Ordinal)) return;
            if (line.IndexOf("<Objs", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("<Obj ", StringComparison.Ordinal) >= 0)
            {
                foreach (Match m in ClixmlError.Matches(line))
                {
                    string t = WebUtility.HtmlDecode(m.Groups[1].Value)
                        .Replace("_x000D_", "").Replace("_x000A_", "\n").Trim();
                    if (t.Length > 0) AppendLog("[ERR] " + t + "\n");
                }
                return;
            }
            AppendLog("[ERR] " + line + "\n");
        }

        private void ScanLine(string line)
        {
            foreach (Milestone m in Milestones)
            {
                if (line.IndexOf(m.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    SetProgress(m.Percent, m.Text);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------ Input / save

        private void SendTypedInput()
        {
            string text = inputBox.Text;
            inputBox.Clear();
            SendInput(text, false);
        }

        private void SendInput(string text, bool auto)
        {
            Process p = proc;
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    p.StandardInput.WriteLine(text);
                    p.StandardInput.Flush();
                    AppendLog((auto ? "\n[auto-sent: " : "\n[sent: ") + text + "]\n");
                }
            }
            catch (Exception ex)
            {
                AppendLog("\n[could not send input: " + ex.Message + "]\n");
            }
        }

        private void SaveOutput()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*";
                dlg.FileName = "MTR-Update-output-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string text;
                    lock (logLock) { text = fullLog.ToString(); }
                    File.WriteAllText(dlg.FileName, text.Replace("\n", "\r\n"), Encoding.UTF8);
                }
            }
        }

        private static bool IsAdmin()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
    }
}
