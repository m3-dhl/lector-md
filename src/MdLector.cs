using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: System.Reflection.AssemblyTitle("Lector MD")]
[assembly: System.Reflection.AssemblyProduct("Lector MD")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]

namespace MdLector
{
    static class Program
    {
        public static readonly string PipeName = "MdLector-" + Environment.UserName;

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        [STAThread]
        static void Main(string[] args)
        {
            bool first;
            var mutex = new Mutex(true, @"Local\MdLector-Instancia", out first);
            // Ya hay una ventana abierta: se le pasan los archivos para que los abra en pestañas.
            if (!first && SendToRunning(args)) return;

            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ReaderForm(args, first));
            GC.KeepAlive(mutex);
        }

        static bool SendToRunning(string[] args)
        {
            try
            {
                AllowSetForegroundWindow(-1);
                var paths = new List<string>();
                foreach (string a in args) { try { paths.Add(Path.GetFullPath(a)); } catch { } }
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(5000);
                    byte[] data = Encoding.UTF8.GetBytes(string.Join("\n", paths.ToArray()));
                    c.Write(data, 0, data.Length);
                    c.Flush();
                }
                return true;
            }
            catch { return false; }
        }
    }

    class Tab
    {
        public int Id;
        public string Path;
        public readonly List<string> History = new List<string>();
        public FileSystemWatcher Watcher;
        public string Host { get { return "t" + Id + ".mdlector"; } }
    }

    class ReaderForm : Form
    {
        const string AppHost = "app.mdlector";

        static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdLector");
        static readonly string WindowFile = Path.Combine(DataDir, "window.txt");

        readonly WebView2 web;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly Dictionary<int, Tab> tabs = new Dictionary<int, Tab>();
        readonly HashSet<int> dirty = new HashSet<int>();
        readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer { Interval = 250 };
        readonly List<string> pending = new List<string>();
        int nextId = 1;
        Tab active;
        bool ready;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        public ReaderForm(string[] args, bool serve)
        {
            pending.AddRange(args);
            Text = "Lector MD";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            MinimumSize = new Size(420, 320);
            RestoreWindow();

            bool dark = SystemIsDark();
            BackColor = dark ? Color.FromArgb(27, 29, 33) : Color.FromArgb(251, 251, 250);

            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(web);

            reloadTimer.Tick += delegate
            {
                reloadTimer.Stop();
                var ids = new List<int>(dirty);
                dirty.Clear();
                foreach (int id in ids)
                {
                    Tab t;
                    if (tabs.TryGetValue(id, out t) && t.Path != null) LoadIntoTab(t, t.Path, true, null, false);
                }
            };
            HandleCreated += delegate
            {
                SetDarkTitle(dark);
                if (serve) new Thread(PipeLoop) { IsBackground = true }.Start();
            };
            Load += async delegate { await Init(); };
            FormClosing += delegate { SaveWindow(); };
        }

        // ---------- instancia única ----------
        void PipeLoop()
        {
            while (true)
            {
                try
                {
                    using (var s = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1))
                    {
                        s.WaitForConnection();
                        string data;
                        using (var r = new StreamReader(s, Encoding.UTF8)) data = r.ReadToEnd();
                        BeginInvoke((Action)(() => ReceiveExternal(data)));
                    }
                }
                catch { Thread.Sleep(300); }
            }
        }

        void ReceiveExternal(string data)
        {
            foreach (string p in data.Split('\n'))
                if (p.Trim().Length > 0) OpenPath(p.Trim(), null, null);
            if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, 9);
            Activate();
            SetForegroundWindow(Handle);
        }

        // ---------- motor ----------
        async Task Init()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"));
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo iniciar el motor de visualización (WebView2).\n\n" + ex.Message,
                    "Lector MD", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            var c = web.CoreWebView2;
            c.Settings.AreDevToolsEnabled = false;
            c.Settings.IsStatusBarEnabled = false;
            c.Settings.IsZoomControlEnabled = true;
            c.Settings.AreBrowserAcceleratorKeysEnabled = true;
            c.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(BaseDir, "web"),
                CoreWebView2HostResourceAccessKind.Deny);

            c.NavigationStarting += OnNavigationStarting;
            c.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs e)
            {
                e.Handled = true;
                HandleLink(active, e.Uri, true);
            };
            c.WebMessageReceived += OnMessage;
            c.Navigate("https://" + AppHost + "/index.html");
        }

        void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri.StartsWith("https://" + AppHost + "/", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            if (e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                string local = new Uri(e.Uri).LocalPath;
                if (IsMarkdown(local)) OpenPath(local, null, null);
                return;
            }
            HandleLink(active, e.Uri, false);
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg;
            try { msg = json.Deserialize<Dictionary<string, object>>(e.TryGetWebMessageAsString()); }
            catch { return; }

            Tab tab = null;
            int id;
            if (int.TryParse(Str(msg, "tab"), out id)) tabs.TryGetValue(id, out tab);

            switch (Str(msg, "type"))
            {
                case "ready":
                    ready = true;
                    foreach (string p in pending) OpenPath(p, null, null);
                    pending.Clear();
                    break;
                case "open": ShowOpenDialog(); break;
                case "activate":
                    active = tab;
                    Text = tab != null && tab.Path != null ? Path.GetFileName(tab.Path) + " — Lector MD" : "Lector MD";
                    break;
                case "closeTab":
                    if (tab != null) CloseTab(tab);
                    break;
                case "reload":
                    if (tab != null && tab.Path != null) LoadIntoTab(tab, tab.Path, true, null, false);
                    break;
                case "back":
                    if (tab != null && tab.History.Count > 0)
                    {
                        string prev = tab.History[tab.History.Count - 1];
                        tab.History.RemoveAt(tab.History.Count - 1);
                        if (File.Exists(prev)) LoadIntoTab(tab, prev, false, null, true);
                    }
                    break;
                case "link": HandleLink(tab, Str(msg, "href"), Str(msg, "newTab") == "True"); break;
                case "close": Close(); break;
                case "theme": SetDarkTitle(Str(msg, "dark") == "True"); break;
                case "folder":
                    if (tab != null && tab.Path != null) Process.Start("explorer.exe", "/select,\"" + tab.Path + "\"");
                    break;
                case "listDir": SendDir(Str(msg, "path"), Str(msg, "req")); break;
                case "openFile": OpenPath(Str(msg, "path"), null, null); break;
                case "explore":
                    string ex = Str(msg, "path");
                    if (Directory.Exists(ex)) Process.Start("explorer.exe", "\"" + ex + "\"");
                    break;
            }
        }

        // ---------- árbol de carpetas ----------
        void SendDir(string path, string req)
        {
            var data = new Dictionary<string, object>();
            data["type"] = "dir";
            data["req"] = req;
            var dirs = new List<object>();
            var files = new List<object>();
            try
            {
                if (string.IsNullOrEmpty(path))
                    path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                path = Path.GetFullPath(path);
                var info = new DirectoryInfo(path);
                data["parent"] = info.Parent != null ? info.Parent.FullName : null;
                data["name"] = info.Parent != null ? info.Name : info.FullName;

                var subdirs = info.GetDirectories();
                Array.Sort(subdirs, (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
                foreach (var d in subdirs)
                {
                    if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || d.Name.StartsWith(".")) continue;
                    dirs.Add(new Dictionary<string, object> { { "name", d.Name }, { "path", d.FullName } });
                }
                var fs = info.GetFiles();
                Array.Sort(fs, (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
                foreach (var f in fs)
                {
                    if ((f.Attributes & FileAttributes.Hidden) != 0 || !IsMarkdown(f.FullName) || f.Extension.ToLowerInvariant() == ".txt") continue;
                    files.Add(new Dictionary<string, object> { { "name", f.Name }, { "path", f.FullName } });
                }
            }
            catch (Exception e) { data["error"] = e.Message; }
            data["path"] = path;
            data["dirs"] = dirs;
            data["files"] = files;
            web.CoreWebView2.PostWebMessageAsJson(json.Serialize(data));
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        void ShowOpenDialog()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Abrir documentos Markdown";
                dlg.Multiselect = true;
                dlg.Filter = "Markdown (*.md;*.markdown;*.mdown;*.mkd)|*.md;*.markdown;*.mdown;*.mkd;*.mkdn|Texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*";
                if (active != null && active.Path != null) dlg.InitialDirectory = Path.GetDirectoryName(active.Path);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    foreach (string f in dlg.FileNames) OpenPath(f, null, null);
            }
        }

        // target == null → pestaña nueva (o la que ya tenga ese archivo)
        void OpenPath(string path, Tab target, string hash)
        {
            if (!ready) { pending.Add(path); return; }
            try { path = Path.GetFullPath(path); } catch { }
            if (!File.Exists(path))
            {
                MessageBox.Show(this, "No se encuentra el archivo:\n" + path, "Lector MD",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (target == null)
            {
                foreach (Tab t in tabs.Values)
                    if (SamePath(t.Path, path)) { LoadIntoTab(t, path, true, hash, true); return; }
                target = new Tab { Id = nextId++ };
                tabs[target.Id] = target;
            }
            else if (target.Path != null && !SamePath(target.Path, path))
            {
                target.History.Add(target.Path);
            }
            LoadIntoTab(target, path, false, hash, true);
        }

        void LoadIntoTab(Tab tab, string path, bool keepScroll, string hash, bool activate)
        {
            string text;
            try { text = ReadText(path); }
            catch (IOException) { if (keepScroll) { dirty.Add(tab.Id); reloadTimer.Start(); } return; }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo leer el archivo:\n" + ex.Message, "Lector MD",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var c = web.CoreWebView2;
            if (!SamePath(tab.Path, path))
            {
                try { c.ClearVirtualHostNameToFolderMapping(tab.Host); } catch { }
                c.SetVirtualHostNameToFolderMapping(tab.Host, Path.GetDirectoryName(path),
                    CoreWebView2HostResourceAccessKind.Allow);
                tab.Path = path;
                Watch(tab);
            }

            var data = new Dictionary<string, object>();
            data["type"] = "doc";
            data["tab"] = tab.Id;
            data["host"] = tab.Host;
            data["name"] = Path.GetFileName(path);
            data["path"] = path;
            data["dir"] = Path.GetDirectoryName(path);
            data["text"] = text;
            data["keepScroll"] = keepScroll;
            data["canBack"] = tab.History.Count > 0;
            data["hash"] = hash ?? "";
            data["activate"] = activate;
            c.PostWebMessageAsJson(json.Serialize(data));
        }

        void CloseTab(Tab tab)
        {
            tabs.Remove(tab.Id);
            dirty.Remove(tab.Id);
            if (tab.Watcher != null) { tab.Watcher.EnableRaisingEvents = false; tab.Watcher.Dispose(); }
            try { web.CoreWebView2.ClearVirtualHostNameToFolderMapping(tab.Host); } catch { }
            if (active == tab) active = null;
        }

        static bool SamePath(string a, string b)
        {
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static string ReadText(string path)
        {
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            int start = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            try { return new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
        }

        void Watch(Tab tab)
        {
            if (tab.Watcher != null) { tab.Watcher.EnableRaisingEvents = false; tab.Watcher.Dispose(); tab.Watcher = null; }
            try
            {
                var w = new FileSystemWatcher(Path.GetDirectoryName(tab.Path), Path.GetFileName(tab.Path));
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                w.SynchronizingObject = this;
                int id = tab.Id;
                FileSystemEventHandler h = delegate { dirty.Add(id); reloadTimer.Stop(); reloadTimer.Start(); };
                w.Changed += h;
                w.Created += h;
                w.Renamed += delegate { dirty.Add(id); reloadTimer.Stop(); reloadTimer.Start(); };
                w.EnableRaisingEvents = true;
                tab.Watcher = w;
            }
            catch { }
        }

        void HandleLink(Tab tab, string href, bool newTab)
        {
            if (string.IsNullOrEmpty(href)) return;
            Uri uri;
            if (!Uri.TryCreate(href, UriKind.Absolute, out uri)) return;

            if (tab != null && tab.Path != null && uri.Host.Equals(tab.Host, StringComparison.OrdinalIgnoreCase))
            {
                string rel = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/', '\\');
                string local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(tab.Path), rel));
                string hash = uri.Fragment.Length > 1 ? Uri.UnescapeDataString(uri.Fragment.Substring(1)) : null;
                if (IsMarkdown(local)) { OpenPath(local, newTab ? null : tab, hash); return; }
                if (File.Exists(local) || Directory.Exists(local)) ShellOpen(local);
                return;
            }
            if (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "mailto")
                ShellOpen(uri.AbsoluteUri);
        }

        static void ShellOpen(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }

        static bool IsMarkdown(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ext == ".md" || ext == ".markdown" || ext == ".mdown" || ext == ".mkd" || ext == ".mkdn" || ext == ".txt";
        }

        // ---------- ventana ----------
        static bool SystemIsDark()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return v is int && (int)v == 0;
            }
            catch { return false; }
        }

        void SetDarkTitle(bool dark)
        {
            if (!IsHandleCreated) return;
            int v = dark ? 1 : 0;
            try { DwmSetWindowAttribute(Handle, 20, ref v, 4); } catch { }
            BackColor = dark ? Color.FromArgb(27, 29, 33) : Color.FromArgb(251, 251, 250);
            if (web != null) web.DefaultBackgroundColor = BackColor;
        }

        void RestoreWindow()
        {
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1000, 820);
            try
            {
                if (!File.Exists(WindowFile)) return;
                string[] p = File.ReadAllText(WindowFile).Split(',');
                var r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                bool visible = false;
                foreach (Screen s in Screen.AllScreens) if (s.WorkingArea.IntersectsWith(r)) visible = true;
                if (!visible || r.Width < 300 || r.Height < 200) return;
                StartPosition = FormStartPosition.Manual;
                Bounds = r;
                if (p.Length > 4 && p[4] == "1") WindowState = FormWindowState.Maximized;
            }
            catch { }
        }

        void SaveWindow()
        {
            try
            {
                Rectangle r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                File.WriteAllText(WindowFile, r.X + "," + r.Y + "," + r.Width + "," + r.Height + "," +
                    (WindowState == FormWindowState.Maximized ? "1" : "0"));
            }
            catch { }
        }
    }
}

