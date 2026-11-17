using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: System.Reflection.AssemblyTitle("Lector MD")]
[assembly: System.Reflection.AssemblyProduct("Lector MD")]
[assembly: System.Reflection.AssemblyVersion("1.4.0.0")]

namespace MdLector
{
    static class Program
    {
        // Una tubería por usuario y sesión de Windows.
        public static readonly string PipeName = "MdLector-" + WindowsIdentity.GetCurrent().User.Value + "-" +
            Process.GetCurrentProcess().SessionId;

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

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
                var paths = new List<string>();
                foreach (string a in args) { try { paths.Add(Path.GetFullPath(a)); } catch { } }
                // Identification: el servidor no puede suplantarnos.
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None,
                    TokenImpersonationLevel.Identification))
                {
                    c.Connect(5000);
                    // Solo se envía a otra copia de este mismo programa (evita que otro proceso ocupe la tubería).
                    uint pid;
                    if (!GetNamedPipeServerProcessId(c.SafePipeHandle, out pid)) return false;
                    using (var server = Process.GetProcessById((int)pid))
                    {
                        if (!string.Equals(server.MainModule.FileName, Application.ExecutablePath,
                            StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    AllowSetForegroundWindow((int)pid);
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
        public string Root;
        public int Retries;
        public readonly List<string> History = new List<string>();
        public FileSystemWatcher Watcher;
        public Encoding Enc;          // codificación con la que se leyó (se respeta al guardar)
        public DateTime StampTime;    // estado del archivo en disco la última vez que se leyó o guardó
        public long StampLen;
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
        readonly Task<CoreWebView2Environment> envTask;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly Dictionary<int, Tab> tabs = new Dictionary<int, Tab>();
        readonly HashSet<int> dirty = new HashSet<int>();
        readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer { Interval = 250 };
        readonly List<string> pending = new List<string>();
        readonly HashSet<int> unsaved = new HashSet<int>(); // pestañas con cambios sin guardar
        int nextId = 1;
        Tab active;
        bool ready, closeConfirmed;

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

            // El motor arranca ya, en paralelo con la creación de la ventana.
            envTask = CreateEnvironment();

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
            FormClosing += OnClosing;
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!closeConfirmed && unsaved.Count > 0 && e.CloseReason != CloseReason.WindowsShutDown)
            {
                var r = MessageBox.Show(this, unsaved.Count == 1
                        ? "Hay un documento con cambios sin guardar.\n\n¿Guardarlo antes de salir?"
                        : "Hay " + unsaved.Count + " documentos con cambios sin guardar.\n\n¿Guardarlos antes de salir?",
                    "Lector MD", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes)
                {
                    // Se piden los textos a la página; al terminar de guardar todo, se cierra.
                    e.Cancel = true;
                    Post(new Dictionary<string, object> { { "type", "saveAll" } });
                    return;
                }
            }
            SaveWindow();
        }

        void Post(Dictionary<string, object> data)
        {
            if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json.Serialize(data));
        }

        // ---------- instancia única ----------
        void PipeLoop()
        {
            // Solo el usuario actual puede conectarse a la tubería.
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                PipeAccessRights.FullControl, AccessControlType.Allow));
            const int MaxMessage = 64 * 1024;
            int failures = 0;
            while (failures < 20)
            {
                try
                {
                    using (var s = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security))
                    {
                        s.WaitForConnection();
                        var buf = new byte[MaxMessage];
                        int total = 0, n;
                        while (total < MaxMessage && (n = s.Read(buf, total, MaxMessage - total)) > 0) total += n;
                        string data = Encoding.UTF8.GetString(buf, 0, total);
                        BeginInvoke((Action)(() => ReceiveExternal(data)));
                        failures = 0;
                    }
                }
                catch { failures++; Thread.Sleep(500); }
            }
        }

        void ReceiveExternal(string data)
        {
            foreach (string p in data.Split('\n'))
                if (p.Trim().Length > 0 && IsMarkdown(p.Trim())) OpenPath(p.Trim(), null, null);
            if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, 9);
            Activate();
            SetForegroundWindow(Handle);
        }

        // ---------- motor ----------
        // Menos procesos y memoria; sin servicios de red que un lector local no necesita.
        const string BrowserArgs =
            "--disable-gpu --in-process-gpu --renderer-process-limit=1 " +
            "--enable-features=NetworkServiceInProcess2 " +
            "--disable-background-networking --disable-component-update " +
            "--auth-server-allowlist=_ " + // nunca enviar credenciales de Windows (NTLM/Kerberos) a ningún servidor
            "--disable-features=msSmartScreenProtection,AutofillServerCommunication,OptimizationHints,Translate,SpareRendererForSitePerProcess";

        static Task<CoreWebView2Environment> CreateEnvironment()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                return CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"),
                    new CoreWebView2EnvironmentOptions(BrowserArgs));
            }
            catch (Exception ex)
            {
                var tcs = new TaskCompletionSource<CoreWebView2Environment>();
                tcs.SetException(ex);
                return tcs.Task;
            }
        }

        async Task Init()
        {
            try
            {
                await web.EnsureCoreWebView2Async(await envTask);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo iniciar el motor de visualización (WebView2).\n\n" + ex.Message,
                    "Lector MD", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            var c = web.CoreWebView2;
            var st = c.Settings;
            st.AreDevToolsEnabled = false;
            st.IsStatusBarEnabled = false;
            st.IsZoomControlEnabled = true;
            st.AreBrowserAcceleratorKeysEnabled = true;
            st.AreHostObjectsAllowed = false;
            st.AreDefaultScriptDialogsEnabled = false;
            st.IsPasswordAutosaveEnabled = false;
            st.IsGeneralAutofillEnabled = false;
            st.IsReputationCheckingRequired = false; // solo contenido local; los enlaces web van al navegador
            c.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(BaseDir, "web"),
                CoreWebView2HostResourceAccessKind.Deny);

            // Nada de descargas, permisos (cámara, ubicación…) ni ventanas nuevas dentro de la app.
            c.DownloadStarting += delegate(object s, CoreWebView2DownloadStartingEventArgs e) { e.Cancel = true; };
            c.PermissionRequested += delegate(object s, CoreWebView2PermissionRequestedEventArgs e)
            {
                e.State = CoreWebView2PermissionState.Deny;
            };
            Resize += delegate
            {
                // Minimizada: el motor libera memoria.
                try
                {
                    c.MemoryUsageTargetLevel = WindowState == FormWindowState.Minimized
                        ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
                }
                catch { }
            };

            c.NavigationStarting += OnNavigationStarting;
            c.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs e)
            {
                e.Handled = true;
                HandleLink(active, e.Uri, true);
            };
            c.WebMessageReceived += OnMessage;
            // Imágenes de los documentos: las sirve la app (no hay carpeta mapeada al motor web).
            // Filtro "All": cualquier petición a *.mdlector se responde aquí (sin tocar la red ni el DNS);
            // solo las imágenes reciben contenido, el resto 404.
            c.AddWebResourceRequestedFilter("https://*.mdlector/*", CoreWebView2WebResourceContext.All);
            c.WebResourceRequested += OnImageRequested;
            c.Navigate(AppPage);
        }

        const string AppPage = "https://" + AppHost + "/index.html";
        bool pageLoaded;

        static readonly Dictionary<string, string> ImageTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".gif", "image/gif" },
            { ".webp", "image/webp" }, { ".bmp", "image/bmp" }, { ".svg", "image/svg+xml" }, { ".ico", "image/x-icon" },
            { ".avif", "image/avif" }
        };
        const long MaxImageBytes = 64L * 1024 * 1024;

        void OnImageRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            Uri uri;
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri) ||
                uri.Host.Equals(AppHost, StringComparison.OrdinalIgnoreCase)) return;

            var env = web.CoreWebView2.Environment;
            Tab tab = null;
            foreach (Tab t in tabs.Values)
                if (uri.Host.Equals(t.Host, StringComparison.OrdinalIgnoreCase)) { tab = t; break; }

            string root = tab != null ? tab.Root : null;
            string urlPath = uri.AbsolutePath;
            if (root == null) { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }

            // El disco (o una unidad de red lenta) se consulta fuera del hilo de la interfaz.
            var deferral = e.GetDeferral();
            Task.Run(() =>
            {
                string type;
                string local = ResolveInRoot(root, urlPath);
                if (local == null || !ImageTypes.TryGetValue(Path.GetExtension(local) ?? "", out type)) return null;
                try
                {
                    var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        64 * 1024, FileOptions.SequentialScan);
                    if (fs.Length <= MaxImageBytes) return Tuple.Create((Stream)fs, type);
                    fs.Dispose();
                }
                catch { }
                return null;
            }).ContinueWith(t =>
            {
                try
                {
                    var r = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    e.Response = r != null
                        ? env.CreateWebResourceResponse(r.Item1, 200, "OK",
                            "Content-Type: " + r.Item2 + "\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-cache")
                        : env.CreateWebResourceResponse(null, 404, "Not Found", "");
                }
                catch { }
                finally { deferral.Complete(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // La única navegación permitida es la carga inicial de la interfaz.
            if (string.Equals(e.Uri, AppPage, StringComparison.OrdinalIgnoreCase) && !pageLoaded)
            {
                pageLoaded = true;
                return;
            }
            e.Cancel = true;
            if (e.Uri.StartsWith("https://" + AppHost + "/", StringComparison.OrdinalIgnoreCase)) return;
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
            if (!string.Equals(e.Source, AppPage, StringComparison.OrdinalIgnoreCase)) return;
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
                    UpdateTitle();
                    break;
                case "closeTab":
                    if (tab != null) CloseTab(tab);
                    break;
                case "reload":
                    if (tab != null && tab.Path != null) LoadIntoTab(tab, tab.Path, true, null, false);
                    break;
                case "back":
                    if (tab != null && tab.History.Count > 0 && !unsaved.Contains(tab.Id))
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
                    if (tab != null && tab.Path != null) Explorer("/select,\"" + tab.Path + "\"");
                    break;
                case "listDir": SendDir(Str(msg, "path"), Str(msg, "req")); break;
                case "openFile":
                    string of = Str(msg, "path");
                    if (IsMarkdown(of)) OpenPath(of, null, null);
                    break;
                case "explore":
                    string ex = Str(msg, "path");
                    if (Directory.Exists(ex)) Explorer("\"" + ex + "\"");
                    break;
                case "dirty":
                    if (tab == null) break;
                    if (Str(msg, "dirty") == "True") unsaved.Add(tab.Id); else unsaved.Remove(tab.Id);
                    if (tab == active) UpdateTitle();
                    break;
                case "save": SaveTab(tab, Str(msg, "text"), Str(msg, "then")); break;
                case "askClose":
                    if (tab == null || tab.Path == null) break;
                    var answer = MessageBox.Show(this, "¿Guardar los cambios en «" + Path.GetFileName(tab.Path) + "»?",
                        "Lector MD", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (answer == DialogResult.Yes)
                        Post(new Dictionary<string, object> { { "type", "saveTab" }, { "tab", tab.Id }, { "then", "closeTab" } });
                    else if (answer == DialogResult.No)
                        Post(new Dictionary<string, object> { { "type", "closeTabNow" }, { "tab", tab.Id } });
                    break;
            }
        }

        void UpdateTitle()
        {
            Text = active != null && active.Path != null
                ? (unsaved.Contains(active.Id) ? "● " : "") + Path.GetFileName(active.Path) + " — Lector MD"
                : "Lector MD";
        }

        // ---------- edición ----------
        // Solo se escribe en el archivo de la propia pestaña: la página nunca elige la ruta.
        void SaveTab(Tab tab, string text, string then)
        {
            if (tab == null || tab.Path == null) return;
            var res = new Dictionary<string, object> { { "type", "saved" }, { "tab", tab.Id }, { "ok", false } };
            try
            {
                if (text.Length > MaxFileBytes) throw new IOException("El documento supera 8 MB.");
                var fi = new FileInfo(tab.Path);
                if (fi.Exists && (fi.LastWriteTimeUtc != tab.StampTime || fi.Length != tab.StampLen) &&
                    MessageBox.Show(this, "«" + fi.Name + "» ha cambiado en el disco desde que lo abriste.\n\n" +
                        "¿Sobrescribirlo con tu versión?", "Lector MD", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    res["cancelled"] = true;
                    Post(res);
                    return;
                }
                WriteAtomic(tab.Path, Encode(tab, text));
                SetStamp(tab);
                unsaved.Remove(tab.Id);
                res["ok"] = true;
            }
            catch (Exception ex) { res["error"] = ex.Message; }
            Post(res);
            if (tab == active) UpdateTitle();
            if (!(bool)res["ok"]) return;
            if (then == "closeTab") Post(new Dictionary<string, object> { { "type", "closeTabNow" }, { "tab", tab.Id } });
            else if (then == "quit" && unsaved.Count == 0) { closeConfirmed = true; BeginInvoke((Action)Close); }
        }

        // Misma codificación que el original; si un carácter no cabe en ella (ANSI), se pasa a UTF-8.
        static byte[] Encode(Tab tab, string text)
        {
            Encoding enc = tab.Enc ?? new UTF8Encoding(false);
            byte[] body;
            try
            {
                var strict = (Encoding)enc.Clone();
                strict.EncoderFallback = EncoderFallback.ExceptionFallback;
                body = strict.GetBytes(text);
            }
            catch (EncoderFallbackException)
            {
                enc = new UTF8Encoding(false);
                body = enc.GetBytes(text);
            }
            tab.Enc = enc;
            byte[] pre = enc.GetPreamble();
            if (pre.Length == 0) return body;
            var all = new byte[pre.Length + body.Length];
            Buffer.BlockCopy(pre, 0, all, 0, pre.Length);
            Buffer.BlockCopy(body, 0, all, pre.Length, body.Length);
            return all;
        }

        // Se escribe en un temporal y se sustituye: un corte a mitad no deja el documento a medias.
        static void WriteAtomic(string path, byte[] bytes)
        {
            string tmp = Path.Combine(Path.GetDirectoryName(path),
                "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null, true); else File.Move(tmp, path);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                // Algunas unidades (red, FAT, carpetas sin permiso de creación) no lo admiten: escritura directa.
                File.WriteAllBytes(path, bytes);
            }
        }

        static void SetStamp(Tab tab)
        {
            try { var fi = new FileInfo(tab.Path); tab.StampTime = fi.LastWriteTimeUtc; tab.StampLen = fi.Length; }
            catch { }
        }

        // ---------- árbol de carpetas ----------
        // Se lista en segundo plano: una carpeta lenta o enorme no congela la ventana.
        void SendDir(string path, string req)
        {
            Task.Run(() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(BuildDir(path, req)))
                .ContinueWith(t =>
                {
                    if (t.Status != TaskStatus.RanToCompletion || IsDisposed) return;
                    try { BeginInvoke((Action)(() => { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(t.Result); })); }
                    catch (InvalidOperationException) { }
                });
        }

        static Dictionary<string, object> BuildDir(string path, string req)
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

                // Límites: se muestran como mucho 3000 elementos y se examinan como mucho 50000.
                const int MaxEntries = 3000, MaxScanned = 50000;
                int scanned = 0;
                var subdirs = new List<DirectoryInfo>();
                foreach (var d in info.EnumerateDirectories())
                {
                    if (++scanned > MaxScanned) break;
                    if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || d.Name.StartsWith(".")) continue;
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0 && IsSymlink(d.FullName)) continue;
                    subdirs.Add(d);
                    if (subdirs.Count >= MaxEntries) break;
                }
                subdirs.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
                foreach (var d in subdirs)
                    dirs.Add(new Dictionary<string, object> { { "name", d.Name }, { "path", d.FullName } });

                var mds = new List<FileInfo>();
                foreach (var f in info.EnumerateFiles())
                {
                    if (++scanned > MaxScanned) break;
                    if ((f.Attributes & FileAttributes.Hidden) != 0 || !IsMarkdown(f.Name) ||
                        string.Equals(f.Extension, ".txt", StringComparison.OrdinalIgnoreCase)) continue;
                    mds.Add(f);
                    if (mds.Count >= MaxEntries) break;
                }
                mds.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
                foreach (var f in mds)
                    files.Add(new Dictionary<string, object> { { "name", f.Name }, { "path", f.FullName } });
            }
            catch (Exception e) { data["error"] = e.Message; }
            data["path"] = path;
            data["dirs"] = dirs;
            data["files"] = files;
            return data;
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
                // Con cambios sin guardar, el enlace se abre en otra pestaña para no perderlos.
                if (unsaved.Contains(target.Id)) { OpenPath(path, null, hash); return; }
                target.History.Add(target.Path);
            }
            LoadIntoTab(target, path, false, hash, true);
        }

        void LoadIntoTab(Tab tab, string path, bool keepScroll, string hash, bool activate)
        {
            string text;
            Encoding enc;
            try { text = ReadText(path, out enc); tab.Retries = 0; }
            catch (IOException)
            {
                // Bloqueado mientras otro programa lo guarda: unos pocos reintentos y se deja.
                if (keepScroll && ++tab.Retries <= 8) { dirty.Add(tab.Id); reloadTimer.Start(); }
                return;
            }
            catch (Exception ex)
            {
                // Recargas automáticas: en silencio. Aperturas: avisar.
                if (!keepScroll)
                    MessageBox.Show(this, "No se pudo leer el archivo:\n" + ex.Message, "Lector MD",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var c = web.CoreWebView2;
            string dir = Path.GetDirectoryName(path);
            // URL virtual de la pestaña = ruta desde la raíz de la unidad (o recurso compartido), para que
            // funcionen rutas relativas como ../img.png. Solo se sirven imágenes (OnImageRequested).
            string root = Path.GetPathRoot(path);
            tab.Root = root;
            if (!SamePath(tab.Path, path))
            {
                tab.Path = path;
                Watch(tab);
            }
            tab.Enc = enc;
            SetStamp(tab);
            var segs = new List<string>();
            foreach (string s in dir.Substring(root.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                segs.Add(Uri.EscapeDataString(s));
            string baseUrl = "https://" + tab.Host + "/" + string.Join("/", segs.ToArray()) + (segs.Count > 0 ? "/" : "");

            var data = new Dictionary<string, object>();
            data["type"] = "doc";
            data["tab"] = tab.Id;
            data["base"] = baseUrl;
            data["name"] = Path.GetFileName(path);
            data["path"] = path;
            data["dir"] = dir;
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
            unsaved.Remove(tab.Id);
            if (tab.Watcher != null) { tab.Watcher.EnableRaisingEvents = false; tab.Watcher.Dispose(); }
            if (active == tab) active = null;
        }

        static bool SamePath(string a, string b)
        {
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        const long MaxFileBytes = 8L * 1024 * 1024;

        static string ReadText(string path, out Encoding enc)
        {
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length > MaxFileBytes)
                    throw new InvalidDataException("El archivo supera 8 MB; no parece un documento Markdown.");
                bytes = new byte[fs.Length];
                int read = 0, n;
                while (read < bytes.Length && (n = fs.Read(bytes, read, bytes.Length - read)) > 0) read += n;
                if (read < bytes.Length) Array.Resize(ref bytes, read);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { enc = Encoding.Unicode; return enc.GetString(bytes, 2, bytes.Length - 2); }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { enc = Encoding.BigEndianUnicode; return enc.GetString(bytes, 2, bytes.Length - 2); }
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int start = bom ? 3 : 0;
            enc = new UTF8Encoding(bom);
            try { return new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start); }
            catch (DecoderFallbackException) { enc = Encoding.Default; return enc.GetString(bytes); }
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

            if (tab != null && tab.Root != null && uri.Host.Equals(tab.Host, StringComparison.OrdinalIgnoreCase))
            {
                string local = ResolveInRoot(tab.Root, uri.AbsolutePath);
                if (local == null) return;
                string hash = uri.Fragment.Length > 1 ? Uri.UnescapeDataString(uri.Fragment.Substring(1)) : null;
                if (IsMarkdown(local)) { OpenPath(local, newTab ? null : tab, hash); return; }
                if (Directory.Exists(local)) { Explorer("\"" + local + "\""); return; }
                if (!File.Exists(local)) return;
                // Un enlace nunca ejecuta nada: solo se abren tipos inofensivos; el resto se muestra en el Explorador.
                if (SafeToOpen(local)) ShellOpen(local); else Explorer("/select,\"" + local + "\"");
                return;
            }
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto)
                ShellOpen(uri.AbsoluteUri);
        }

        // Convierte la ruta de la URL virtual en ruta local, sin permitir salir de la raíz
        // (ni saltar a otra unidad o a un recurso de red \\servidor).
        static string ResolveInRoot(string root, string urlPath)
        {
            try
            {
                string rel = Uri.UnescapeDataString(urlPath).TrimStart('/').Replace('/', '\\');
                if (rel.IndexOf(':') >= 0 || rel.StartsWith("\\")) return null;
                string full = Path.GetFullPath(Path.Combine(root, rel));
                string prefix = root.TrimEnd('\\') + "\\";
                if (!(full + "\\").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
                return HasSymlink(full, prefix) ? null : full;
            }
            catch { return null; }
        }

        // ¿Algún tramo de la ruta (bajo la raíz) es un enlace simbólico? Podría apuntar a \\servidor.
        // Las uniones (junctions) y los marcadores de OneDrive se permiten: no pueden saltar a la red.
        const uint ReparseTagSymlink = 0xA000000C;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFile(string name, out WIN32_FIND_DATA data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr h);

        // Se recorre de la raíz hacia la hoja: cada consulta solo atraviesa tramos ya comprobados,
        // así que la propia comprobación nunca sigue un enlace hacia la red. Ante cualquier error: inseguro.
        static bool HasSymlink(string full, string rootPrefix)
        {
            string cur = rootPrefix.TrimEnd('\\');
            foreach (string part in full.Substring(rootPrefix.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.IndexOfAny(new[] { '*', '?' }) >= 0) return true; // FindFirstFile los trataría como comodines
                cur += "\\" + part;
                WIN32_FIND_DATA d;
                IntPtr h = FindFirstFile(cur, out d);
                if (h == new IntPtr(-1)) return true;
                FindClose(h);
                if ((d.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0 && d.dwReserved0 == ReparseTagSymlink)
                    return true;
            }
            return false;
        }

        static bool IsSymlink(string path)
        {
            WIN32_FIND_DATA d;
            IntPtr h = FindFirstFile(path.TrimEnd('\\'), out d);
            if (h == new IntPtr(-1)) return true; // ante la duda, no se sigue
            FindClose(h);
            return d.dwReserved0 == ReparseTagSymlink;
        }

        static readonly HashSet<string> SafeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

        static bool SafeToOpen(string path) { return SafeExtensions.Contains(Path.GetExtension(path) ?? ""); }

        static void Explorer(string args)
        {
            try { Process.Start("explorer.exe", args); } catch { }
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

