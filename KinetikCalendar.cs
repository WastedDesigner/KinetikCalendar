// Kinetik Calendar: a desktop agenda widget for Google, Outlook and Apple (iCloud) calendars, in the style of Kinetik's widget.
// Each calendar is read from its private iCal (.ics) link, so there's no sign-in, no API keys and nothing to install.
// Copyright © 2026 Akila Sella Hennedige. GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Kinetik Calendar")]
[assembly: AssemblyProduct("Kinetik Calendar")]
[assembly: AssemblyCopyright("Copyright © 2026 Akila Sella Hennedige")]
[assembly: AssemblyVersion("2.0.0.0")]

static class Program
{
    public const string Version = "2.0.0";
    public static bool Demo; // --demo: made-up calendars, nothing read from or saved to this PC
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        Demo = args.Contains("--demo");
        using (new Mutex(true, Demo ? @"Local\KinetikCalendarDemo" : @"Local\KinetikCalendar", out created))
        {
            if (!created) return; // already running
            SetProcessDPIAware();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            ServicePointManager.DefaultConnectionLimit = 4;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ToolStripManager.Renderer = new DarkMenu();
            Backup.KeyName = "KinetikCalendar"; Backup.AppTitle = "Kinetik Calendar";
            if (!Demo) Backup.OfferRestore();
            var app = new CalApp(args.Contains("--settings"));
            using (new KinetikSuite.Shutdown("KinetikCalendar" + (Demo ? "Demo" : ""), app.Quit))
                Application.Run(app);
        }
        Backup.AfterExit();
    }
}

// Hands memory the app no longer needs back to Windows: after startup, after each sync and after closing Settings.
static class Mem
{
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);

    public static void Trim() { KinetikSuite.MemoryPolicy.Measure("KinetikCalendar", TrimCore); }

    static void TrimCore()
    {
        try
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            SetProcessWorkingSetSize(new IntPtr(-1), new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }
}

// ======================================================================= Settings
class Feed
{
    public string Name = "", Url = "", Kind = "Google";
    public Color Color = DefaultColor("Google");
    public bool On = true;

    // Live state, not saved
    public volatile List<Ics.VEvent> Parsed;
    public string Status = "Not synced yet";
    public bool Failed, Changed;

    public static readonly string[] Kinds = { "Google", "Outlook", "Apple", "Other" };
    public static readonly string[] KindNames = { "Google Calendar", "Outlook / Microsoft 365", "Apple iCloud", "Other (.ics link)" };

    public static readonly Color[] Palette =
    {
        Color.FromArgb(96, 150, 250), Color.FromArgb(40, 190, 200), Color.FromArgb(250, 104, 96), Color.FromArgb(180, 135, 255),
        Color.FromArgb(255, 170, 60), Color.FromArgb(120, 205, 120), Color.FromArgb(255, 120, 190), Color.FromArgb(90, 200, 255),
        Color.FromArgb(240, 210, 80), Color.FromArgb(255, 140, 100), Color.FromArgb(130, 110, 250), Color.FromArgb(165, 170, 185),
    };

    public static string KindTitle(string kind)
    {
        int i = Array.IndexOf(Kinds, kind);
        return i < 0 ? kind : KindNames[i];
    }

    public static Color DefaultColor(string kind)
    {
        switch (kind)
        {
            case "Google": return Palette[0];
            case "Outlook": return Palette[1];
            case "Apple": return Palette[2];
            default: return Palette[3];
        }
    }

    // Guesses the provider from a pasted link.
    public static string Detect(string url)
    {
        url = (url ?? "").ToLowerInvariant();
        if (url.Contains("google.com")) return "Google";
        if (url.Contains("outlook.") || url.Contains("office365.com") || url.Contains("live.com")) return "Outlook";
        if (url.Contains("icloud.com")) return "Apple";
        return null;
    }

    // The provider's own web calendar, opened when an event is clicked.
    public static string WebUrl(Feed f, DateTime day)
    {
        switch (f.Kind)
        {
            case "Google": return "https://calendar.google.com/calendar/r/day/" + day.Year + "/" + day.Month + "/" + day.Day;
            case "Outlook":
                var u = f.Url.ToLowerInvariant();
                return u.Contains("office365.com") || u.Contains("outlook.office.com") ? "https://outlook.office.com/calendar/view/day" : "https://outlook.live.com/calendar/0/view/day";
            case "Apple": return "https://www.icloud.com/calendar/";
            default: return null;
        }
    }
}

// Every public int, bool and string field is a setting, saved under HKCU\Software\KinetikCalendar by its own name.
class Settings
{
    const string KeyPath = @"Software\KinetikCalendar";
    public int X = int.MinValue, Y = int.MinValue, Width = 320, Scale = 100, Opacity = CommonMenu.DefaultOpacity, Round = 100, Days = 7, Layer = 0, SyncMin = 15, MaxHeight = 0; // MaxHeight 0 = as tall as the screen allows
    public bool Locked, Snap = true, Shadow = true, Border = true, ClickThrough, Animate = true, ClickOpens = true;
    public string ThemeName = "Midnight", FontName = "";
    public int Accent, BgColor; // ARGB; 0 means the theme's own
    public int Density = 1, Marker = 0, MonthStyle = 0, FirstDay = -1, TitleStyle = 0, MaxPerDay = 0;
    public bool MergeDupes = true, WeekNumbers, ShadeWeekends = true, ShowDetails = true, ShowLocation = true, ShowPast = true, ShowAllDay = true, ShowEmptyDays, Countdown = true, ProgressBars = true;
    public bool Use24h = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H");
    public List<Feed> Feeds = new List<Feed>();
    public int Rev { get; set; } // bumped on every change, so the widget knows to redraw

    public DayOfWeek FirstDow
    {
        get { return FirstDay >= 0 && FirstDay <= 6 ? (DayOfWeek)FirstDay : CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek; }
    }

    static IEnumerable<FieldInfo> Fields
    {
        get
        {
            return typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(int) || f.FieldType == typeof(bool) || f.FieldType == typeof(string));
        }
    }

    static readonly string[] LookFields =
    {
        "Width", "MaxHeight", "Scale", "Opacity", "Round", "Shadow", "Border", "ThemeName", "FontName", "Accent", "BgColor",
        "Density", "Marker", "MonthStyle", "FirstDay", "TitleStyle", "WeekNumbers", "ShadeWeekends",
    };

    public void ResetLook()
    {
        var d = new Settings();
        foreach (var f in Fields.Where(f => LookFields.Contains(f.Name))) f.SetValue(this, f.GetValue(d));
    }

    static int I(RegistryKey k, string n, int d)
    {
        try { var v = k.GetValue(n); return v == null ? d : Convert.ToInt32(v); } catch { return d; }
    }

    // Set when the saved settings exist but couldn't be read. The app then runs on defaults, and Save does nothing,
    // so those defaults can never overwrite the real settings. Restarting the app tries again.
    public static bool Unreadable;
    static bool warned;

    // Reads the settings, retrying a few times: the key can be briefly unreadable, e.g. while a backup is being
    // restored into it or OneDrive/another program has it busy.
    public static Settings Load()
    {
        if (Program.Demo) return DemoData.Settings();
        Settings s = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) Thread.Sleep(500);
            bool ok;
            s = TryLoad(out ok);
            if (ok) { Unreadable = false; s.Opacity = CommonMenu.Migrate(s.Opacity); return s; }
        }
        Unreadable = true;
        if (!warned)
        {
            warned = true;
            MessageBox.Show("Kinetik Calendar couldn't read its saved settings, so it's showing the defaults for now.\n\n" +
                "Your settings haven't been changed, and nothing will be saved over them while this is showing. " +
                "Restart Kinetik Calendar to try again" + (Backup.Exists ? ", or use Settings → Behaviour → Restore." : "."),
                "Kinetik Calendar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return s;
    }

    // ok is false when the settings are there but something in them couldn't be read (a missing key is fine: first run).
    static Settings TryLoad(out bool ok)
    {
        ok = true;
        var s = new Settings();
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (k == null) { KinetikSuite.SettingsValidation.Sanitize(s); return s; }
                foreach (var f in Fields)
                {
                    var v = k.GetValue(f.Name);
                    if (v == null) continue;
                    try
                    {
                        if (f.FieldType == typeof(string)) f.SetValue(s, v.ToString());
                        else if (f.FieldType == typeof(int)) f.SetValue(s, Convert.ToInt32(v));
                        else f.SetValue(s, Convert.ToInt32(v) != 0);
                    }
                    catch { ok = false; }
                }
                // Settings from v1.0
                if (k.GetValue("MonthStyle") == null && I(k, "ShowMonth", 1) == 0) s.MonthStyle = 2;
                if (k.GetValue("TitleStyle") == null && I(k, "ShowTitle", 1) == 0) s.TitleStyle = 2;
                if (k.GetValue("ThemeName") == null) s.ThemeName = I(k, "Theme", 0) == 1 ? "Light" : I(k, "Theme", 0) == 2 ? "Auto" : "Midnight";
                s.SyncMin = Math.Max(5, s.SyncMin);

                using (var fk = k.OpenSubKey("Feeds"))
                    if (fk != null)
                        foreach (var n in fk.GetSubKeyNames().OrderBy(n => { int v; return int.TryParse(n, out v) ? v : 999; }))
                        {
                            // each calendar on its own: one that can't be read doesn't lose the others
                            try
                            {
                                using (var f = fk.OpenSubKey(n))
                                {
                                    var kind = Convert.ToString(f.GetValue("Kind", "Other"));
                                    s.Feeds.Add(new Feed
                                    {
                                        Name = Convert.ToString(f.GetValue("Name", "")), Url = Convert.ToString(f.GetValue("Url", "")), Kind = kind,
                                        Color = Color.FromArgb(255, Color.FromArgb(I(f, "Color", Feed.DefaultColor(kind).ToArgb()))), On = I(f, "On", 1) != 0
                                    });
                                }
                            }
                            catch { ok = false; } // keep going, but don't let a save drop the calendar that couldn't be read
                        }
            }
        }
        catch { ok = false; }
        KinetikSuite.SettingsValidation.Sanitize(s); return s;
    }

    public void Save()
    {
        if (Program.Demo || Unreadable) return;
        KinetikSuite.SettingsValidation.Sanitize(this);
        if (KinetikSuite.RegistryFiles.CannotSave) return; // never save defaults over settings that couldn't be read
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                foreach (var f in Fields)
                {
                    var v = f.GetValue(this);
                    if (f.FieldType == typeof(bool)) k.SetValue(f.Name, (bool)v ? 1 : 0);
                    else if (f.FieldType == typeof(int)) k.SetValue(f.Name, (int)v);
                    else k.SetValue(f.Name, (string)v ?? "");
                }
                k.DeleteSubKeyTree("Feeds", false);
                using (var fk = k.CreateSubKey("Feeds"))
                    for (int i = 0; i < Feeds.Count; i++)
                        using (var f = fk.CreateSubKey(i.ToString()))
                        {
                            f.SetValue("Name", Feeds[i].Name); f.SetValue("Url", Feeds[i].Url); f.SetValue("Kind", Feeds[i].Kind);
                            f.SetValue("Color", Feeds[i].Color.ToArgb()); f.SetValue("On", Feeds[i].On ? 1 : 0);
                        }
            }
        }
        catch { }
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Kinetik Calendar";

    public static bool IsOn
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Name) != null; }
            catch { return false; }
        }
    }

    public static void Set(bool on)
    {
        if (Program.Demo) return;
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(Name, false);
        }
        catch { }
    }
}

// ======================================================================= Themes
class Theme
{
    public readonly string Name;
    public Color Bg, Text, Sub, Accent;
    public readonly bool Light;

    public Theme(string name, Color bg, Color text, Color sub, Color accent, bool light)
    {
        Name = name; Bg = bg; Text = text; Sub = sub; Accent = accent; Light = light;
    }

    public Color Track { get { return Light ? Color.FromArgb(26, 0, 0, 0) : Color.FromArgb(34, 255, 255, 255); } }
    public Color Line { get { return Term ? Color.FromArgb(110, Sub) : Light ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255); } }

    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

    // The terminal themes (the same green, amber and yellow as Kinetik's dashboard and Kinetik Screen) draw in a
    // monospace font unless a font is picked, and their divider lines glow in the theme's colour.
    public bool Term { get { return IsTerm(Name); } }
    public static bool IsTerm(string name) { return name == "Terminal" || name == "Amber terminal" || name == "Yellow terminal"; }
    static string mono;
    public static string Mono
    {
        get
        {
            if (mono == null)
            {
                mono = "Consolas";
                try { using (var f = new Font("Cascadia Mono", 10f)) if (f.Name == "Cascadia Mono") mono = "Cascadia Mono"; } catch { }
            }
            return mono;
        }
    }

    public static readonly Theme[] All =
    {
        new Theme("Midnight", C(22, 22, 26), C(242, 242, 246), C(168, 168, 180), C(240, 88, 88), false),
        new Theme("Navy", C(22, 27, 40), C(236, 240, 248), C(138, 150, 176), C(77, 166, 255), false),
        new Theme("Graphite", C(40, 40, 44), C(236, 236, 236), C(160, 160, 166), C(255, 159, 10), false),
        new Theme("Nord", C(46, 52, 64), C(236, 239, 244), C(152, 162, 182), C(136, 192, 208), false),
        new Theme("Dracula", C(40, 42, 54), C(248, 248, 242), C(150, 156, 192), C(255, 121, 198), false),
        new Theme("Solarized", C(0, 43, 54), C(238, 232, 213), C(131, 148, 150), C(203, 75, 22), false),
        new Theme("Forest", C(22, 33, 27), C(228, 240, 231), C(138, 166, 148), C(108, 201, 124), false),
        new Theme("Ocean", C(12, 29, 45), C(228, 242, 250), C(128, 160, 186), C(56, 198, 232), false),
        new Theme("Sunset", C(40, 23, 35), C(250, 236, 240), C(192, 150, 172), C(255, 138, 92), false),
        new Theme("Mono", C(10, 10, 10), C(245, 245, 245), C(140, 140, 140), C(245, 245, 245), false),
        new Theme("OLED black", C(0, 0, 0), C(242, 242, 245), C(136, 136, 146), C(76, 194, 255), false),
        new Theme("Mocha", C(30, 30, 46), C(205, 214, 244), C(147, 153, 178), C(203, 166, 247), false),
        new Theme("Terminal", C(3, 10, 5), C(90, 255, 120), C(46, 170, 80), C(205, 255, 215), false),
        new Theme("Amber terminal", C(12, 7, 0), C(255, 182, 40), C(185, 118, 22), C(255, 232, 180), false),
        new Theme("Yellow terminal", C(0, 0, 0), C(255, 226, 0), C(200, 178, 0), C(255, 255, 190), false),
        new Theme("Light", C(246, 246, 248), C(24, 24, 28), C(96, 96, 106), C(230, 70, 70), true),
        new Theme("Paper", C(250, 246, 236), C(52, 42, 32), C(132, 116, 96), C(204, 92, 52), true),
        new Theme("Frost", C(236, 242, 250), C(20, 30, 48), C(92, 106, 132), C(40, 120, 230), true),
    };

    public static Theme Get(string name)
    {
        // "App" follows the theme and accent colour every Kinetik app shares.
        if (name == "App") { var l = KinetikSuite.Look.Current; return new Theme("App", l.Card, l.Text, l.Sub, l.Accent, l.Light); }
        foreach (var t in All) if (t.Name == name) return t;
        return All[0];
    }

    static bool appsLight;
    static DateTime appsLightAt;
    public static bool WindowsLight()
    {
        if ((DateTime.UtcNow - appsLightAt).TotalSeconds > 3)
        {
            appsLightAt = DateTime.UtcNow;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    appsLight = k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 1;
            }
            catch { appsLight = false; }
        }
        return appsLight;
    }

    // The theme in use, with the custom accent and background applied.
    public static Theme Resolve(Settings c)
    {
        var b = c.ThemeName == "Auto" ? Get(WindowsLight() ? "Light" : "Midnight") : Get(c.ThemeName);
        var t = new Theme(b.Name, b.Bg, b.Text, b.Sub, b.Accent, b.Light);
        if (c.Accent != 0) t.Accent = Color.FromArgb(255, Color.FromArgb(c.Accent));
        if (c.BgColor != 0) t.Bg = Color.FromArgb(255, Color.FromArgb(c.BgColor));
        return t;
    }
}

// ======================================================================= Fetching
// The last good copy of each calendar is kept in %LOCALAPPDATA%\KinetikCalendar, so the widget fills in straight away
// at startup and keeps working offline. It's kept out of the app's folder on purpose, so a synced or shared folder never holds your calendars.
static class Net
{
    public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KinetikCalendar"); } }

    static string Hash(string s)
    {
        using (var sha = SHA1.Create())
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2")));
    }

    static string CachePath(string url) { return Path.Combine(Dir, Hash(url.Trim()) + ".ics"); }

    public static string ReadCache(string url)
    {
        try { var p = CachePath(url); return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null; } catch { return null; }
    }

    public static void WriteCache(string url, string text)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(CachePath(url), text, Encoding.UTF8); } catch { }
    }

    public static void DeleteCache(string url)
    {
        try { File.Delete(CachePath(url)); File.Delete(CachePath(url) + ".meta"); } catch { }
    }

    // ETag, Last-Modified and content hash of the cached copy, so an unchanged calendar is neither downloaded nor parsed again.
    static string[] ReadMeta(string url)
    {
        try { var p = CachePath(url) + ".meta"; return File.Exists(p) ? File.ReadAllLines(p) : null; } catch { return null; }
    }

    static void WriteMeta(string url, string etag, string modified, string hash)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllLines(CachePath(url) + ".meta", new[] { etag ?? "", modified ?? "", hash ?? "" }); } catch { }
    }

    static string Normalise(string url)
    {
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url.Substring(9);
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new Exception("The link should start with https:// or webcal://");
        return url;
    }

    // Returns null when the server says the calendar hasn't changed (304).
    static string Get(string url, ref string etag, ref string modified)
    {
        var req = (HttpWebRequest)WebRequest.Create(Normalise(url));
        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) KinetikCalendar/" + Program.Version;
        req.Timeout = 30000; req.ReadWriteTimeout = 30000;
        req.AutomaticDecompression = DecompressionMethods.GZip;
        if (!string.IsNullOrEmpty(etag)) req.Headers["If-None-Match"] = etag;
        DateTime lm;
        if (!string.IsNullOrEmpty(modified) && DateTime.TryParse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out lm)) req.IfModifiedSince = lm;
        using (var deadline = new CancellationTokenSource(30000))
        using (deadline.Token.Register(req.Abort))
        try
        {
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                etag = resp.Headers["ETag"];
                modified = resp.Headers["Last-Modified"];
                if (resp.ContentLength > 16 * 1024 * 1024) throw new IOException("Calendar download exceeds 16 MB.");
                var content = new StringBuilder(); var buffer = new char[8192]; int read;
                while ((read = rd.Read(buffer, 0, buffer.Length)) > 0)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (content.Length + read > 8 * 1024 * 1024) throw new IOException("Calendar download exceeds 8 million characters.");
                    content.Append(buffer, 0, read);
                }
                var text = content.ToString();
                if (text.IndexOf("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("That link didn't return a calendar");
                return text;
            }
        }
        catch (WebException we)
        {
            var r = we.Response as HttpWebResponse;
            if (r != null)
            {
                bool unchanged = r.StatusCode == HttpStatusCode.NotModified;
                r.Close();
                if (unchanged) return null;
            }
            throw;
        }
    }

    public static string Download(string url)
    {
        string e = null, m = null;
        return Get(url, ref e, ref m);
    }

    public static string Friendly(Exception ex)
    {
        var we = ex as WebException;
        if (we != null)
        {
            var r = we.Response as HttpWebResponse;
            if (r != null)
            {
                int code = (int)r.StatusCode;
                if (code == 401 || code == 403) return "Access denied (" + code + "). The link may have been reset";
                if (code == 404) return "Link not found (404). It may have been reset or unpublished";
                return "Server error (" + code + ")";
            }
            if (we.Status == WebExceptionStatus.Timeout) return "Timed out";
            if (we.Status == WebExceptionStatus.NameResolutionFailure || we.Status == WebExceptionStatus.ConnectFailure) return "Offline";
        }
        return ex.Message;
    }

    // Downloads, parses and caches one calendar, skipping the work when nothing changed. Falls back to the cached copy offline.
    public static void Sync(Feed f)
    {
        f.Changed = false;
        if (Program.Demo) { f.Status = "Demo calendar"; return; }
        try
        {
            var meta = f.Parsed != null ? ReadMeta(f.Url) : null;
            string etag = meta != null && meta.Length > 0 ? meta[0] : null, modified = meta != null && meta.Length > 1 ? meta[1] : null;
            string oldHash = meta != null && meta.Length > 2 ? meta[2] : null;
            var text = Get(f.Url, ref etag, ref modified);
            if (text != null)
            {
                var h = Hash(text);
                if (h != oldHash || f.Parsed == null)
                {
                    f.Parsed = Ics.Parse(text);
                    f.Changed = true;
                    WriteCache(f.Url, text);
                }
                WriteMeta(f.Url, etag, modified, h);
            }
            if (f.Failed) f.Changed = true; // back online: the status line changes
            f.Failed = false;
            f.Status = "Synced " + DateTime.Now.ToString("t");
        }
        catch (Exception ex)
        {
            if (!f.Failed) f.Changed = true;
            f.Failed = true;
            f.Status = Friendly(ex);
            if (f.Parsed == null)
            {
                var cached = ReadCache(f.Url);
                if (cached != null) { f.Parsed = Ics.Parse(cached); f.Changed = true; }
            }
        }
    }
}

// ======================================================================= iCalendar
class CalEvent
{
    public string Title, Location;
    public DateTime Start, End; // local time; all-day events run midnight to midnight
    public bool AllDay;
    public Feed Feed;
    public List<Color> Also; // the same event in other calendars
}

// Enough of RFC 5545 for real-world Google, Outlook and iCloud feeds: time zones, all-day events, RRULE
// (DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, COUNT, UNTIL, BYDAY, BYMONTHDAY, BYMONTH, BYSETPOS), EXDATE,
// moved or cancelled single occurrences (RECURRENCE-ID), and cancelled events.
static class Ics
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Only these are kept. Descriptions, attendees and the like are the bulk of a feed and the widget never shows them.
    static readonly HashSet<string> Wanted = new HashSet<string> { "DTSTART", "DTEND", "DURATION", "RRULE", "EXDATE", "RECURRENCE-ID", "UID", "SUMMARY", "LOCATION", "STATUS" };

    public class Prop
    {
        public string Name, Value;
        public Dictionary<string, string> P;
        public string Param(string k) { string v; return P != null && P.TryGetValue(k, out v) ? v : null; }
    }

    public class VEvent
    {
        public readonly List<Prop> Props = new List<Prop>(6);
        public Prop Get(string n) { foreach (var p in Props) if (p.Name == n) return p; return null; }
        public string Text(string n) { var p = Get(n); return p == null ? null : Unescape(p.Value); }
    }

    struct T
    {
        public DateTime Wall;     // the clock time as written
        public TimeZoneInfo Tz;
        public bool DateOnly;
        public DateTime Local { get { return DateOnly ? Wall : ToLocal(Wall, Tz); } }
    }

    // ---- parsing
    public static List<VEvent> Parse(string text)
    {
        var evs = new List<VEvent>();
        VEvent cur = null;
        int depth = 0; // inside a VALARM or other block within the event
        var sb = new StringBuilder();
        using (var rd = new StringReader(text))
        {
            string raw, pending = null;
            while (true)
            {
                raw = rd.ReadLine();
                if (raw != null && raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && pending != null) { sb.Append(raw, 1, raw.Length - 1); continue; } // folded line
                if (pending != null)
                {
                    var l = sb.ToString().TrimEnd();
                    if (l.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new VEvent(); depth = 0; }
                    else if (cur != null)
                    {
                        if (l.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) depth++;
                        else if (l.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (depth > 0) depth--;
                            else { evs.Add(cur); cur = null; }
                        }
                        else if (depth == 0)
                        {
                            var p = ParseLine(l);
                            if (p != null) cur.Props.Add(p);
                        }
                    }
                }
                if (raw == null) break;
                sb.Clear(); sb.Append(raw); pending = raw;
            }
        }
        return evs;
    }

    static Prop ParseLine(string l)
    {
        bool quoted = false;
        int colon = -1;
        for (int i = 0; i < l.Length; i++)
        {
            if (l[i] == '"') quoted = !quoted;
            else if (l[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return null;
        int semi = l.IndexOf(';');
        var name = (semi > 0 && semi < colon ? l.Substring(0, semi) : l.Substring(0, colon)).Trim().ToUpperInvariant();
        if (!Wanted.Contains(name)) return null;
        var p = new Prop { Name = string.Intern(name), Value = l.Substring(colon + 1) };
        if (semi > 0 && semi < colon)
        {
            p.P = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in l.Substring(semi + 1, colon - semi - 1).Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) p.P[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim('"');
            }
        }
        return p;
    }

    static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s.Trim();
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n == 'n' || n == 'N' ? '\n' : n);
            }
            else sb.Append(s[i]);
        }
        return sb.ToString().Trim();
    }

    public static string CalName(string text)
    {
        var m = Regex.Match(text, @"^X-WR-CALNAME:(.*?)\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? Unescape(m.Groups[1].Value) : null;
    }

    static List<T> Times(Prop p)
    {
        var res = new List<T>();
        var tzid = p.Param("TZID");
        bool dateOnly = string.Equals(p.Param("VALUE"), "DATE", StringComparison.OrdinalIgnoreCase);
        foreach (var raw in p.Value.Split(','))
        {
            var v = raw.Trim();
            if (v.Length < 8) continue;
            DateTime d;
            if (v.Length == 8 || dateOnly)
            {
                if (DateTime.TryParseExact(v.Substring(0, 8), "yyyyMMdd", Inv, DateTimeStyles.None, out d))
                    res.Add(new T { Wall = d, Tz = TimeZoneInfo.Local, DateOnly = true });
                continue;
            }
            bool utc = v.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
            if (!DateTime.TryParseExact(utc ? v.Substring(0, v.Length - 1) : v, new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" }, Inv, DateTimeStyles.None, out d)) continue;
            res.Add(new T { Wall = d, Tz = utc ? TimeZoneInfo.Utc : tzid != null ? Zone(tzid) : TimeZoneInfo.Local });
        }
        return res;
    }

    static bool TryTime(Prop p, out T t)
    {
        var list = Times(p);
        t = list.Count > 0 ? list[0] : default(T);
        return list.Count > 0;
    }

    static TimeSpan Duration(string v)
    {
        var m = Regex.Match(v.Trim(), @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return TimeSpan.Zero;
        Func<int, int> g = i => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        var ts = new TimeSpan(g(2) * 7 + g(3), g(4), g(5), g(6));
        return m.Groups[1].Value == "-" ? -ts : ts;
    }

    // ---- time zones
    static DateTime ToLocal(DateTime wall, TimeZoneInfo tz)
    {
        if (tz == null || ReferenceEquals(tz, TimeZoneInfo.Local)) return wall; // floating time
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        try
        {
            if (tz.IsInvalidTime(wall)) wall = wall.AddHours(1); // falls in a daylight-saving gap
            return TimeZoneInfo.ConvertTime(wall, tz, TimeZoneInfo.Local);
        }
        catch { return wall; }
    }

    static readonly Dictionary<string, TimeZoneInfo> zones = new Dictionary<string, TimeZoneInfo>(StringComparer.OrdinalIgnoreCase);

    static TimeZoneInfo Zone(string id)
    {
        lock (zones)
        {
            TimeZoneInfo z;
            if (!zones.TryGetValue(id, out z)) zones[id] = z = FindZone(id.Trim().Trim('"'));
            return z;
        }
    }

    // Outlook writes Windows zone names, which .NET knows. Google and iCloud write IANA names (Australia/Melbourne),
    // which .NET Framework doesn't, so those go through this table.
    static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        string win;
        if (Iana.TryGetValue(id, out win)) try { return TimeZoneInfo.FindSystemTimeZoneById(win); } catch { }
        foreach (var kv in Iana) // e.g. "/mozilla.org/20050126_1/Europe/London"
            if (id.EndsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) try { return TimeZoneInfo.FindSystemTimeZoneById(kv.Value); } catch { }
        return TimeZoneInfo.Local;
    }

    static readonly Dictionary<string, string> Iana = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Australia/Melbourne", "AUS Eastern Standard Time" }, { "Australia/Sydney", "AUS Eastern Standard Time" },
        { "Australia/Canberra", "AUS Eastern Standard Time" }, { "Australia/ACT", "AUS Eastern Standard Time" },
        { "Australia/NSW", "AUS Eastern Standard Time" }, { "Australia/Victoria", "AUS Eastern Standard Time" },
        { "Australia/Hobart", "Tasmania Standard Time" }, { "Australia/Brisbane", "E. Australia Standard Time" },
        { "Australia/Adelaide", "Cen. Australia Standard Time" }, { "Australia/Darwin", "AUS Central Standard Time" },
        { "Australia/Perth", "W. Australia Standard Time" }, { "Pacific/Auckland", "New Zealand Standard Time" },
        { "Asia/Colombo", "Sri Lanka Standard Time" }, { "Asia/Kolkata", "India Standard Time" }, { "Asia/Calcutta", "India Standard Time" },
        { "Asia/Singapore", "Singapore Standard Time" }, { "Asia/Kuala_Lumpur", "Singapore Standard Time" }, { "Asia/Manila", "Singapore Standard Time" },
        { "Asia/Tokyo", "Tokyo Standard Time" }, { "Asia/Seoul", "Korea Standard Time" }, { "Asia/Shanghai", "China Standard Time" },
        { "Asia/Hong_Kong", "China Standard Time" }, { "Asia/Taipei", "Taipei Standard Time" }, { "Asia/Bangkok", "SE Asia Standard Time" },
        { "Asia/Jakarta", "SE Asia Standard Time" }, { "Asia/Ho_Chi_Minh", "SE Asia Standard Time" }, { "Asia/Dubai", "Arabian Standard Time" },
        { "Asia/Karachi", "Pakistan Standard Time" }, { "Asia/Dhaka", "Bangladesh Standard Time" }, { "Asia/Kathmandu", "Nepal Standard Time" },
        { "Europe/London", "GMT Standard Time" }, { "Europe/Dublin", "GMT Standard Time" }, { "Europe/Lisbon", "GMT Standard Time" },
        { "Europe/Paris", "Romance Standard Time" }, { "Europe/Brussels", "Romance Standard Time" }, { "Europe/Madrid", "Romance Standard Time" },
        { "Europe/Copenhagen", "Romance Standard Time" }, { "Europe/Berlin", "W. Europe Standard Time" }, { "Europe/Amsterdam", "W. Europe Standard Time" },
        { "Europe/Rome", "W. Europe Standard Time" }, { "Europe/Vienna", "W. Europe Standard Time" }, { "Europe/Stockholm", "W. Europe Standard Time" },
        { "Europe/Zurich", "W. Europe Standard Time" }, { "Europe/Oslo", "W. Europe Standard Time" }, { "Europe/Warsaw", "Central European Standard Time" },
        { "Europe/Prague", "Central Europe Standard Time" }, { "Europe/Budapest", "Central Europe Standard Time" }, { "Europe/Athens", "GTB Standard Time" },
        { "Europe/Helsinki", "FLE Standard Time" }, { "Europe/Kiev", "FLE Standard Time" }, { "Europe/Istanbul", "Turkey Standard Time" },
        { "Europe/Moscow", "Russian Standard Time" }, { "Africa/Johannesburg", "South Africa Standard Time" }, { "Africa/Cairo", "Egypt Standard Time" },
        { "Africa/Lagos", "W. Central Africa Standard Time" }, { "Africa/Nairobi", "E. Africa Standard Time" },
        { "America/New_York", "Eastern Standard Time" }, { "America/Toronto", "Eastern Standard Time" }, { "America/Detroit", "Eastern Standard Time" },
        { "America/Chicago", "Central Standard Time" }, { "America/Winnipeg", "Central Standard Time" }, { "America/Denver", "Mountain Standard Time" },
        { "America/Edmonton", "Mountain Standard Time" }, { "America/Phoenix", "US Mountain Standard Time" }, { "America/Los_Angeles", "Pacific Standard Time" },
        { "America/Vancouver", "Pacific Standard Time" }, { "America/Anchorage", "Alaskan Standard Time" }, { "Pacific/Honolulu", "Hawaiian Standard Time" },
        { "America/Halifax", "Atlantic Standard Time" }, { "America/Mexico_City", "Central Standard Time (Mexico)" },
        { "America/Sao_Paulo", "E. South America Standard Time" }, { "America/Bogota", "SA Pacific Standard Time" },
        { "America/Argentina/Buenos_Aires", "Argentina Standard Time" }, { "America/Buenos_Aires", "Argentina Standard Time" },
        { "Etc/UTC", "UTC" }, { "Etc/GMT", "UTC" }, { "UTC", "UTC" }, { "GMT", "UTC" }, { "Z", "UTC" },
    };

    // ---- expanding into the events between two local times
    public static List<CalEvent> Expand(List<VEvent> evs, Feed feed, DateTime from, DateTime to)
    {
        var result = new List<CalEvent>();
        // Single occurrences that were moved or cancelled: UID → their original start times
        var moved = new Dictionary<string, HashSet<DateTime>>();
        foreach (var e in evs)
        {
            var rid = e.Get("RECURRENCE-ID");
            T t;
            if (rid == null || !TryTime(rid, out t)) continue;
            var uid = e.Text("UID") ?? "";
            HashSet<DateTime> set;
            if (!moved.TryGetValue(uid, out set)) moved[uid] = set = new HashSet<DateTime>();
            set.Add(t.Local);
        }
        foreach (var e in evs)
        {
            try { ExpandOne(e, feed, from, to, moved, result); }
            catch { } // one malformed event shouldn't hide the rest
        }
        return result;
    }

    static void ExpandOne(VEvent e, Feed feed, DateTime from, DateTime to, Dictionary<string, HashSet<DateTime>> moved, List<CalEvent> result)
    {
        var ds = e.Get("DTSTART");
        T st;
        if (ds == null || !TryTime(ds, out st)) return;
        var rr = e.Get("RRULE");
        bool isOverride = e.Get("RECURRENCE-ID") != null;
        bool recurring = rr != null && !isOverride;
        // A one-off event that starts after the window can't show up: skip it before doing any more work
        if (!recurring && st.Wall > to.AddDays(2)) return;
        if (string.Equals(e.Text("STATUS"), "CANCELLED", StringComparison.OrdinalIgnoreCase)) return;
        var uid = e.Text("UID") ?? "";

        TimeSpan len;
        var de = e.Get("DTEND");
        var du = e.Get("DURATION");
        T et;
        if (de != null && TryTime(de, out et)) len = st.DateOnly ? et.Wall.Date - st.Wall : et.Local - st.Local;
        else if (du != null) len = Duration(du.Value);
        else len = st.DateOnly ? TimeSpan.FromDays(1) : TimeSpan.Zero;
        if (len < TimeSpan.Zero) len = TimeSpan.Zero;
        if (st.DateOnly && len < TimeSpan.FromDays(1)) len = TimeSpan.FromDays(1);
        if (!recurring && st.Wall + len < from.AddDays(-2)) return;

        var title = e.Text("SUMMARY");
        if (string.IsNullOrWhiteSpace(title)) title = "(No title)";
        var loc = e.Text("LOCATION");
        if (!string.IsNullOrWhiteSpace(loc)) loc = Regex.Replace(loc, @"\s*\n\s*", ", "); else loc = null;

        var skip = new HashSet<DateTime>();
        if (recurring)
        {
            foreach (var p in e.Props)
                if (p.Name == "EXDATE")
                    foreach (var t in Times(p)) skip.Add(st.DateOnly ? t.Local.Date : t.Local);
            HashSet<DateTime> mv;
            if (moved.TryGetValue(uid, out mv)) skip.UnionWith(mv);
        }

        var walls = recurring ? Recur(st.Wall, rr.Value, st.Tz, to.AddDays(2)) : new[] { st.Wall };
        foreach (var w in walls)
        {
            var start = st.DateOnly ? w : ToLocal(w, st.Tz);
            if (start >= to) break;
            if (recurring && skip.Contains(start)) continue;
            var end = start + len;
            if (end > from || (len == TimeSpan.Zero && start >= from))
                result.Add(new CalEvent { Title = title, Location = loc, Start = start, End = end, AllDay = st.DateOnly, Feed = feed });
        }
    }

    static DayOfWeek Dow(string code)
    {
        switch (code.ToUpperInvariant())
        {
            case "SU": return DayOfWeek.Sunday;
            case "MO": return DayOfWeek.Monday;
            case "TU": return DayOfWeek.Tuesday;
            case "WE": return DayOfWeek.Wednesday;
            case "TH": return DayOfWeek.Thursday;
            case "FR": return DayOfWeek.Friday;
            default: return DayOfWeek.Saturday;
        }
    }

    static List<int> Ints(Dictionary<string, string> r, string key)
    {
        var res = new List<int>();
        string v;
        if (r.TryGetValue(key, out v))
            foreach (var s in v.Split(',')) { int n; if (int.TryParse(s.Trim(), out n) && n != 0) res.Add(n); }
        return res;
    }

    // Occurrence start times (as clock times in the event's own zone), in order, up to `limit`.
    static IEnumerable<DateTime> Recur(DateTime start, string rule, TimeZoneInfo tz, DateTime limit)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in rule.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) r[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
        }
        string freq;
        if (!r.TryGetValue("FREQ", out freq)) yield break;
        freq = freq.ToUpperInvariant();
        string s;
        int interval = 1, count = int.MaxValue, n;
        if (r.TryGetValue("INTERVAL", out s) && int.TryParse(s, out n)) interval = Math.Max(1, n);
        if (r.TryGetValue("COUNT", out s) && int.TryParse(s, out n)) count = n;
        var until = DateTime.MaxValue;
        if (r.TryGetValue("UNTIL", out s))
        {
            s = s.Trim();
            DateTime u;
            if (s.Length == 8 && DateTime.TryParseExact(s, "yyyyMMdd", Inv, DateTimeStyles.None, out u)) until = u.AddDays(1).AddTicks(-1);
            else if (DateTime.TryParseExact(s.TrimEnd('Z', 'z'), "yyyyMMdd'T'HHmmss", Inv, DateTimeStyles.None, out u))
                until = s.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ? TimeZoneInfo.ConvertTimeFromUtc(u, tz ?? TimeZoneInfo.Local) : u;
        }
        var byDay = new List<KeyValuePair<int, DayOfWeek>>();
        if (r.TryGetValue("BYDAY", out s))
            foreach (var tok in s.Split(','))
            {
                var m = Regex.Match(tok.Trim().ToUpperInvariant(), @"^([+-]?\d+)?(MO|TU|WE|TH|FR|SA|SU)$");
                if (m.Success) byDay.Add(new KeyValuePair<int, DayOfWeek>(m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0, Dow(m.Groups[2].Value)));
            }
        var byMonthDay = Ints(r, "BYMONTHDAY");
        var byMonth = Ints(r, "BYMONTH");
        var bySetPos = Ints(r, "BYSETPOS");
        var wkst = r.TryGetValue("WKST", out s) ? Dow(s) : DayOfWeek.Monday;
        if (until < limit) limit = until;

        var time = start.TimeOfDay;
        var d0 = start.Date;
        int emitted = 0;
        for (int k = 0; k < 50000; k++)
        {
            DateTime periodStart;
            var c = new List<DateTime>();
            switch (freq)
            {
                case "DAILY":
                    periodStart = d0.AddDays((double)k * interval);
                    if ((byMonth.Count == 0 || byMonth.Contains(periodStart.Month)) &&
                        (byDay.Count == 0 || byDay.Any(b => b.Value == periodStart.DayOfWeek)) &&
                        (byMonthDay.Count == 0 || byMonthDay.Any(md => (md > 0 ? md : DateTime.DaysInMonth(periodStart.Year, periodStart.Month) + md + 1) == periodStart.Day))) c.Add(periodStart);
                    break;
                case "WEEKLY":
                    periodStart = d0.AddDays(-(((int)d0.DayOfWeek - (int)wkst + 7) % 7)).AddDays(7.0 * k * interval);
                    if (byDay.Count == 0) c.Add(periodStart.AddDays(((int)d0.DayOfWeek - (int)wkst + 7) % 7));
                    else foreach (var bd in byDay) c.Add(periodStart.AddDays(((int)bd.Value - (int)wkst + 7) % 7));
                    if (byMonth.Count > 0) c.RemoveAll(x => !byMonth.Contains(x.Month));
                    break;
                case "MONTHLY":
                    periodStart = new DateTime(d0.Year, d0.Month, 1).AddMonths(k * interval);
                    if (byMonth.Count == 0 || byMonth.Contains(periodStart.Month)) c.AddRange(MonthDays(periodStart, byDay, byMonthDay, d0.Day));
                    break;
                case "YEARLY":
                    if (d0.Year + k * interval > 9998) yield break;
                    periodStart = new DateTime(d0.Year + k * interval, 1, 1);
                    foreach (var m in byMonth.Count > 0 ? byMonth : new List<int> { d0.Month })
                        if (m >= 1 && m <= 12) c.AddRange(MonthDays(new DateTime(periodStart.Year, m, 1), byDay, byMonthDay, d0.Day));
                    break;
                default:
                    yield break;
            }
            if (periodStart > limit) yield break;
            if (c.Count > 1) c = c.Distinct().OrderBy(x => x).ToList();
            if (bySetPos.Count > 0)
            {
                var pick = new List<DateTime>();
                foreach (var p in bySetPos)
                {
                    int i = p > 0 ? p - 1 : c.Count + p;
                    if (i >= 0 && i < c.Count) pick.Add(c[i]);
                }
                c = pick.Distinct().OrderBy(x => x).ToList();
            }
            foreach (var day in c)
            {
                var dt = day + time;
                if (dt < start) continue;
                if (dt > until || emitted >= count) yield break;
                emitted++;
                yield return dt;
            }
        }
    }

    static List<DateTime> MonthDays(DateTime month, List<KeyValuePair<int, DayOfWeek>> byDay, List<int> byMonthDay, int defaultDay)
    {
        int dim = DateTime.DaysInMonth(month.Year, month.Month);
        var res = new List<DateTime>();
        if (byMonthDay.Count > 0)
        {
            foreach (var md in byMonthDay)
            {
                int d = md > 0 ? md : dim + md + 1;
                if (d < 1 || d > dim) continue;
                var dt = new DateTime(month.Year, month.Month, d);
                if (byDay.Count == 0 || byDay.Any(b => b.Value == dt.DayOfWeek)) res.Add(dt);
            }
        }
        else if (byDay.Count > 0)
        {
            foreach (var bd in byDay)
            {
                var all = new List<DateTime>();
                for (int d = 1; d <= dim; d++)
                {
                    var dt = new DateTime(month.Year, month.Month, d);
                    if (dt.DayOfWeek == bd.Value) all.Add(dt);
                }
                if (bd.Key == 0) res.AddRange(all);
                else
                {
                    int i = bd.Key > 0 ? bd.Key - 1 : all.Count + bd.Key;
                    if (i >= 0 && i < all.Count) res.Add(all[i]);
                }
            }
        }
        else if (defaultDay <= dim) res.Add(new DateTime(month.Year, month.Month, defaultDay));
        return res;
    }
}

// ======================================================================= Demo
// Made-up calendars for `--demo`, placed around today so the widget always looks lived-in. Used for the screenshots.
static class DemoData
{
    public static Settings Settings()
    {
        var s = new Settings();
        s.Feeds.Add(new Feed { Name = "Work", Url = "demo:work", Kind = "Outlook", Color = Feed.Palette[1] });
        s.Feeds.Add(new Feed { Name = "Personal", Url = "demo:personal", Kind = "Google", Color = Feed.Palette[0] });
        s.Feeds.Add(new Feed { Name = "Family", Url = "demo:family", Kind = "Apple", Color = Feed.Palette[2] });
        s.Feeds.Add(new Feed { Name = "Fitness", Url = "demo:fitness", Kind = "Other", Color = Feed.Palette[4] });
        return s;
    }

    static readonly string[] Codes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

    public static string Ics(Feed f)
    {
        var today = DateTime.Today;
        var now = DateTime.Now;
        var nowSlot = today.AddMinutes(Math.Floor(now.TimeOfDay.TotalMinutes / 15) * 15);
        var sb = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nX-WR-CALNAME:" + f.Name + "\r\n");
        int uid = 0;
        Action<string, DateTime, DateTime, string, string> ev = (title, from, end, loc, rule) =>
        {
            sb.Append("BEGIN:VEVENT\r\nUID:demo-" + f.Name + "-" + (uid++) + "\r\nSUMMARY:" + title + "\r\n");
            sb.Append("DTSTART:" + from.ToString("yyyyMMdd'T'HHmmss") + "\r\nDTEND:" + end.ToString("yyyyMMdd'T'HHmmss") + "\r\n");
            if (loc != null) sb.Append("LOCATION:" + loc + "\r\n");
            if (rule != null) sb.Append("RRULE:" + rule + "\r\n");
            sb.Append("END:VEVENT\r\n");
        };
        Action<string, DateTime, int, string> allDay = (title, day, days, rule) =>
        {
            sb.Append("BEGIN:VEVENT\r\nUID:demo-" + f.Name + "-" + (uid++) + "\r\nSUMMARY:" + title + "\r\n");
            sb.Append("DTSTART;VALUE=DATE:" + day.ToString("yyyyMMdd") + "\r\nDTEND;VALUE=DATE:" + day.AddDays(days).ToString("yyyyMMdd") + "\r\n");
            if (rule != null) sb.Append("RRULE:" + rule + "\r\n");
            sb.Append("END:VEVENT\r\n");
        };
        Func<int, double, DateTime> at = (d, h) => today.AddDays(d).AddHours(h);
        var start = today.AddDays(-20);
        switch (f.Url)
        {
            case "demo:work":
                ev("Team stand-up", start.AddHours(9.25), start.AddHours(9.5), "Teams", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR");
                ev("Sprint retro", nowSlot.AddMinutes(-30), nowSlot.AddMinutes(45), "Studio 2", null);
                ev("Design review", nowSlot.AddMinutes(75), nowSlot.AddMinutes(135), "Studio 2", null);
                ev("1:1 with Jordan", at(1, 11.5), at(1, 12), null, null);
                ev("Quarterly planning", at(2, 10), at(2, 12), "Board room", null);
                ev("Product demo", at(3, 15), at(3, 16), "Zoom", null);
                allDay("Project launch", today.AddDays(9), 1, null);
                break;
            case "demo:personal":
                ev("Coffee with Sam", at(1, 8), at(1, 8.75), "Blue Door Café", null);
                ev("Quarterly planning", at(2, 10), at(2, 12), "Board room", null); // also in Work: shows once, in both colours
                ev("Book club", start.AddHours(19), start.AddHours(20.5), "Library", "FREQ=WEEKLY;BYDAY=" + Codes[(int)today.AddDays(3).DayOfWeek]);
                ev("Dentist", at(4, 16.5), at(4, 17.25), "Smile Dental", null);
                allDay("Weekend away", today.AddDays(5), 3, null);
                break;
            case "demo:family":
                allDay("Mum's birthday", today.AddDays(2), 1, "FREQ=YEARLY");
                ev("School pickup", start.AddHours(15.25), start.AddHours(15.75), null, "FREQ=WEEKLY;BYDAY=MO,WE");
                ev("Family dinner", at(6, 18.5), at(6, 20.5), "Nonna's", null);
                break;
            default:
                ev("Gym", start.AddHours(6.5), start.AddHours(7.5), "Fitness First", "FREQ=WEEKLY;BYDAY=TU,TH,SA");
                ev("Yoga", start.AddHours(9), start.AddHours(10), "Studio Flow", "FREQ=WEEKLY;BYDAY=SU");
                ev("Park run", at(5, 8), at(5, 9), "Albert Park", null);
                break;
        }
        return sb.Append("END:VCALENDAR\r\n").ToString();
    }
}

// ======================================================================= Drawing helpers
static class Draw
{
    static string family;
    public static string Family
    {
        get
        {
            if (family == null) family = Has("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            return family;
        }
    }

    static readonly Dictionary<string, bool> installed = new Dictionary<string, bool>();
    public static bool Has(string name)
    {
        bool v;
        if (!installed.TryGetValue(name, out v))
        {
            try { using (var f = new FontFamily(name)) v = f.Name == name; } catch { v = false; }
            installed[name] = v;
        }
        return v;
    }

    public static readonly string[] FontChoices = { "Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Calibri", "Aptos", "Inter", "Cascadia Code", "Consolas", "Verdana", "Georgia" };

    public static float Ease(float t) { t = Math.Max(0, Math.Min(1, t)); return 1 - (1 - t) * (1 - t) * (1 - t); }               // ease-out cubic
    public static float Back(float t) { t = Math.Max(0, Math.Min(1, t)); float c = 1.7f; return 1 + (c + 1) * (float)Math.Pow(t - 1, 3) + c * (float)Math.Pow(t - 1, 2); } // slight overshoot

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Max(0, Math.Min(1, t));
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
        if (rad < 0.5f) { p.AddRectangle(r); return p; }
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, RectangleF r, float rad, Color c)
    {
        if (c.A == 0 || r.Width <= 0 || r.Height <= 0) return;
        using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w)
    {
        using (var p = Round(r, rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
    }
}

// A premultiplied-alpha bitmap backed by a DIB, pushed to a layered window with UpdateLayeredWindow (as in Kinetik).
sealed class DibSurface : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
    {
        public int Size, Width, Height; public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLEND b, int flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    IntPtr memDC, dib, oldObj;
    Bitmap bmp;
    int w, h;

    public Bitmap Canvas(int width, int height)
    {
        if (dib != IntPtr.Zero && width == w && height == h) return bmp;
        Free();
        memDC = CreateCompatibleDC(IntPtr.Zero);
        var bmi = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        IntPtr bits;
        dib = CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
        oldObj = SelectObject(memDC, dib);
        bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        w = width; h = height;
        return bmp;
    }

    public void Push(IntPtr hwnd, int x, int y, byte alpha)
    {
        var size = new SIZE { CX = w, CY = h };
        var src = new POINT();
        var pos = new POINT { X = x, Y = y };
        var blend = new BLEND { Op = 0, Flags = 0, Alpha = alpha, Format = 1 };
        UpdateLayeredWindow(hwnd, IntPtr.Zero, ref pos, ref size, memDC, ref src, 0, ref blend, 2);
    }

    void Free()
    {
        if (bmp != null) { bmp.Dispose(); bmp = null; }
        if (memDC != IntPtr.Zero) { SelectObject(memDC, oldObj); DeleteDC(memDC); memDC = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
    }

    public void Dispose() { Free(); }
}

// A card's soft shadow: stacked, slightly larger translucent shapes. Those anti-aliased fills over the whole card cost
// more than the rest of a frame put together, so they're drawn once into a bitmap and copied while nothing about them changes.
sealed class ShadowCache : IDisposable
{
    Bitmap bmp; string key;

    // Lays the shadow into the (cleared) w × h target. look sums up everything draw depends on.
    public void Paint(Graphics target, int w, int h, string look, Action<Graphics> draw)
    {
        var k = w + "x" + h + "|" + look + "|" + (int)target.SmoothingMode + (int)target.PixelOffsetMode;
        if (k != key || bmp == null)
        {
            if (bmp == null || bmp.Width != w || bmp.Height != h) { if (bmp != null) bmp.Dispose(); bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb); }
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = target.SmoothingMode; g.PixelOffsetMode = target.PixelOffsetMode;
                draw(g);
            }
            key = k;
        }
        bmp.SetResolution(target.DpiX, target.DpiY); // so it's copied 1:1
        var mode = target.CompositingMode;
        target.CompositingMode = CompositingMode.SourceCopy;
        target.DrawImageUnscaled(bmp, 0, 0);
        target.CompositingMode = mode;
    }

    public void Dispose() { if (bmp != null) bmp.Dispose(); bmp = null; key = null; }
}

class DarkColors : ProfessionalColorTable
{
    static Color Bg { get { return KinetikSuite.Look.Current.Card; } }
    static Color Hi { get { return KinetikSuite.Look.Current.Hover; } }
    static Color Edge { get { return KinetikSuite.Look.Current.Border; } }
    public override Color ToolStripDropDownBackground { get { return Bg; } }
    public override Color ImageMarginGradientBegin { get { return Bg; } }
    public override Color ImageMarginGradientMiddle { get { return Bg; } }
    public override Color ImageMarginGradientEnd { get { return Bg; } }
    public override Color MenuBorder { get { return Edge; } }
    public override Color MenuItemBorder { get { return Hi; } }
    public override Color MenuItemSelected { get { return Hi; } }
    public override Color MenuItemSelectedGradientBegin { get { return Hi; } }
    public override Color MenuItemSelectedGradientEnd { get { return Hi; } }
    public override Color MenuItemPressedGradientBegin { get { return Hi; } }
    public override Color MenuItemPressedGradientEnd { get { return Hi; } }
    public override Color SeparatorDark { get { return Edge; } }
    public override Color SeparatorLight { get { return Edge; } }
    public override Color CheckBackground { get { return KinetikSuite.Look.Current.Hover; } }
    public override Color CheckSelectedBackground { get { return KinetikSuite.Look.Current.Hover; } }
    public override Color CheckPressedBackground { get { return KinetikSuite.Look.Current.Hover; } }
}

class DarkMenu : ToolStripProfessionalRenderer
{
    public DarkMenu() : base(new DarkColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? KinetikSuite.Look.Current.Text : KinetikSuite.Look.Current.Sub;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = KinetikSuite.Look.Current.Sub;
        base.OnRenderArrow(e);
    }
}

// ======================================================================= App
class CalApp : ApplicationContext
{
    public readonly Settings Cfg = Settings.Load();
    public WidgetForm Widget;
    public readonly ContextMenuStrip Menu = KinetikSuite.MenuFocus.Attach(new ContextMenuStrip());
    public readonly Icon AppIcon;
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Control ui = new Control(); // marshals background syncs back to the UI thread
    readonly System.Windows.Forms.Timer syncTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer minute = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer { Interval = 600 };
    SettingsForm settingsForm;
    int busy;
    bool locked;
    DateTime shownDay = DateTime.Today;

    public bool Syncing { get { return busy == 1; } }

    public CalApp(bool openSettings)
    {
        ui.CreateControl();
        var h = ui.Handle;
        byte[] iconBytes = null;
        try
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico"))
                if (s != null) { var ms = new MemoryStream(); s.CopyTo(ms); iconBytes = ms.ToArray(); }
        }
        catch { }
        AppIcon = iconBytes != null ? new Icon(new MemoryStream(iconBytes)) : SystemIcons.Application;

        Menu.Opening += (s, e) => BuildMenu();

        tray.Icon = iconBytes != null ? new Icon(new MemoryStream(iconBytes), SystemInformation.SmallIconSize) : SystemIcons.Application;
        tray.Text = Program.Demo ? "Kinetik Calendar (demo)" : "Kinetik Calendar";
        Backup.BeforeExit = () => tray.Visible = false;
        tray.ContextMenuStrip = Menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenSettings(null); };
        tray.Visible = true;

        // Show the saved copies straight away, then sync
        foreach (var f in Cfg.Feeds)
        {
            var cached = Program.Demo ? DemoData.Ics(f) : Net.ReadCache(f.Url);
            if (cached != null) { try { f.Parsed = Ics.Parse(cached); f.Status = Program.Demo ? "Demo calendar" : "Showing saved copy"; } catch { } }
        }
        CreateWidget();
        Rebuild();

        // Wakes once a minute, on the minute: enough for the clock, countdowns and progress bars
        minute.Tick += (s, e) => Minute();
        ArmMinute();
        syncTimer.Tick += (s, e) => Sync();
        ScheduleSync(1500);
        saveTimer.Tick += (s, e) => { saveTimer.Stop(); Cfg.Save(); };
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.DisplaySettingsChanged += OnDisplay;
        SystemEvents.SessionSwitch += OnSession;

        if (openSettings || Cfg.Feeds.Count == 0) OpenSettings(null);
    }

    void ArmMinute()
    {
        var now = DateTime.Now;
        minute.Stop();
        minute.Interval = Math.Max(1000, (int)(60000 - (now.Second * 1000 + now.Millisecond)) + 50);
        minute.Start();
    }

    void OnPower(object s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) KinetikSuite.Ui.Post(ui, () => { ArmMinute(); ScheduleSync(15000); }); // give the network a moment to come back
    }

    void OnDisplay(object s, EventArgs e) { KinetikSuite.Ui.Post(ui, () => Widget.Render(true)); }

    // Nothing redraws while the PC is locked; it catches up on unlock.
    void OnSession(object s, SessionSwitchEventArgs e)
    {
        KinetikSuite.Ui.Post(ui, () =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock) { locked = true; minute.Stop(); }
            else if (e.Reason == SessionSwitchReason.SessionUnlock) { locked = false; Minute(); }
        });
    }

    void Minute()
    {
        ArmMinute();
        if (locked) return;
        if (DateTime.Today != shownDay) { shownDay = DateTime.Today; Widget.P.Selected = null; Widget.P.ViewOffset = 0; Rebuild(); Sync(); }
        else Widget.Render(false);
        Widget.Shimmer();
    }

    void ScheduleSync(int ms)
    {
        syncTimer.Stop();
        syncTimer.Interval = Math.Max(1000, ms);
        syncTimer.Start();
    }

    public void Sync()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        syncTimer.Stop();
        var feeds = Cfg.Feeds.Where(f => f.On && f.Url != "").ToList();
        Widget.SyncStarted();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (var f in feeds) Net.Sync(f);
            try { ui.BeginInvoke((Action)(() => SyncDone(feeds.Any(f => f.Changed)))); } catch { }
        });
    }

    void SyncDone(bool changed)
    {
        busy = 0;
        if (changed) Rebuild(); else Widget.Render(true);
        if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.SyncFinished();
        bool anyFailed = Cfg.Feeds.Any(f => f.On && f.Failed);
        ScheduleSync(anyFailed ? 2 * 60000 : Cfg.SyncMin * 60000);
        if (changed || settingsForm == null || settingsForm.IsDisposed) Mem.Trim();
    }

    // Re-expands every calendar's events for the days on show. Cheap: no downloading or parsing.
    public void Rebuild()
    {
        var today = DateTime.Today;
        DateTime from = today, to = today.AddDays(Math.Max(1, Cfg.Days));
        var anchor = Widget.P.ViewAnchor(today); // the month (or week) the calendar has been moved to
        var gs = Cfg.MonthStyle == 1 ? anchor.AddDays(-7) : Painter.GridStart(anchor, Cfg.FirstDow);
        if (Cfg.MonthStyle != 2)
        {
            if (gs < from) from = gs;
            if (gs.AddDays(42) > to) to = gs.AddDays(42);
        }
        var sel = Widget.P.Selected;
        if (sel.HasValue)
        {
            if (sel.Value < from) from = sel.Value;
            if (sel.Value.AddDays(1) > to) to = sel.Value.AddDays(1);
        }
        var all = new List<CalEvent>();
        foreach (var f in Cfg.Feeds)
        {
            var p = f.Parsed;
            if (f.On && p != null) all.AddRange(Ics.Expand(p, f, from, to));
        }
        if (Cfg.MergeDupes) // an event you're invited to from two calendars shows once, marked in both colours
        {
            var seen = new Dictionary<string, CalEvent>();
            var merged = new List<CalEvent>(all.Count);
            foreach (var e in all)
            {
                var key = e.Title + "\u0001" + e.Start.Ticks + "\u0001" + e.End.Ticks;
                CalEvent first;
                if (!seen.TryGetValue(key, out first)) { seen[key] = e; merged.Add(e); continue; }
                if (first.Feed.Color == e.Feed.Color || (first.Also != null && first.Also.Contains(e.Feed.Color))) continue;
                if (first.Also == null) first.Also = new List<Color>(2);
                first.Also.Add(e.Feed.Color);
            }
            all = merged;
        }
        Cfg.Rev++;
        Widget.SetEvents(all);
        if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.PreviewChanged();
    }

    // Called by Settings after any change. `rebuild` when it affects which events are on show.
    void BuildMenu()
    {
        var old = Menu.Items.Cast<ToolStripItem>().ToList();
        Menu.Items.Clear();
        foreach (var it in old) it.Dispose(); // a cleared item that was shown keeps its window otherwise
        Menu.Items.Add("Sync now", null, (s, e) => Sync());
        var open = new ToolStripMenuItem("Open calendar");
        foreach (var grp in Cfg.Feeds.Where(f => f.On && Feed.WebUrl(f, DateTime.Today) != null).GroupBy(f => Feed.WebUrl(f, DateTime.Today)))
        {
            var f = grp.First();
            open.DropDownItems.Add(Feed.KindTitle(f.Kind), null, (s2, e2) => OpenWeb(f, DateTime.Today));
        }
        if (open.DropDownItems.Count == 0) { open.DropDownItems.Add("(none)"); open.Enabled = false; }
        Menu.Items.Add(open);
        Menu.Items.Add(new ToolStripSeparator());
        Common().AddAll(Menu.Items);
    }

    // The look, placement, startup and backup options, shared word for word with the other Kinetik apps.
    CommonMenu Common()
    {
        return new CommonMenu
        {
            Title = "Kinetik Calendar", Version = Program.Version,
            OpenTheme = () => OpenSettings("Look"),
            Scale = () => Cfg.Scale, SetScale = v => { Cfg.Scale = v; Apply(true); },
            Widths = new[] { 280, 320, 400 }, CardWidth = () => Cfg.Width, SetCardWidth = v => { Cfg.Width = v; Apply(true); },
            FontName = () => Cfg.FontName, SetFontName = v => { Cfg.FontName = v; Apply(true); },
            Opacity = () => Cfg.Opacity, SetOpacity = v => { Cfg.Opacity = v; Apply(false); },
            Layer = () => Cfg.Layer, SetLayer = v => { Cfg.Layer = v; Cfg.Save(); RecreateWidget(); },
            Locked = () => Cfg.Locked, SetLocked = v => { Cfg.Locked = v; Apply(false); },
            ClickThrough = () => Cfg.ClickThrough, SetClickThrough = v => { Cfg.ClickThrough = v; Apply(false); },
            Snap = () => Cfg.Snap, SetSnap = v => { Cfg.Snap = v; Apply(false); },
            OpenSettings = () => OpenSettings(null),
            RunAtStartup = () => Startup.IsOn, SetRunAtStartup = Startup.Set,
            Restore = Backup.RestoreNow, LastBackup = () => Backup.SavedText,
            BackUp = () => { Cfg.Save(); Backup.SaveNow(); },
            Exit = Quit,
        };
    }

    public void Apply(bool rebuild)
    {
        Cfg.Rev++;
        saveTimer.Stop(); saveTimer.Start();
        if (rebuild) Rebuild();
        else
        {
            Widget.Render(true);
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.PreviewChanged();
        }
    }

    void CreateWidget()
    {
        Widget = new WidgetForm(this);
        Widget.Show();
    }

    // Placement (desktop / normal / on top) is fixed when the window is created, so changing it means a new window.
    public void RecreateWidget()
    {
        var old = Widget;
        var events = old != null ? old.P.Events : new List<CalEvent>();
        CreateWidget();
        if (old != null) { old.Close(); old.Dispose(); }
        Widget.SetEvents(events);
    }

    public void OpenSettings(string page)
    {
        if (settingsForm == null || settingsForm.IsDisposed)
        {
            settingsForm = new SettingsForm(this, page ?? "Calendars");
            settingsForm.FormClosed += (s, e) => { Cfg.Save(); settingsForm = null; ui.BeginInvoke((Action)Mem.Trim); };
        }
        else if (page != null) settingsForm.ShowPage(page);
        settingsForm.Show();
        if (settingsForm.WindowState == FormWindowState.Minimized) settingsForm.WindowState = FormWindowState.Normal;
        settingsForm.Activate();
    }

    public void OpenWeb(Feed f, DateTime day)
    {
        var url = Feed.WebUrl(f, day);
        if (url == null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public void Quit()
    {
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.DisplaySettingsChanged -= OnDisplay;
        SystemEvents.SessionSwitch -= OnSession;
        tray.Visible = false;
        Cfg.Save();
        if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.Close();
        Widget.Close();
        ExitThread();
    }
}

// ======================================================================= Widget painting
// Lays out and paints the card. Used by the widget window and by the live preview in Settings.
class Painter : IDisposable
{
    public class Row
    {
        public int Kind; // 0 event, 1 day heading, 2 date title, 3 month grid, 4 note, 5 clock title, 6 week strip
        public string Label, Value, Sub, Extra;
        public CalEvent Ev;
        public bool Dim, Now, Soon, Warn, Today, Picked;
        public DateTime Day;
        public double Frac;
        public float H;
    }

    public class Hit { public RectangleF R; public CalEvent Ev; }

    readonly CalApp app;
    Settings c { get { return app.Cfg; } }
    public List<CalEvent> Events = new List<CalEvent>();
    Dictionary<DateTime, List<Color>> dayColors = new Dictionary<DateTime, List<Color>>();
    public readonly List<Hit> Hits = new List<Hit>();
    public readonly List<KeyValuePair<RectangleF, DateTime>> DayHits = new List<KeyValuePair<RectangleF, DateTime>>();
    public DateTime? Selected, HoverDay; // a day clicked in the month or week view, and the one under the mouse
    readonly SolidBrush brush = new SolidBrush(Color.White);
    readonly List<Row> head = new List<Row>(), body = new List<Row>();
    Font fLabel, fValue, fHead, fSmall, fTitle, fClock, fClockSub, fDay, fDayBold;
    string fontKey;

    public float U = 1;
    public Theme Th = Theme.All[0];
    public int WinW, WinH, Margin;
    public float CardW, CardH, Pad, BodyTop, BodyView, BodyFull;
    public string Key = "";
    public float MaxScroll { get { return Math.Max(0, BodyFull - BodyView); } }
    public float BodyBottom { get { return CardH - Pad + Gap; } }

    // The scrollbar track and thumb, in card coordinates (add Margin for window coordinates)
    public float ThumbH { get { float viewH = BodyBottom - BodyTop; return Math.Min(viewH, Math.Max(24 * U, viewH * BodyView / Math.Max(1, BodyFull))); } }
    public float ThumbY { get { return BodyTop + (BodyBottom - BodyTop - ThumbH) * (MaxScroll > 0 ? Scroll / MaxScroll : 0); } }

    // Animation state, set by the window
    public bool ThumbDrag;
    public float Scroll, Appear = 1, HoverA, Shimmer = -1, Spin = -1, DrawCardH = -1;
    public CalEvent Hover;

    static readonly StringFormat Trim = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
    static readonly StringFormat Center = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    static readonly StringFormat Typo = MakeTypo();

    static StringFormat MakeTypo()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        return f;
    }

    public Painter(CalApp app) { this.app = app; }

    // Set by the widget window, which fades a solid card itself; PushAlpha is then the opacity to show it at.
    public bool WindowAlpha;
    public int PushAlpha = 255;
    readonly ShadowCache shadow = new ShadowCache();

    public void Dispose() { DisposeFonts(); brush.Dispose(); if (layer != null) layer.Dispose(); shadow.Dispose(); }

    public void SetEvents(List<CalEvent> list)
    {
        Events = list;
        var dc = new Dictionary<DateTime, List<Color>>();
        foreach (var e in list)
        {
            var last = e.End > e.Start ? e.End.AddTicks(-1).Date : e.Start.Date;
            int n = 0;
            for (var d = e.Start.Date; d <= last && n < 60; d = d.AddDays(1), n++)
            {
                List<Color> cols;
                if (!dc.TryGetValue(d, out cols)) dc[d] = cols = new List<Color>(3);
                if (!cols.Contains(e.Feed.Color)) cols.Add(e.Feed.Color);
                if (e.Also != null) foreach (var a in e.Also) if (!cols.Contains(a)) cols.Add(a);
            }
        }
        dayColors = dc;
    }

    public void CopyFrom(Painter p) { Events = p.Events; dayColors = p.dayColors; }

    // ---- fonts
    void EnsureFonts()
    {
        var fam = c.FontName != "" && Draw.Has(c.FontName) ? c.FontName : Theme.IsTerm(c.ThemeName) ? Theme.Mono : Draw.Family;
        var key = fam + "|" + U.ToString("0.000");
        if (fontKey == key) return;
        DisposeFonts();
        fLabel = new Font(fam, 12.5f * U, FontStyle.Regular, GraphicsUnit.Pixel);
        fValue = new Font(fam, 12.5f * U, FontStyle.Bold, GraphicsUnit.Pixel);
        fHead = new Font(fam, 10.5f * U, FontStyle.Bold, GraphicsUnit.Pixel);
        fSmall = new Font(fam, 11.5f * U, FontStyle.Regular, GraphicsUnit.Pixel);
        fTitle = new Font(fam, 15f * U, FontStyle.Bold, GraphicsUnit.Pixel);
        fClock = new Font(Draw.Has(fam + " Light") ? fam + " Light" : Draw.Has("Segoe UI Light") && fam.StartsWith("Segoe") ? "Segoe UI Light" : fam, 36f * U, FontStyle.Regular, GraphicsUnit.Pixel);
        fClockSub = new Font(fam, 13f * U, FontStyle.Regular, GraphicsUnit.Pixel);
        fDay = new Font(fam, 11.5f * U, FontStyle.Regular, GraphicsUnit.Pixel);
        fDayBold = new Font(fam, 11.5f * U, FontStyle.Bold, GraphicsUnit.Pixel);
        fontKey = key;
    }

    void DisposeFonts()
    {
        foreach (var f in new[] { fLabel, fValue, fHead, fSmall, fTitle, fClock, fClockSub, fDay, fDayBold }) if (f != null) f.Dispose();
        fLabel = fValue = fHead = fSmall = fTitle = fClock = fClockSub = fDay = fDayBold = null;
        fontKey = null;
    }

    // ---- dates
    public static DateTime GridStart(DateTime today, DayOfWeek fdw)
    {
        var first = new DateTime(today.Year, today.Month, 1);
        return first.AddDays(-(((int)first.DayOfWeek - (int)fdw + 7) % 7));
    }

    static int Weeks(DateTime today, DayOfWeek fdw)
    {
        int lead = (new DateTime(today.Year, today.Month, 1) - GridStart(today, fdw)).Days;
        return (lead + DateTime.DaysInMonth(today.Year, today.Month) + 6) / 7;
    }

    string Time(DateTime t)
    {
        if (c.Use24h) return t.ToString("HH:mm");
        return t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();
    }

    static string Span(TimeSpan d)
    {
        int mins = (int)Math.Round(d.TotalMinutes);
        if (mins < 60) return mins + "m";
        if (mins >= 1440 && mins % 1440 == 0) return (mins / 1440) + "d";
        return mins % 60 == 0 ? (mins / 60) + "h" : (mins / 60) + "h " + (mins % 60) + "m";
    }

    // ---- rows
    float LineH { get { return (c.Density == 0 ? 18 : c.Density == 2 ? 22 : 20) * U; } }
    float Gap { get { return (c.Density == 0 ? 3 : c.Density == 2 ? 10 : 6) * U; } }
    float SubH { get { return (c.Density == 0 ? 14 : 15) * U; } }

    float Height(Row r, bool firstInCard)
    {
        switch (r.Kind)
        {
            case 0: return LineH + (r.Sub != null ? SubH : 0) + (r.Now && c.ProgressBars ? 9 * U : 0) + Gap;
            case 1: return firstInCard ? 18 * U : (c.Density == 0 ? 24 : c.Density == 2 ? 34 : 30) * U;
            case 2: return 30 * U;
            case 3: return (NavH + 18 + Weeks(ViewAnchor(DateTime.Today), c.FirstDow) * 23 + 4) * U;
            case 5: return 66 * U;
            case 6: return (NavH + 18 + 23 + 6) * U;
            default: return 24 * U;
        }
    }

    void Build()
    {
        head.Clear(); body.Clear();
        var now = DateTime.Now;
        var today = now.Date;
        int failed = c.Feeds.Count(f => f.On && f.Failed);
        string status = failed > 0 ? "⚠ " + failed + " offline" : null;
        if (c.TitleStyle == 0) head.Add(new Row { Kind = 2, Label = today.ToString("dddd d MMMM"), Sub = status, Warn = failed > 0 });
        else if (c.TitleStyle == 1)
        {
            var clock = c.Use24h ? now.ToString("HH:mm") : now.ToString("h:mm", CultureInfo.InvariantCulture);
            head.Add(new Row { Kind = 5, Label = clock, Value = c.Use24h ? null : now.ToString("tt", CultureInfo.InvariantCulture).ToLowerInvariant(), Extra = today.ToString("dddd d MMMM"), Sub = status, Warn = failed > 0 });
        }
        if (c.MonthStyle == 0) head.Add(new Row { Kind = 3 });
        else if (c.MonthStyle == 1) head.Add(new Row { Kind = 6 });

        if (!c.Feeds.Any(f => f.On))
        {
            body.Add(new Row { Kind = 1, Label = "NO CALENDARS YET" });
            body.Add(new Row { Kind = 4, Label = "Right-click → Settings… to add one" });
            return;
        }

        int days = Math.Max(1, c.Days);
        if (Selected.HasValue && (Selected.Value < today || Selected.Value >= today.AddDays(days)))
            AddDay(Selected.Value, today, now, true); // a day clicked outside the agenda's range goes on top
        bool anyLater = false;
        for (int d = 0; d < days; d++)
            if (AddDay(today.AddDays(d), today, now, false) && d > 0) anyLater = true;
        if (!anyLater && c.Days > 1) body.Add(new Row { Kind = 4, Label = "Nothing in the next " + (c.Days - 1) + " days" });
    }

    bool AddDay(DateTime day, DateTime today, DateTime now, bool extra)
    {
        var next = day.AddDays(1);
        bool picked = Selected.HasValue && Selected.Value == day;
        var list = Events.Where(e => e.Start < next && (e.End > day || (e.End == e.Start && e.Start >= day)))
                         .Where(e => c.ShowAllDay || !e.AllDay)
                         .Where(e => day != today || c.ShowPast || e.AllDay || e.End > now || (e.End == e.Start && e.Start >= now))
                         .OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start < day ? day : e.Start).ThenBy(e => e.Title).ToList();
        if (list.Count == 0 && day != today && !c.ShowEmptyDays && !picked) return false;
        int d = (day - today).Days;
        var label = d == 0 ? "Today" : d == 1 ? "Tomorrow" : d == -1 ? "Yesterday" : day.ToString("dddd");
        body.Add(new Row { Kind = 1, Today = d == 0, Picked = picked, Day = day, Label = label.ToUpper(), Sub = extra || d < 0 || d > 6 ? day.ToString("ddd d MMM") : day.ToString("d MMM") });
        if (list.Count == 0) body.Add(new Row { Kind = 4, Label = d == 0 ? "Nothing else today" : "Nothing on" });
        int shown = c.MaxPerDay > 0 && !picked ? Math.Min(c.MaxPerDay, list.Count) : list.Count;
        for (int i = 0; i < shown; i++) body.Add(EventRow(list[i], day, next, now));
        if (shown < list.Count) body.Add(new Row { Kind = 4, Label = "+" + (list.Count - shown) + " more" });
        return true;
    }

    // Where a day's heading sits in the scrolling agenda, or -1.
    public float OffsetOf(DateTime day)
    {
        float y = 0;
        foreach (var r in body)
        {
            if (r.Kind == 1 && r.Day == day) return y;
            y += r.H;
        }
        return -1;
    }

    Row EventRow(CalEvent e, DateTime day, DateTime next, DateTime now)
    {
        var r = new Row { Kind = 0, Ev = e, Label = e.Title };
        var details = new List<string>();
        if (e.AllDay)
        {
            r.Value = "All day";
            int total = (int)Math.Round((e.End - e.Start).TotalDays);
            if (total > 1) details.Add("Day " + ((day - e.Start.Date).Days + 1) + " of " + total);
        }
        else
        {
            bool startsToday = e.Start >= day, endsToday = e.End <= next;
            r.Value = startsToday ? Time(e.Start) : endsToday ? "until " + Time(e.End) : "All day";
            if (now >= e.Start && now < e.End)
            {
                r.Now = true;
                r.Frac = (now - e.Start).TotalSeconds / Math.Max(1, (e.End - e.Start).TotalSeconds);
                r.Value = "now";
                details.Add((endsToday ? "until " + Time(e.End) : "until " + e.End.ToString("ddd") + " " + Time(e.End)) + ", " + Span(e.End - now) + " left");
            }
            else
            {
                if (c.Countdown && e.Start > now && e.Start - now <= TimeSpan.FromMinutes(60))
                {
                    r.Soon = true;
                    r.Value = "in " + Math.Max(1, (int)Math.Ceiling((e.Start - now).TotalMinutes)) + " min";
                }
                if (startsToday && e.End > e.Start)
                    details.Add(endsToday ? Time(e.Start) + " – " + Time(e.End) + "  (" + Span(e.End - e.Start) + ")" : "to " + e.End.ToString("ddd") + " " + Time(e.End));
                r.Dim = e.End < now || (e.End == e.Start && e.Start < now);
            }
        }
        if (c.ShowLocation && e.Location != null) details.Add(e.Location);
        if (c.ShowDetails && details.Count > 0) r.Sub = string.Join("  ·  ", details);
        return r;
    }

    // ---- layout
    public void Layout(float u, int maxWinH)
    {
        U = u;
        Th = Theme.Resolve(c);
        EnsureFonts();
        Build();
        float headH = 0;
        for (int i = 0; i < head.Count; i++) { head[i].H = Height(head[i], i == 0); headH += head[i].H; }
        BodyFull = 0;
        for (int i = 0; i < body.Count; i++) { body[i].H = Height(body[i], i == 0 && head.Count == 0); BodyFull += body[i].H; }
        if (head.Count > 0 && body.Count > 0 && head[head.Count - 1].Kind == 3) headH -= 4 * U;

        Pad = 14 * U;
        Margin = c.Shadow && c.Opacity >= 25 ? (int)Math.Ceiling(14 * U) : 1;
        CardW = Math.Max(200, (int)(c.Width * U));
        float full = Pad * 2 + headH + BodyFull - Gap;
        float maxCard = Math.Max(160 * U, maxWinH - Margin * 2);
        if (c.MaxHeight > 0) maxCard = Math.Max(160 * U, Math.Min(maxCard, c.MaxHeight * U));
        CardH = (float)Math.Ceiling(Math.Min(full, maxCard));
        BodyTop = Pad + headH;
        BodyView = CardH - BodyTop - Pad + Gap;
        WinW = (int)CardW + Margin * 2;
        WinH = (int)CardH + Margin * 2;

        var sb = new StringBuilder();
        sb.Append(c.Rev).Append('|').Append(WinW).Append('x').Append(WinH).Append('|').Append(Th.Name).Append('|').Append(Selected).Append('|').Append(HoverDay).Append('|').Append(ViewOffset).Append('|').Append(HoverNav);
        foreach (var r in head.Concat(body))
            sb.Append('|').Append(r.Kind).Append(r.Label).Append(r.Value).Append(r.Sub).Append(r.Extra).Append(r.Dim).Append(r.Now ? r.Frac.ToString("0.00") : "");
        Key = sb.ToString();
    }

    // ---- painting
    Bitmap layer;

    public void Paint(Graphics target)
    {
        target.Clear(Color.Transparent);
        target.SmoothingMode = SmoothingMode.AntiAlias;
        target.PixelOffsetMode = PixelOffsetMode.HighQuality;
        Hits.Clear();
        DayHits.Clear();
        NavPrev = NavNext = NavToday = GridArea = RectangleF.Empty;

        int alpha = (int)Math.Round(c.Opacity * 2.55);
        float cardH = DrawCardH > 0 ? DrawCardH : CardH;
        var card = new RectangleF(Margin, Margin, CardW, cardH);
        float rad = Math.Min(card.Height / 2, 12 * U * c.Round / 100f);
        var size = target.VisibleClipBounds;
        int lw = (int)Math.Ceiling(size.Width), lh = (int)Math.Ceiling(size.Height);

        // ClearType needs solid pixels behind the text. So for a mostly opaque card, the card is drawn solid with ClearType text
        // and then faded to the card's opacity as a whole: by the widget window itself (PushAlpha, free), or else by blending
        // a layer in (a few ms a frame). A very see-through card keeps grayscale text.
        bool crisp = alpha >= 150, byWindow = crisp && WindowAlpha;
        PushAlpha = byWindow ? alpha : 255;
        int shadowA = byWindow ? 255 : alpha; // the window's fade then brings it to the same strength
        if (Margin > 1) // soft shadow: stacked, slightly larger translucent shapes
            shadow.Paint(target, lw, lh, card + "|" + rad + "|" + U + "|" + shadowA, sg =>
            {
                for (int k = 7; k >= 1; k--)
                {
                    var r = card;
                    r.Inflate(k * 1.6f * U, k * 1.6f * U);
                    r.Offset(0, 2 * U);
                    Draw.Fill(sg, r, rad + k * 1.6f * U, Color.FromArgb(Math.Max(1, 7 * shadowA / 255), 0, 0, 0));
                }
            });

        Graphics g = target;
        if (byWindow)
        {
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.TextContrast = 2;
        }
        else if (crisp)
        {
            if (layer == null || layer.Width != lw || layer.Height != lh) { if (layer != null) layer.Dispose(); layer = new Bitmap(lw, lh, PixelFormat.Format32bppPArgb); }
            g = Graphics.FromImage(layer);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.TextContrast = 2;
        }
        else g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var bg = crisp ? Th.Bg : Color.FromArgb(Math.Max(1, alpha), Th.Bg); // alpha ≥ 1 so the whole card still catches clicks
        Draw.Fill(g, card, rad, bg);
        if (c.Border && alpha > 0) Draw.Stroke(g, card, rad, Th.Line, 1);

        var state = g.Save();
        using (var clip = Draw.Round(card, rad)) g.SetClip(clip);
        float x0 = card.X + Pad, x1 = card.Right - Pad, y = card.Y + Pad;
        int idx = 0;
        foreach (var r in head) { PaintRow(g, r, x0, x1, y + Slide(idx++), card); y += r.H; }

        // The agenda scrolls under the pinned title and month when it's taller than the screen
        float bodyTop = card.Y + BodyTop, bodyBottom = card.Bottom - Pad + Gap;
        g.SetClip(RectangleF.FromLTRB(card.X, bodyTop - 2 * U, card.Right, Math.Max(bodyTop, bodyBottom)), CombineMode.Intersect);
        y = bodyTop - Scroll;
        foreach (var r in body)
        {
            if (y + r.H >= bodyTop - 2 * U && y <= bodyBottom) PaintRow(g, r, x0, x1, y + Slide(idx), card);
            idx++;
            y += r.H;
        }
        if (MaxScroll > 0.5f)
        {
            float f = 16 * U;
            if (Scroll > 0.5f)
                using (var lg = new LinearGradientBrush(new RectangleF(card.X, bodyTop - 2 * U, card.Width, f + 1), bg, Color.FromArgb(0, Th.Bg), 90f))
                    g.FillRectangle(lg, card.X, bodyTop - 2 * U, card.Width, f);
            if (Scroll < MaxScroll - 0.5f)
                using (var lg = new LinearGradientBrush(new RectangleF(card.X, bodyBottom - f - 1, card.Width, f + 1), Color.FromArgb(0, Th.Bg), bg, 90f))
                    g.FillRectangle(lg, card.X, bodyBottom - f, card.Width, f);
            float thumb = ThumbH, ty = card.Y + ThumbY, w = (ThumbDrag ? 5 : 4) * U;
            Draw.Fill(g, new RectangleF(card.Right - 3 * U - w, bodyTop, w, bodyBottom - bodyTop), w / 2, Color.FromArgb(22, Th.Sub));
            Draw.Fill(g, new RectangleF(card.Right - 3 * U - w, ty, w, thumb), w / 2, Color.FromArgb(ThumbDrag ? 150 : 95, Th.Sub));
        }
        g.Restore(state);
        if (crisp && !byWindow)
        {
            g.Dispose();
            if (alpha >= 255) target.DrawImageUnscaled(layer, 0, 0);
            else
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha / 255f });
                    target.DrawImage(layer, new Rectangle(0, 0, layer.Width, layer.Height), 0, 0, layer.Width, layer.Height, GraphicsUnit.Pixel, ia);
                }
        }
    }

    // Rows rise into place one after another while the widget fades in.
    float Slide(int i)
    {
        if (Appear >= 1) return 0;
        float t = Draw.Ease(Appear * 1.7f - i * 0.05f);
        return (1 - t) * 10 * U;
    }

    void PaintRow(Graphics g, Row it, float x0, float x1, float y, RectangleF card)
    {
        switch (it.Kind)
        {
            case 0: DrawEvent(g, it, x0, x1, y); break;
            case 1:
            {
                float ty = y + it.H - 16 * U;
                var hsz = g.MeasureString(it.Label, fHead, PointF.Empty, Typo);
                brush.Color = it.Today || it.Picked ? Th.Accent : Th.Sub;
                g.DrawString(it.Label, fHead, brush, x0, ty, Typo);
                if (!string.IsNullOrEmpty(it.Sub))
                {
                    var ssz = g.MeasureString(it.Sub, fSmall, PointF.Empty, Typo);
                    brush.Color = Color.FromArgb(215, Th.Sub);
                    g.DrawString(it.Sub, fSmall, brush, x1 - ssz.Width, ty + (hsz.Height - ssz.Height) / 2, Typo);
                    using (var p = new Pen(Th.Line, 1)) // a hairline between the heading and the date, like a ruled diary
                        g.DrawLine(p, x0 + hsz.Width + 8 * U, ty + hsz.Height / 2, x1 - ssz.Width - 8 * U, ty + hsz.Height / 2);
                }
                break;
            }
            case 2:
            {
                float sw = Status(g, it, x1, y, 22 * U);
                brush.Color = Th.Text;
                g.DrawString(it.Label, fTitle, brush, new RectangleF(x0, y, Math.Max(1, x1 - x0 - sw - 8 * U), 22 * U), Trim);
                break;
            }
            case 5:
            {
                var tsz = g.MeasureString(it.Label, fClock, PointF.Empty, Typo);
                brush.Color = Th.Text;
                g.DrawString(it.Label, fClock, brush, x0 - 2 * U, y - 4 * U, Typo);
                if (it.Value != null)
                {
                    brush.Color = Th.Sub;
                    g.DrawString(it.Value, fClockSub, brush, x0 + tsz.Width + 2 * U, y + tsz.Height - 24 * U, Typo);
                }
                brush.Color = Th.Accent;
                g.DrawString(it.Extra, fValue, brush, new RectangleF(x0, y + 40 * U, x1 - x0, 18 * U), Trim);
                Status(g, it, x1, y, 22 * U);
                break;
            }
            case 3: PaintMonth(g, x0, x1, y, false); break;
            case 6: PaintMonth(g, x0, x1, y, true); break;
            default:
                brush.Color = Th.Sub;
                g.DrawString(it.Label, fLabel, brush, new RectangleF(x0, y, x1 - x0, 20 * U), Trim);
                break;
        }
    }

    // The sync spinner or the offline warning at the right of the title. Returns its width.
    float Status(Graphics g, Row it, float x1, float y, float h)
    {
        if (Spin >= 0)
        {
            float d = 12 * U;
            using (var p = new Pen(Color.FromArgb(70, Th.Sub), 1.8f * U)) g.DrawEllipse(p, x1 - d, y + (h - d) / 2, d, d);
            using (var p = new Pen(Th.Accent, 1.8f * U) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(p, x1 - d, y + (h - d) / 2, d, d, Spin * 360, 100);
            return d;
        }
        if (it.Sub == null) return 0;
        var ssz = g.MeasureString(it.Sub, fSmall, PointF.Empty, Typo);
        brush.Color = it.Warn ? Color.FromArgb(255, 170, 60) : Th.Sub;
        g.DrawString(it.Sub, fSmall, brush, x1 - ssz.Width, y + (h - ssz.Height) / 2, Typo);
        return ssz.Width;
    }

    void DrawEvent(Graphics g, Row r, float x0, float x1, float y)
    {
        float lineH = LineH, lx;
        var col = r.Ev.Feed.Color;
        int a = r.Dim ? 100 : 255;
        var rowRect = new RectangleF(x0 - 6 * U, y - 1 * U, x1 - x0 + 12 * U, r.H - Gap + 2 * U);
        if (Hover == r.Ev && HoverA > 0.01f)
            Draw.Fill(g, rowRect, 7 * U, Color.FromArgb((int)(HoverA * (Th.Light ? 16 : 22)), Th.Light ? Color.Black : Color.White));
        float barH = lineH - 6 * U + (r.Sub != null ? SubH : 0);
        switch (c.Marker)
        {
            case 1: // dot
            {
                float d = 8 * U;
                int n = r.Ev.Also == null ? 1 : 1 + r.Ev.Also.Count;
                for (int k = n - 1; k >= 0; k--)
                    using (var b = new SolidBrush(Color.FromArgb(a, k == 0 ? col : r.Ev.Also[k - 1]))) g.FillEllipse(b, x0 + k * 5 * U, y + (lineH - d) / 2, d, d);
                lx = x0 + 16 * U + (n - 1) * 5 * U;
                break;
            }
            case 2: // tinted card
                Draw.Fill(g, rowRect, 7 * U, Color.FromArgb(r.Dim ? 16 : 38, col));
                Draw.Fill(g, new RectangleF(rowRect.X, rowRect.Y + 4 * U, 3 * U, rowRect.Height - 8 * U), 1.5f * U, Color.FromArgb(a, col));
                lx = x0 + 6 * U;
                break;
            case 3: // none
                lx = x0;
                break;
            default: // bar
                if (r.Ev.Also == null) Draw.Fill(g, new RectangleF(x0, y + 3 * U, 4 * U, barH), 2 * U, Color.FromArgb(a, col));
                else // one segment per calendar
                {
                    int n = 1 + r.Ev.Also.Count;
                    float seg = (barH - (n - 1) * 2 * U) / n;
                    for (int k = 0; k < n; k++)
                        Draw.Fill(g, new RectangleF(x0, y + 3 * U + k * (seg + 2 * U), 4 * U, seg), 2 * U, Color.FromArgb(a, k == 0 ? col : r.Ev.Also[k - 1]));
                }
                lx = x0 + 13 * U;
                break;
        }

        var vsz = g.MeasureString(r.Value ?? "", fValue, PointF.Empty, Typo);
        brush.Color = r.Now || r.Soon ? (c.Marker == 3 ? Th.Accent : col) : r.Dim ? Color.FromArgb(150, Th.Sub) : Th.Text;
        g.DrawString(r.Value ?? "", fValue, brush, x1 - vsz.Width, y + (lineH - vsz.Height) / 2, Typo);
        brush.Color = Color.FromArgb(r.Dim ? 140 : 240, Th.Text);
        g.DrawString(r.Label, r.Now ? fValue : fLabel, brush, new RectangleF(lx, y, Math.Max(1, x1 - vsz.Width - 10 * U - lx), lineH), Trim);

        float yy = y + lineH;
        if (r.Sub != null)
        {
            brush.Color = Color.FromArgb(r.Dim ? 130 : 255, Th.Sub);
            g.DrawString(r.Sub, fSmall, brush, new RectangleF(lx, yy - 4 * U, Math.Max(1, x1 - lx), SubH), Trim);
            yy += SubH;
        }
        if (r.Now && c.ProgressBars)
        {
            var tr = new RectangleF(lx, yy + 1 * U, x1 - lx, 5 * U);
            Draw.Fill(g, tr, tr.Height / 2, Th.Track);
            float fw = (float)(tr.Width * Math.Max(0, Math.Min(1, r.Frac)));
            if (fw >= 1)
            {
                var fr = new RectangleF(tr.X, tr.Y, Math.Max(fw, tr.Height), tr.Height);
                Draw.Fill(g, fr, tr.Height / 2, col);
                if (Shimmer >= 0 && Shimmer <= 1) // a glint that runs along the bar once a minute
                {
                    float bw = 36 * U, cx = fr.X - bw + (fr.Width + bw * 2) * Shimmer;
                    var st = g.Save();
                    using (var clip = Draw.Round(fr, tr.Height / 2)) g.SetClip(clip, CombineMode.Intersect);
                    var band = new RectangleF(cx - bw / 2, fr.Y, bw, fr.Height);
                    using (var lg = new LinearGradientBrush(band, Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0f))
                    {
                        var blend = new ColorBlend(3);
                        blend.Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(150, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) };
                        blend.Positions = new[] { 0f, 0.5f, 1f };
                        lg.InterpolationColors = blend;
                        g.FillRectangle(lg, band);
                    }
                    g.Restore(st);
                }
            }
        }
        Hits.Add(new Hit { R = rowRect, Ev = r.Ev });
    }

    // ---- moving between months (or weeks, in the week view)
    const float NavH = 24;
    public int ViewOffset;        // months (or weeks) away from now
    public float GridShift;       // horizontal slide while changing month, in pixels
    public int HoverNav;          // -1 previous, 1 next, 2 today, 0 none
    public RectangleF NavPrev, NavNext, NavToday, GridArea;

    public DateTime ViewAnchor(DateTime today)
    {
        if (c.MonthStyle == 1)
            return today.AddDays(-(((int)today.DayOfWeek - (int)c.FirstDow + 7) % 7)).AddDays(7 * ViewOffset);
        return new DateTime(today.Year, today.Month, 1).AddMonths(ViewOffset);
    }

    void PaintNav(Graphics g, float x0, float x1, float y, bool weekOnly, DateTime anchor)
    {
        string label = weekOnly
            ? (ViewOffset == 0 ? "This week" : ViewOffset == 1 ? "Next week" : ViewOffset == -1 ? "Last week" : "Week of " + anchor.ToString("d MMM"))
            : anchor.Year == DateTime.Today.Year ? anchor.ToString("MMMM") : anchor.ToString("MMMM yyyy");
        brush.Color = ViewOffset == 0 ? Th.Text : Th.Accent;
        g.DrawString(label, fValue, brush, new RectangleF(x0, y, x1 - x0 - 90 * U, 18 * U), Trim);

        float bw = 22 * U, bh = 20 * U, by = y - 1 * U;
        NavNext = new RectangleF(x1 - bw, by, bw, bh);
        NavPrev = new RectangleF(x1 - bw * 2 - 2 * U, by, bw, bh);
        NavToday = RectangleF.Empty;
        if (ViewOffset != 0)
        {
            float tw = g.MeasureString("Today", fSmall, PointF.Empty, Typo).Width + 14 * U;
            NavToday = new RectangleF(NavPrev.X - tw - 6 * U, by + 1 * U, tw, bh - 2 * U);
            Draw.Fill(g, NavToday, NavToday.Height / 2, Color.FromArgb(HoverNav == 2 ? 70 : 40, Th.Accent));
            brush.Color = Th.Accent;
            g.DrawString("Today", fSmall, brush, NavToday, Center);
        }
        var hover = Color.FromArgb(Th.Light ? 16 : 24, Th.Text);
        if (HoverNav == -1) Draw.Fill(g, NavPrev, 6 * U, hover);
        if (HoverNav == 1) Draw.Fill(g, NavNext, 6 * U, hover);
        using (var p = new Pen(Th.Sub, 1.6f * U) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            float s = 4 * U;
            PointF a = new PointF(NavPrev.X + bw / 2 + s / 2, NavPrev.Y + bh / 2 - s), b = new PointF(NavPrev.X + bw / 2 - s / 2, NavPrev.Y + bh / 2), c2 = new PointF(a.X, NavPrev.Y + bh / 2 + s);
            g.DrawLines(p, new[] { a, b, c2 });
            a = new PointF(NavNext.X + bw / 2 - s / 2, NavNext.Y + bh / 2 - s); b = new PointF(NavNext.X + bw / 2 + s / 2, NavNext.Y + bh / 2); c2 = new PointF(a.X, NavNext.Y + bh / 2 + s);
            g.DrawLines(p, new[] { a, b, c2 });
        }
    }

    void PaintMonth(Graphics g, float x0, float x1, float y, bool weekOnly)
    {
        var today = DateTime.Today;
        var fdw = c.FirstDow;
        var anchor = ViewAnchor(today);
        PaintNav(g, x0, x1, y, weekOnly, anchor);
        y += NavH * U;
        var start = weekOnly ? anchor : GridStart(anchor, fdw);
        int weeks = weekOnly ? 1 : Weeks(anchor, fdw);
        GridArea = new RectangleF(x0, y - NavH * U, x1 - x0, (NavH + 18) * U + 23 * U * weeks);
        var st = g.Save();
        if (Math.Abs(GridShift) > 0.5f) // slides in from the side it's coming from
        {
            g.SetClip(new RectangleF(x0 - 4 * U, y - 4 * U, x1 - x0 + 8 * U, 18 * U + 23 * U * weeks + 10 * U), CombineMode.Intersect);
            g.TranslateTransform(GridShift, 0);
        }
        float wn = c.WeekNumbers ? 24 * U : 0;
        float cw = (x1 - x0 - wn) / 7, rh = 23 * U, top = y + 18 * U;
        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;

        for (int i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)(((int)fdw + i) % 7);
            float cx0 = x0 + wn + cw * i;
            if (c.ShadeWeekends && (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday))
                Draw.Fill(g, new RectangleF(cx0 + 2 * U, y - 2 * U, cw - 4 * U, 18 * U + rh * weeks + 2 * U), 7 * U, Color.FromArgb(Th.Light ? 9 : 10, Th.Text));
            var name = dtf.GetShortestDayName(dow);
            brush.Color = Color.FromArgb(215, Th.Sub);
            g.DrawString(name.Substring(0, Math.Min(2, name.Length)), fHead, brush, new RectangleF(cx0, y, cw, 14 * U), Center);
        }
        if (wn > 0)
        {
            brush.Color = Color.FromArgb(150, Th.Sub);
            g.DrawString("Wk", fHead, brush, new RectangleF(x0, y, wn, 14 * U), Center);
        }
        float pop = Draw.Back(Appear * 1.4f - 0.3f);
        for (int w = 0; w < weeks; w++)
        {
            if (wn > 0)
            {
                int wk = CultureInfo.CurrentCulture.Calendar.GetWeekOfYear(start.AddDays(w * 7 + 3), CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
                brush.Color = Color.FromArgb(150, Th.Sub);
                g.DrawString(wk.ToString(), fSmall, brush, new RectangleF(x0, top + rh * w, wn, 18 * U), Center);
            }
            for (int i = 0; i < 7; i++)
            {
                var day = start.AddDays(w * 7 + i);
                float cx = x0 + wn + cw * (i + 0.5f), cy = top + rh * w + 9 * U;
                bool isToday = day == today, inMonth = weekOnly || day.Month == anchor.Month;
                bool picked = Selected.HasValue && Selected.Value == day;
                var cell = new RectangleF(cx - cw / 2 + 1 * U, cy - 10 * U, cw - 2 * U, rh - 1 * U);
                DayHits.Add(new KeyValuePair<RectangleF, DateTime>(cell, day));
                if (HoverDay.HasValue && HoverDay.Value == day && !isToday)
                    Draw.Fill(g, cell, 6 * U, Color.FromArgb(Th.Light ? 14 : 20, Th.Text));
                if (isToday || picked)
                {
                    // Centre the circle on the digits as they're actually drawn: text layout pads the box by a
                    // font-dependent amount, so the box's centre isn't where the glyphs land.
                    var font = isToday ? fDayBold : fDay;
                    float ccx = cx, ccy = cy;
                    using (var path = new GraphicsPath())
                    {
                        path.AddString(day.Day.ToString(), font.FontFamily, (int)font.Style, font.Size, new RectangleF(cx - cw / 2, cy - 9 * U, cw, 18 * U), Center);
                        var gb = path.GetBounds();
                        if (gb.Width > 0) { ccx = gb.X + gb.Width / 2; ccy = gb.Y + gb.Height / 2; }
                    }
                    float rr = 10 * U * pop;
                    if (isToday && rr > 0.5f) using (var b = new SolidBrush(Th.Accent)) g.FillEllipse(b, ccx - rr, ccy - rr, rr * 2, rr * 2);
                    if (picked) // the clicked day gets a ring
                    {
                        float pr = isToday ? 12 * U : 10 * U;
                        using (var p = new Pen(Th.Accent, 1.6f * U)) g.DrawEllipse(p, ccx - pr, ccy - pr, pr * 2, pr * 2);
                    }
                }
                bool accentLight = Th.Accent.GetBrightness() > 0.7f;
                brush.Color = isToday ? (accentLight ? Color.FromArgb(20, 20, 24) : Color.White) : !inMonth ? Color.FromArgb(70, Th.Sub) : day < today ? Th.Sub : Th.Text;
                g.DrawString(day.Day.ToString(), isToday ? fDayBold : fDay, brush, new RectangleF(cx - cw / 2, cy - 9 * U, cw, 18 * U), Center);
                List<Color> cols;
                if (!isToday && dayColors.TryGetValue(day, out cols)) // a dot for each calendar with something on
                {
                    int n = Math.Min(3, cols.Count);
                    float dr = 1.7f * U, gap = 5 * U, dx = cx - (n - 1) * gap / 2;
                    for (int k = 0; k < n; k++)
                        using (var b = new SolidBrush(Color.FromArgb(inMonth && day >= today ? 255 : 110, cols[k])))
                            g.FillEllipse(b, dx + k * gap - dr, cy + 10 * U - dr, dr * 2, dr * 2);
                }
            }
        }
        g.Restore(st);
    }
}

// ======================================================================= Widget window
class WidgetForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    readonly CalApp app;
    readonly Settings c;
    public readonly int Layer;
    public readonly Painter P;
    readonly DibSurface surface = new DibSurface();
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    double appearAt = -1, heightAt = -1, shimmerAt = -1, last;
    float heightFrom, hoverTarget, scrollTarget, slideFrom;
    double slideAt = -1;
    string lastKey;
    float lastCardH;
    bool dragging, moved, clickThrough, shown;
    Point grab, downAt;
    float thumbGrab;

    public WidgetForm(CalApp app)
    {
        this.app = app; c = app.Cfg; Layer = c.Layer;
        P = new Painter(app) { WindowAlpha = true };
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Kinetik Calendar";
        anim.Tick += (s, e) => Animate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // LAYERED | TOOLWINDOW | NOACTIVATE
            if (Layer == 2) cp.ExStyle |= 0x8;          // TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE → MA_NOACTIVATE: clicking it never steals focus
        if (m.Msg == 0x46 && Layer == 0) // WM_WINDOWPOSCHANGING: "on the desktop" keeps it underneath every other window
        {
            var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
            if ((wp.flags & 0x4) == 0) { wp.hwndInsertAfter = new IntPtr(1); Marshal.StructureToPtr(wp, m.LParam, false); }
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        surface.Dispose();
        if (disposing) { anim.Dispose(); P.Dispose(); }
        base.Dispose(disposing);
    }

    public void SetEvents(List<CalEvent> list)
    {
        P.SetEvents(list);
        Render(true);
    }

    // ---- animation: one timer, running only while something is moving
    double Now { get { return clock.Elapsed.TotalMilliseconds; } }
    void Kick() { if (!anim.Enabled) { last = Now; anim.Start(); } }

    public void SyncStarted()
    {
        if (c.Animate) { P.Spin = 0; Kick(); }
        else Render(true);
    }

    public void Shimmer()
    {
        if (!c.Animate || !P.Events.Any(e => !e.AllDay && e.Start <= DateTime.Now && e.End > DateTime.Now)) return;
        shimmerAt = Now; Kick();
    }

    void Animate()
    {
        double now = Now, dt = Math.Min(50, now - last);
        last = now;
        bool active = false;
        if (appearAt >= 0)
        {
            P.Appear = (float)Math.Min(1, (now - appearAt) / 550);
            if (P.Appear >= 1) appearAt = -1; else active = true;
        }
        if (heightAt >= 0)
        {
            float t = Draw.Ease((float)((now - heightAt) / 260));
            P.DrawCardH = heightFrom + (P.CardH - heightFrom) * t;
            if (t >= 1) { heightAt = -1; P.DrawCardH = -1; } else active = true;
        }
        if (Math.Abs(P.HoverA - hoverTarget) > 0.001f)
        {
            float step = (float)(dt / 140);
            P.HoverA = hoverTarget > P.HoverA ? Math.Min(hoverTarget, P.HoverA + step) : Math.Max(hoverTarget, P.HoverA - step);
            active = true;
        }
        if (Math.Abs(P.Scroll - scrollTarget) > 0.3f)
        {
            P.Scroll += (scrollTarget - P.Scroll) * (float)Math.Min(1, dt / 70);
            active = true;
        }
        else P.Scroll = scrollTarget;
        if (slideAt >= 0)
        {
            float t = Draw.Ease((float)((now - slideAt) / 240));
            P.GridShift = slideFrom * (1 - t);
            if (t >= 1) { slideAt = -1; P.GridShift = 0; } else active = true;
        }
        if (shimmerAt >= 0)
        {
            P.Shimmer = (float)((now - shimmerAt) / 1100);
            if (P.Shimmer > 1) { shimmerAt = -1; P.Shimmer = -1; } else active = true;
        }
        if (app.Syncing && c.Animate) { P.Spin = (float)(now / 850 % 1); active = true; }
        else if (P.Spin >= 0) { P.Spin = -1; Render(true); }
        Frame();
        if (!active) anim.Stop();
    }

    // ---- mouse: drag to move, hover and click events, scroll, right-click for the menu
    CalEvent HitAt(Point p)
    {
        foreach (var h in P.Hits) if (h.R.Contains(p)) return h.Ev;
        return null;
    }

    DateTime? DayAt(Point p)
    {
        foreach (var h in P.DayHits) if (h.Key.Contains(p)) return h.Value;
        return null;
    }

    int NavAt(Point p)
    {
        if (P.NavPrev.Contains(p)) return -1;
        if (P.NavNext.Contains(p)) return 1;
        if (P.NavToday.Contains(p)) return 2;
        return 0;
    }

    // Moves the calendar a month (or week) back or forward, or (0) back to now. The days slide in from that side.
    void Nav(int dir)
    {
        int before = P.ViewOffset;
        P.ViewOffset = dir == 0 ? 0 : P.ViewOffset + dir;
        if (P.ViewOffset == before) return;
        if (c.Animate)
        {
            slideFrom = (P.ViewOffset > before ? 1 : -1) * 48 * P.U;
            P.GridShift = slideFrom; slideAt = Now; Kick();
        }
        app.Rebuild();
    }

    // Clicking a day in the month or week view jumps the agenda to it. A day outside the agenda's range
    // gets its own section at the top. Clicking it again goes back.
    void SelectDay(DateTime day)
    {
        P.Selected = P.Selected == day ? (DateTime?)null : day;
        Render(true);
        float off = P.Selected.HasValue ? P.OffsetOf(day) : 0;
        scrollTarget = Math.Max(0, Math.Min(P.MaxScroll, off < 0 ? 0 : off));
        if (!c.Animate) P.Scroll = scrollTarget;
        Changed();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && OnScrollbar(e.Location))
        {
            // Grab the thumb, or jump to where the track was clicked
            float ty = P.Margin + P.ThumbY;
            thumbGrab = e.Y >= ty && e.Y <= ty + P.ThumbH ? e.Y - ty : P.ThumbH / 2;
            P.ThumbDrag = true;
            DragThumb(e.Y);
            base.OnMouseDown(e);
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            downAt = Cursor.Position; moved = false;
            if (!c.Locked) { dragging = true; grab = new Point(Cursor.Position.X - c.X, Cursor.Position.Y - c.Y); }
        }
        base.OnMouseDown(e);
    }

    bool OnScrollbar(Point p)
    {
        if (P.MaxScroll <= 0.5f) return false;
        float right = P.Margin + P.CardW;
        return p.X >= right - 14 * P.U && p.X <= right && p.Y >= P.Margin + P.BodyTop && p.Y <= P.Margin + P.BodyBottom;
    }

    void DragThumb(int y)
    {
        float track = P.BodyBottom - P.BodyTop - P.ThumbH;
        if (track <= 0) return;
        float f = (y - thumbGrab - P.Margin - P.BodyTop) / track;
        scrollTarget = P.Scroll = Math.Max(0, Math.Min(P.MaxScroll, f * P.MaxScroll));
        Frame();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (P.ThumbDrag) { DragThumb(e.Y); base.OnMouseMove(e); return; }
        if (dragging && (moved || Math.Abs(Cursor.Position.X - downAt.X) + Math.Abs(Cursor.Position.Y - downAt.Y) > 4))
        {
            moved = true;
            var p = Snap(new Point(Cursor.Position.X - grab.X, Cursor.Position.Y - grab.Y));
            c.X = p.X; c.Y = p.Y;
            SetWindowPos(Handle, IntPtr.Zero, p.X, p.Y, 0, 0, 0x1 | 0x4 | 0x10); // NOSIZE | NOZORDER | NOACTIVATE
        }
        var ev = dragging && moved || OnScrollbar(e.Location) ? null : HitAt(e.Location);
        if (ev != null && ev != P.Hover) { P.Hover = ev; P.HoverA = c.Animate ? 0 : 1; hoverTarget = 1; Changed(); }
        else if (ev == null && hoverTarget > 0) { hoverTarget = 0; if (!c.Animate) P.HoverA = 0; Changed(); }
        var day = dragging && moved ? null : DayAt(e.Location);
        int nav = dragging && moved ? 0 : NavAt(e.Location);
        if (day != P.HoverDay || nav != P.HoverNav) { P.HoverDay = day; P.HoverNav = nav; Frame(); }
        Cursor = dragging && moved ? Cursors.SizeAll : day != null || nav != 0 || (ev != null && c.ClickOpens && Feed.WebUrl(ev.Feed, DateTime.Today) != null) ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hoverTarget > 0) { hoverTarget = 0; if (!c.Animate) P.HoverA = 0; Changed(); }
        if (P.HoverDay != null || P.HoverNav != 0) { P.HoverDay = null; P.HoverNav = 0; Frame(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (P.ThumbDrag) { P.ThumbDrag = false; Frame(); base.OnMouseUp(e); return; }
        if (e.Button == MouseButtons.Left)
        {
            bool wasDrag = dragging && moved;
            dragging = false;
            if (wasDrag) c.Save();
            else if (NavAt(e.Location) != 0) { int n = NavAt(e.Location); Nav(n == 2 ? 0 : n); }
            else if (DayAt(e.Location).HasValue) SelectDay(DayAt(e.Location).Value);
            else if (c.ClickOpens)
            {
                var ev = HitAt(e.Location);
                if (ev != null) app.OpenWeb(ev.Feed, ev.Start.Date < DateTime.Today ? DateTime.Today : ev.Start.Date);
            }
        }
        else if (e.Button == MouseButtons.Right) app.Menu.Show(Cursor.Position);
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (P.GridArea.Contains(e.Location)) Nav(e.Delta > 0 ? -1 : 1); // over the calendar: previous / next month
        else if (P.MaxScroll > 0)
        {
            scrollTarget = Math.Max(0, Math.Min(P.MaxScroll, scrollTarget - e.Delta / 120f * 48 * P.U));
            if (!c.Animate) P.Scroll = scrollTarget;
            Changed();
        }
        base.OnMouseWheel(e);
    }

    void Changed() { if (c.Animate) Kick(); else Frame(); }

    // Pulls the card flush to a screen edge (with a small gap) when it's dragged close to one.
    Point Snap(Point p)
    {
        if (!c.Snap) return p;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int m = P.Margin, gap = (int)(12 * P.U), reach = (int)(18 * P.U);
        int left = p.X + m, top = p.Y + m, right = p.X + P.WinW - m, bottom = p.Y + P.WinH - m;
        if (Math.Abs(left - (wa.Left + gap)) < reach) p.X = wa.Left + gap - m;
        else if (Math.Abs(right - (wa.Right - gap)) < reach) p.X = wa.Right - gap - P.WinW + m;
        if (Math.Abs(top - (wa.Top + gap)) < reach) p.Y = wa.Top + gap - m;
        else if (Math.Abs(bottom - (wa.Bottom - gap)) < reach) p.Y = wa.Bottom - gap - P.WinH + m;
        return p;
    }

    // A new or off-screen widget (e.g. its monitor was unplugged) goes to the top-right of the main screen.
    void EnsurePosition()
    {
        if (c.X == int.MinValue || !Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new Rectangle(c.X, c.Y, P.WinW, P.WinH))))
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int gap = (int)(12 * P.U);
            c.X = wa.Right - gap - P.WinW + P.Margin;
            c.Y = wa.Top + gap - P.Margin;
        }
    }

    // ---- painting
    // Lays out the card and redraws it, but only when something on it has actually changed.
    public void Render(bool force)
    {
        if (IsDisposed || !IsHandleCreated) return;
        var screen = c.X == int.MinValue ? Screen.PrimaryScreen : Screen.FromPoint(new Point(c.X + 40, c.Y + 40));
        float u = DeviceDpi / 96f * c.Scale / 100f;
        P.Layout(u, screen.WorkingArea.Height - (int)(24 * u));
        if (P.Key == lastKey && !force && !anim.Enabled) return;
        lastKey = P.Key;

        bool want = c.Locked && c.ClickThrough;
        if (want != clickThrough)
        {
            clickThrough = want;
            int ex = GetWindowLong(Handle, -20);
            SetWindowLong(Handle, -20, want ? ex | 0x20 : ex & ~0x20); // TRANSPARENT: clicks fall through
        }
        scrollTarget = Math.Min(scrollTarget, P.MaxScroll);
        if (!anim.Enabled) P.Scroll = Math.Min(P.Scroll, P.MaxScroll);

        if (!shown)
        {
            shown = true;
            if (c.Animate) { P.Appear = 0; appearAt = Now; Kick(); }
        }
        else if (c.Animate && lastCardH > 0 && Math.Abs(lastCardH - P.CardH) > 1)
        {
            heightFrom = P.DrawCardH > 0 ? P.DrawCardH : lastCardH;
            heightAt = Now; Kick();
        }
        lastCardH = P.CardH;
        Frame();
    }

    void Frame()
    {
        if (IsDisposed || !IsHandleCreated) return;
        int w = P.WinW, h = P.WinH;
        if (P.DrawCardH > 0) h = Math.Max(h, (int)Math.Ceiling(P.DrawCardH) + P.Margin * 2); // room for the card while it shrinks
        EnsurePosition();
        var bmp = surface.Canvas(w, h);
        using (var g = Graphics.FromImage(bmp)) P.Paint(g);
        byte alpha = (byte)(P.PushAlpha * Draw.Ease(P.Appear * 1.6f));
        surface.Push(Handle, c.X, c.Y, alpha);
        if (!Visible || alpha > 0)
        {
            ShowWindow(Handle, 8); // SW_SHOWNA
            if (Layer == 0) SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);       // bottom of the pile
            else if (Layer == 2) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10); // stay on top
        }
    }
}

// ======================================================================= Settings window: building blocks
static class UI
{
    public static float S = 1;
    public static int I(float v) { return (int)Math.Round(v * S); }

    public static Color Side { get { return KinetikSuite.Look.Current.Nav; } }

    public static Color Page { get { return KinetikSuite.Look.Current.Bg; } }

    public static Color Card { get { return KinetikSuite.Look.Current.Card; } }

    public static Color CardHi { get { return KinetikSuite.Look.Current.Hover; } }

    public static Color Field { get { return KinetikSuite.Look.Current.Field; } }

    public static Color Edge { get { return KinetikSuite.Look.Current.Border; } }

    public static Color Text { get { return KinetikSuite.Look.Current.Text; } }

    public static Color Sub { get { return KinetikSuite.Look.Current.Sub; } }

    public static Color Accent { get { return KinetikSuite.Look.Current.Accent; } }

    public static Color Warn { get { return KinetikSuite.Look.Current.Light ? Color.FromArgb(176, 112, 0) : Color.FromArgb(255, 170, 60); } }

    public static Color Good { get { return KinetikSuite.Look.Current.Good; } }

    static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    public static Font F(float px, FontStyle st = FontStyle.Regular)
    {
        var key = px + "|" + st + "|" + S;
        Font f;
        if (!fonts.TryGetValue(key, out f)) fonts[key] = f = new Font(Draw.Family, px * S, st, GraphicsUnit.Pixel);
        return f;
    }

    public static Button Btn(string text, EventHandler click, bool primary = false)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? KinetikSuite.Look.Current.Accent : Field, ForeColor = primary ? KinetikSuite.Look.Current.OnAccent : Text,
            Font = F(13), AutoSize = true, MinimumSize = new Size(I(84), I(32)), Padding = new Padding(I(8), 0, I(8), 0), UseVisualStyleBackColor = false, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? KinetikSuite.Look.Current.Accent : Edge;
        b.FlatAppearance.MouseOverBackColor = primary ? KinetikSuite.Look.Current.Accent : KinetikSuite.Look.Current.Hover;
        b.FlatAppearance.MouseDownBackColor = primary ? KinetikSuite.Look.Current.Accent : KinetikSuite.Look.Current.Hover;
        b.Click += click;
        return b;
    }

    // Small line icons, drawn rather than loaded so they stay crisp at any scale.
    public static void Glyph(Graphics g, string name, RectangleF r, Color c)
    {
        float x = r.X, y = r.Y, s = r.Width;
        Func<float, float, PointF> P = (a, b) => new PointF(x + a * s, y + b * s);
        Func<float, float, float, float, RectangleF> R = (a, b, w, h) => new RectangleF(x + a * s, y + b * s, w * s, h * s);
        var sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new Pen(c, Math.Max(1.4f, s / 11)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        using (var b = new SolidBrush(c))
        {
            switch (name)
            {
                case "calendar":
                    using (var path = Draw.Round(R(0.12f, 0.2f, 0.76f, 0.68f), s * 0.12f)) g.DrawPath(p, path);
                    g.DrawLine(p, P(0.12f, 0.42f), P(0.88f, 0.42f));
                    g.DrawLine(p, P(0.34f, 0.1f), P(0.34f, 0.28f)); g.DrawLine(p, P(0.66f, 0.1f), P(0.66f, 0.28f));
                    break;
                case "list":
                    for (int i = 0; i < 3; i++)
                    {
                        float yy = 0.27f + 0.23f * i;
                        g.FillEllipse(b, R(0.12f, yy - 0.06f, 0.12f, 0.12f));
                        g.DrawLine(p, P(0.36f, yy), P(0.88f, yy));
                    }
                    break;
                case "palette":
                    g.DrawEllipse(p, R(0.12f, 0.12f, 0.76f, 0.76f));
                    g.FillEllipse(b, R(0.28f, 0.3f, 0.14f, 0.14f)); g.FillEllipse(b, R(0.52f, 0.24f, 0.14f, 0.14f)); g.FillEllipse(b, R(0.6f, 0.5f, 0.14f, 0.14f));
                    break;
                case "layout":
                    using (var path = Draw.Round(R(0.12f, 0.14f, 0.76f, 0.72f), s * 0.1f)) g.DrawPath(p, path);
                    g.DrawLine(p, P(0.12f, 0.38f), P(0.88f, 0.38f)); g.DrawLine(p, P(0.42f, 0.38f), P(0.42f, 0.86f));
                    break;
                case "gear":
                    g.DrawEllipse(p, R(0.33f, 0.33f, 0.34f, 0.34f));
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        g.DrawLine(p, P(0.5f + 0.27f * (float)Math.Cos(a), 0.5f + 0.27f * (float)Math.Sin(a)), P(0.5f + 0.4f * (float)Math.Cos(a), 0.5f + 0.4f * (float)Math.Sin(a)));
                    }
                    break;
                case "info":
                    g.DrawEllipse(p, R(0.12f, 0.12f, 0.76f, 0.76f));
                    g.DrawLine(p, P(0.5f, 0.46f), P(0.5f, 0.68f)); g.FillEllipse(b, R(0.45f, 0.27f, 0.1f, 0.1f));
                    break;
                case "layers":
                    g.DrawPolygon(p, new[] { P(0.5f, 0.15f), P(0.88f, 0.36f), P(0.5f, 0.57f), P(0.12f, 0.36f) });
                    g.DrawLines(p, new[] { P(0.12f, 0.56f), P(0.5f, 0.77f), P(0.88f, 0.56f) });
                    break;
                case "lock":
                    using (var path = Draw.Round(R(0.2f, 0.44f, 0.6f, 0.44f), s * 0.08f)) g.DrawPath(p, path);
                    g.DrawArc(p, R(0.32f, 0.14f, 0.36f, 0.5f), 180, 180);
                    break;
                case "pointer":
                    g.DrawPolygon(p, new[] { P(0.26f, 0.12f), P(0.26f, 0.8f), P(0.42f, 0.65f), P(0.54f, 0.88f), P(0.64f, 0.83f), P(0.52f, 0.6f), P(0.74f, 0.6f) });
                    break;
                case "magnet":
                    g.DrawArc(p, R(0.2f, 0.3f, 0.6f, 0.56f), 0, 180);
                    g.DrawLine(p, P(0.2f, 0.58f), P(0.2f, 0.16f)); g.DrawLine(p, P(0.8f, 0.58f), P(0.8f, 0.16f));
                    g.DrawLine(p, P(0.12f, 0.3f), P(0.28f, 0.3f)); g.DrawLine(p, P(0.72f, 0.3f), P(0.88f, 0.3f));
                    break;
                case "clock":
                    g.DrawEllipse(p, R(0.12f, 0.12f, 0.76f, 0.76f));
                    g.DrawLine(p, P(0.5f, 0.5f), P(0.5f, 0.28f)); g.DrawLine(p, P(0.5f, 0.5f), P(0.66f, 0.58f));
                    break;
                case "sync":
                    g.DrawArc(p, R(0.18f, 0.18f, 0.64f, 0.64f), -60, 290);
                    g.DrawLines(p, new[] { P(0.62f, 0.12f), P(0.68f, 0.24f), P(0.56f, 0.28f) });
                    break;
                case "eye":
                    g.DrawArc(p, R(0.08f, 0.28f, 0.84f, 0.8f), 200, 140);
                    g.DrawArc(p, R(0.08f, -0.08f, 0.84f, 0.8f), 20, 140);
                    g.DrawEllipse(p, R(0.38f, 0.38f, 0.24f, 0.24f));
                    break;
                case "text":
                    g.DrawLine(p, P(0.2f, 0.22f), P(0.8f, 0.22f)); g.DrawLine(p, P(0.5f, 0.22f), P(0.5f, 0.84f));
                    break;
                case "size":
                    g.DrawLine(p, P(0.22f, 0.78f), P(0.78f, 0.22f));
                    g.DrawLines(p, new[] { P(0.5f, 0.22f), P(0.78f, 0.22f), P(0.78f, 0.5f) });
                    g.DrawLines(p, new[] { P(0.22f, 0.5f), P(0.22f, 0.78f), P(0.5f, 0.78f) });
                    break;
                case "width":
                    g.DrawLine(p, P(0.12f, 0.5f), P(0.88f, 0.5f));
                    g.DrawLines(p, new[] { P(0.26f, 0.36f), P(0.12f, 0.5f), P(0.26f, 0.64f) });
                    g.DrawLines(p, new[] { P(0.74f, 0.36f), P(0.88f, 0.5f), P(0.74f, 0.64f) });
                    break;
                case "height":
                    g.DrawLine(p, P(0.5f, 0.12f), P(0.5f, 0.88f));
                    g.DrawLines(p, new[] { P(0.36f, 0.26f), P(0.5f, 0.12f), P(0.64f, 0.26f) });
                    g.DrawLines(p, new[] { P(0.36f, 0.74f), P(0.5f, 0.88f), P(0.64f, 0.74f) });
                    break;
                case "round":
                    g.DrawLine(p, P(0.2f, 0.86f), P(0.2f, 0.52f)); g.DrawArc(p, R(0.2f, 0.2f, 0.64f, 0.64f), 180, 90); g.DrawLine(p, P(0.52f, 0.2f), P(0.86f, 0.2f));
                    break;
                case "opacity":
                    g.DrawEllipse(p, R(0.14f, 0.14f, 0.72f, 0.72f)); g.FillPie(b, x + 0.14f * s, y + 0.14f * s, 0.72f * s, 0.72f * s, 90, 180);
                    break;
                case "shadow":
                    using (var sb = new SolidBrush(Color.FromArgb(90, c))) g.FillRectangle(sb, R(0.3f, 0.3f, 0.58f, 0.58f));
                    g.DrawRectangle(p, x + 0.12f * s, y + 0.12f * s, 0.58f * s, 0.58f * s);
                    break;
                case "border":
                    using (var path = Draw.Round(R(0.14f, 0.14f, 0.72f, 0.72f), s * 0.16f)) g.DrawPath(p, path);
                    break;
                case "grid":
                    using (var path = Draw.Round(R(0.12f, 0.12f, 0.76f, 0.76f), s * 0.1f)) g.DrawPath(p, path);
                    g.DrawLine(p, P(0.12f, 0.5f), P(0.88f, 0.5f)); g.DrawLine(p, P(0.5f, 0.12f), P(0.5f, 0.88f));
                    break;
                case "hash":
                    g.DrawLine(p, P(0.38f, 0.14f), P(0.32f, 0.86f)); g.DrawLine(p, P(0.68f, 0.14f), P(0.62f, 0.86f));
                    g.DrawLine(p, P(0.16f, 0.36f), P(0.86f, 0.36f)); g.DrawLine(p, P(0.14f, 0.64f), P(0.84f, 0.64f));
                    break;
                case "density":
                    g.DrawLine(p, P(0.14f, 0.26f), P(0.86f, 0.26f)); g.DrawLine(p, P(0.14f, 0.5f), P(0.86f, 0.5f)); g.DrawLine(p, P(0.14f, 0.74f), P(0.86f, 0.74f));
                    break;
                case "marker":
                    g.DrawLine(p, P(0.18f, 0.18f), P(0.18f, 0.82f)); g.DrawLine(p, P(0.4f, 0.34f), P(0.86f, 0.34f)); g.DrawLine(p, P(0.4f, 0.62f), P(0.74f, 0.62f));
                    break;
                case "bolt":
                    g.DrawPolygon(p, new[] { P(0.56f, 0.1f), P(0.24f, 0.56f), P(0.48f, 0.56f), P(0.42f, 0.9f), P(0.76f, 0.42f), P(0.52f, 0.42f) });
                    break;
                case "power":
                    g.DrawArc(p, R(0.16f, 0.2f, 0.68f, 0.68f), -60, 300); g.DrawLine(p, P(0.5f, 0.1f), P(0.5f, 0.48f));
                    break;
                case "drop":
                    g.DrawEllipse(p, R(0.2f, 0.34f, 0.6f, 0.54f)); g.DrawLines(p, new[] { P(0.23f, 0.52f), P(0.5f, 0.1f), P(0.77f, 0.52f) });
                    break;
                case "plus":
                    g.DrawLine(p, P(0.5f, 0.18f), P(0.5f, 0.82f)); g.DrawLine(p, P(0.18f, 0.5f), P(0.82f, 0.5f));
                    break;
                case "folder":
                    g.DrawPolygon(p, new[] { P(0.12f, 0.24f), P(0.4f, 0.24f), P(0.48f, 0.34f), P(0.88f, 0.34f), P(0.88f, 0.8f), P(0.12f, 0.8f) });
                    break;
                case "memory":
                    g.DrawRectangle(p, x + 0.12f * s, y + 0.3f * s, 0.76f * s, 0.4f * s);
                    for (int i = 0; i < 4; i++) g.DrawLine(p, P(0.24f + i * 0.17f, 0.7f), P(0.24f + i * 0.17f, 0.84f));
                    break;
                default:
                    g.DrawEllipse(p, R(0.25f, 0.25f, 0.5f, 0.5f));
                    break;
            }
        }
        g.SmoothingMode = sm;
    }
}

// An on/off switch with a sliding knob.
class Toggle : Control
{
    bool on;
    float pos;
    readonly System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 15 };
    public event EventHandler Changed;

    public Toggle(bool value)
    {
        on = value; pos = value ? 1 : 0;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(UI.I(42), UI.I(22));
        Cursor = Cursors.Hand;
        BackColor = UI.Card;
        t.Tick += (s, e) =>
        {
            float target = on ? 1 : 0;
            pos += (target - pos) * 0.35f;
            if (Math.Abs(target - pos) < 0.02f) { pos = target; t.Stop(); }
            Invalidate();
        };
    }

    public bool Checked
    {
        get { return on; }
        set
        {
            if (on == value) return;
            on = value; t.Start();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        Draw.Fill(g, r, r.Height / 2, Draw.Mix(KinetikSuite.Look.Current.Border, UI.Accent, pos));
        if (pos < 1) Draw.Stroke(g, r, r.Height / 2, Color.FromArgb((int)(255 * (1 - pos)), 82, 92, 120), 1);
        if (Focused) Draw.Stroke(g, RectangleF.Inflate(r, -0.5f, -0.5f), r.Height / 2, Color.FromArgb(120, 255, 255, 255), 1);
        float d = Height - UI.I(8), x = UI.I(4) + pos * (Width - UI.I(8) - d);
        using (var b = new SolidBrush(Draw.Mix(KinetikSuite.Look.Current.Sub, Color.White, pos))) g.FillEllipse(b, x, UI.I(4), d, d);
    }
}

// A slider with its value shown alongside. Click it first to use the mouse wheel or arrow keys, as in Kinetik.
class Slider : Control
{
    public int Min, Max, Value;
    public Func<int, string> Format = v => v.ToString();
    public event EventHandler Changed;
    bool drag;

    public Slider(int min, int max, int value)
    {
        Min = min; Max = max; Value = Math.Max(min, Math.Min(max, value));
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(UI.I(230), UI.I(26));
        BackColor = UI.Card;
        Cursor = Cursors.Hand;
    }

    RectangleF Track { get { return new RectangleF(UI.I(8), Height / 2f - UI.I(2), Width - UI.I(70), UI.I(4)); } }

    void Set(int v)
    {
        v = Math.Max(Min, Math.Min(Max, v));
        if (v == Value) return;
        Value = v; Invalidate();
        if (Changed != null) Changed(this, EventArgs.Empty);
    }

    void FromMouse(int mx)
    {
        var tr = Track;
        Set(Min + (int)Math.Round((mx - tr.X) / tr.Width * (Max - Min)));
    }

    int Step { get { return Max - Min > 100 ? 5 : 1; } }

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); if (!OnNumber(e.X)) { drag = true; FromMouse(e.X); } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (drag) FromMouse(e.X); else Cursor = OnNumber(e.X) ? Cursors.IBeam : Cursors.Hand; base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Focused) { Set(Value + Math.Sign(e.Delta) * Step); ((HandledMouseEventArgs)e).Handled = true; }
        else base.OnMouseWheel(e);
    }
    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) Set(Value - Step);
        else if (e.KeyCode == Keys.Right) Set(Value + Step);
        base.OnKeyDown(e);
    }
    // Double-click the number, press Enter or just start typing to enter an exact value
    TextBox edit;
    bool OnNumber(int x) { return x > Track.Right + UI.I(4); }

    void BeginEdit(string seed)
    {
        if (edit != null) return;
        edit = new TextBox { BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Right, Font = UI.F(11f), BackColor = KinetikSuite.Look.Current.Border, ForeColor = UI.Text, Width = UI.I(60) };
        edit.Location = new Point(Width - edit.Width - 1, (Height - edit.Height) / 2);
        edit.Text = seed ?? Value.ToString();
        edit.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(e.KeyCode == Keys.Enter); Focus(); }
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                e.SuppressKeyPress = true;
                int cur; if (!int.TryParse(edit.Text.Trim(), out cur)) cur = Value;
                edit.Text = Math.Max(Min, Math.Min(Max, cur + (e.KeyCode == Keys.Up ? 1 : -1))).ToString();
                edit.SelectionStart = edit.Text.Length;
            }
        };
        edit.LostFocus += (s, e) => EndEdit(true);
        Controls.Add(edit);
        edit.Focus();
        if (seed == null) edit.SelectAll(); else edit.SelectionStart = edit.Text.Length;
    }

    void EndEdit(bool commit)
    {
        if (edit == null) return;
        var t = edit; edit = null;
        var digits = new StringBuilder();
        foreach (char ch in t.Text) if (char.IsDigit(ch) || (ch == '-' && digits.Length == 0)) digits.Append(ch);
        int v;
        if (commit && int.TryParse(digits.ToString(), out v)) Set(v);
        Controls.Remove(t); t.Dispose(); Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e) { if (OnNumber(e.X)) BeginEdit(null); base.OnMouseDoubleClick(e); }
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (char.IsDigit(e.KeyChar) || e.KeyChar == '-') { e.Handled = true; BeginEdit(e.KeyChar.ToString()); }
        else if (e.KeyChar == (char)13) { e.Handled = true; BeginEdit(null); }
        base.OnKeyPress(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var tr = Track;
        float f = Max == Min ? 0 : (Value - Min) / (float)(Max - Min);
        Draw.Fill(g, tr, tr.Height / 2, KinetikSuite.Look.Current.Border);
        Draw.Fill(g, new RectangleF(tr.X, tr.Y, Math.Max(tr.Height, tr.Width * f), tr.Height), tr.Height / 2, UI.Accent);
        float d = UI.I(14), kx = tr.X + tr.Width * f - d / 2;
        if (Focused) using (var b = new SolidBrush(Color.FromArgb(60, UI.Accent))) g.FillEllipse(b, kx - UI.I(4), Height / 2f - d / 2 - UI.I(4), d + UI.I(8), d + UI.I(8));
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx, Height / 2f - d / 2, d, d);
        TextRenderer.DrawText(g, Format(Value), UI.F(12.5f), new Rectangle((int)tr.Right + UI.I(10), 0, Width - (int)tr.Right - UI.I(10), Height), UI.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }
}

class DarkCombo : KinetikSuite.Dropdown
{
    public DarkCombo(string[] items, int sel)
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = UI.Field; ForeColor = UI.Text;
        Font = UI.F(13);
        ItemHeight = UI.I(24);
        Width = UI.I(210);
        Items.AddRange(items);
        SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, sel));
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0, sel = (e.State & DrawItemState.Selected) != 0 && !edit;
        using (var b = new SolidBrush(sel ? KinetikSuite.Look.Current.Hover : UI.Field)) e.Graphics.FillRectangle(b, e.Bounds);
        TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, new Rectangle(e.Bounds.X + UI.I(6), e.Bounds.Y, e.Bounds.Width - UI.I(6), e.Bounds.Height), UI.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

// A rounded settings row: icon (or colour dot), title, description, and a control on the right.
class Card : Control
{
    public string Glyph, Title, Sub;
    public Color? Swatch;
    public Color SubColor = UI.Sub;
    readonly Control right;

    public Card(string glyph, string title, string sub, Control right, int width)
    {
        Glyph = glyph; Title = title; Sub = sub; this.right = right;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = UI.Page;
        Size = new Size(width, UI.I(sub == null ? 50 : 60));
        Margin = new Padding(0, 0, 0, UI.I(8));
        if (right != null) { Controls.Add(right); SetBackColors(right); }
    }

    // Cards one after another join into one card, split by hairlines, as in Kinetik's settings.
    bool first = true, last = true;
    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent == null) return;
        Parent.ControlAdded -= Rejoin; Parent.ControlAdded += Rejoin;
        Parent.ControlRemoved -= Rejoin; Parent.ControlRemoved += Rejoin;
        Rejoin(Parent, null);
    }

    static void Rejoin(object sender, ControlEventArgs e)
    {
        var kids = ((Control)sender).Controls;
        for (int i = 0; i < kids.Count; i++)
        {
            var card = kids[i] as Card;
            if (card == null) continue;
            bool f = i == 0 || !(kids[i - 1] is Card), l = i == kids.Count - 1 || !(kids[i + 1] is Card);
            if (f == card.first && l == card.last) continue;
            card.first = f; card.last = l;
            card.Margin = new Padding(0, 0, 0, l ? UI.I(8) : 0);
            card.Invalidate();
        }
    }

    static System.Drawing.Drawing2D.GraphicsPath Shape(RectangleF r, float rad, bool top, bool bottom)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath(); float d = rad * 2;
        if (top) { p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); } else p.AddLine(r.X, r.Y, r.Right, r.Y);
        if (bottom) { p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); } else p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        p.CloseFigure(); return p;
    }

    static void SetBackColors(Control c)
    {
        if (c is Toggle || c is Slider || c is FlowLayoutPanel || c is Label || c is Swatch || c.GetType() == typeof(Panel)) c.BackColor = UI.Card;
        foreach (Control k in c.Controls) SetBackColors(k);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (right != null) right.Location = new Point(Width - right.Width - UI.I(14), (Height - right.Height) / 2);
        base.OnLayout(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var path = Shape(new RectangleF(0, 0, Width - 1, last ? Height - 1 : Height), UI.I(8), first, last)) using (var b = new SolidBrush(UI.Card)) g.FillPath(b, path);
        if (!first) using (var pen = new Pen(KinetikSuite.Look.Current.Border, 1)) g.DrawLine(pen, UI.I(16), 0, Width - UI.I(16), 0);
        float isz = UI.I(18), ix = UI.I(16), iy = (Height - isz) / 2f;
        if (Swatch.HasValue) using (var b = new SolidBrush(Swatch.Value)) g.FillEllipse(b, ix + UI.I(2), iy + UI.I(2), isz - UI.I(4), isz - UI.I(4));
        else if (Glyph != null) UI.Glyph(g, Glyph, new RectangleF(ix, iy, isz, isz), UI.Accent);
        int tx = UI.I(50), tw = (right != null ? right.Left - UI.I(12) : Width - UI.I(14)) - tx;
        if (Sub == null)
            TextRenderer.DrawText(g, Title, UI.F(13.5f), new Rectangle(tx, 0, tw, Height), UI.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        else
        {
            TextRenderer.DrawText(g, Title, UI.F(13.5f), new Rectangle(tx, Height / 2 - UI.I(19), tw, UI.I(20)), UI.Text, TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Sub, UI.F(11.5f), new Rectangle(tx, Height / 2 + UI.I(1), tw, UI.I(18)), SubColor, TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }
}

class Swatch : Control
{
    public Color Color { get; set; }
    public bool Selected;
    bool hot;

    public Swatch(Color c, int size)
    {
        Color = c;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(size, size);
        Cursor = Cursors.Hand;
        BackColor = UI.Card;
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float inset = Selected || hot ? UI.I(4) : UI.I(2);
        Draw.Fill(g, new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2), Width, Color);
        if (Selected) Draw.Stroke(g, new RectangleF(1, 1, Width - 2.5f, Height - 2.5f), Width, Color.White, 1.5f);
        else if (hot) Draw.Stroke(g, new RectangleF(1, 1, Width - 2.5f, Height - 2.5f), Width, Color.FromArgb(120, 255, 255, 255), 1);
    }
}

// A theme in the gallery, drawn as a tiny widget.
class ThemeTile : Control
{
    public readonly string ThemeName;
    public bool Selected;
    bool hot;
    float sel;
    readonly System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 15 };

    public ThemeTile(string name, bool selected)
    {
        ThemeName = name; Selected = selected; sel = selected ? 1 : 0;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(UI.I(110), UI.I(84));
        Margin = new Padding(0, 0, UI.I(8), UI.I(8));
        Cursor = Cursors.Hand;
        BackColor = UI.Page;
        t.Tick += (s, e) =>
        {
            float target = Selected ? 1 : 0;
            sel += (target - sel) * 0.3f;
            if (Math.Abs(target - sel) < 0.02f) { sel = target; t.Stop(); }
            Invalidate();
        };
    }

    public void SetSelected(bool v) { if (Selected == v) return; Selected = v; t.Start(); }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var outer = new RectangleF(1, 1, Width - 3, Height - 3);
        Draw.Fill(g, outer, UI.I(10), hot ? UI.CardHi : UI.Card);
        if (sel > 0) Draw.Stroke(g, outer, UI.I(10), Color.FromArgb((int)(255 * sel), UI.Accent), 2);
        var inner = new RectangleF(UI.I(8), UI.I(8), Width - UI.I(16), Height - UI.I(34));
        if (ThemeName == "Auto")
        {
            Mini(g, inner, Theme.Get("Midnight"));
            var st = g.Save();
            g.SetClip(new RectangleF(inner.X + inner.Width / 2, inner.Y, inner.Width, inner.Height));
            Mini(g, inner, Theme.Get("Light"));
            g.Restore(st);
        }
        else Mini(g, inner, Theme.Get(ThemeName));
        TextRenderer.DrawText(g, ThemeName == "Auto" ? "Match Windows" : ThemeName == "App" ? "Match app theme" : ThemeName, UI.F(12), new Rectangle(0, Height - UI.I(26), Width, UI.I(20)), Selected ? UI.Text : UI.Sub,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    static void Mini(Graphics g, RectangleF r, Theme th)
    {
        Draw.Fill(g, r, UI.I(6), th.Bg);
        float x = r.X + UI.I(7), w = r.Width - UI.I(14), y = r.Y + UI.I(7);
        using (var b = new SolidBrush(th.Accent)) g.FillEllipse(b, x, y, UI.I(7), UI.I(7));
        Draw.Fill(g, new RectangleF(x + UI.I(11), y + UI.I(1), w * 0.45f, UI.I(4)), UI.I(2), th.Text);
        for (int i = 0; i < 3; i++)
        {
            float yy = y + UI.I(13) + i * UI.I(9);
            Draw.Fill(g, new RectangleF(x, yy, UI.I(3), UI.I(6)), UI.I(1.5f), Feed.Palette[i]);
            Draw.Fill(g, new RectangleF(x + UI.I(7), yy + UI.I(1), w * (0.62f - i * 0.12f), UI.I(4)), UI.I(2), Color.FromArgb(150, th.Sub));
            Draw.Fill(g, new RectangleF(x + w - UI.I(14), yy + UI.I(1), UI.I(14), UI.I(4)), UI.I(2), Color.FromArgb(200, th.Text));
        }
    }
}

class NavItem : Control
{
    public readonly string Page, Glyph;
    public bool Selected;
    bool hot;

    public NavItem(string page, string glyph)
    {
        Page = page; Glyph = glyph;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(UI.I(164), UI.I(40));
        Cursor = Cursors.Hand;
        BackColor = UI.Side;
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Selected || hot) Draw.Fill(g, new RectangleF(UI.I(6), UI.I(2), Width - UI.I(12), Height - UI.I(4)), UI.I(7), Selected ? KinetikSuite.Look.Current.Hover : KinetikSuite.Look.Current.Card);
        if (Selected) Draw.Fill(g, new RectangleF(UI.I(6), Height / 2f - UI.I(8), UI.I(3), UI.I(16)), UI.I(1.5f), UI.Accent);
        UI.Glyph(g, Glyph, new RectangleF(UI.I(20), (Height - UI.I(18)) / 2f, UI.I(18), UI.I(18)), Selected ? UI.Accent : UI.Sub);
        TextRenderer.DrawText(g, Page, UI.F(13.5f), new Rectangle(UI.I(50), 0, Width - UI.I(50), Height), Selected ? UI.Text : KinetikSuite.Look.Current.Sub, TextFormatFlags.VerticalCenter);
    }
}

// The widget as it will look, redrawn after every change.
class PreviewBox : Control
{
    readonly CalApp app;
    readonly Painter p;
    Bitmap cache;
    bool dirty = true;

    public PreviewBox(CalApp app)
    {
        this.app = app;
        p = new Painter(app);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = UI.Page;
    }

    public void Changed() { dirty = true; Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { p.Dispose(); if (cache != null) cache.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, "PREVIEW", UI.F(11, FontStyle.Bold), new Point(UI.I(18), UI.I(20)), UI.Sub);
        var area = new RectangleF(UI.I(16), UI.I(46), Width - UI.I(32), Height - UI.I(62));
        using (var path = Draw.Round(area, UI.I(12)))
        using (var lg = new LinearGradientBrush(area, Color.FromArgb(38, 52, 92), Color.FromArgb(78, 42, 86), 60f))
        {
            g.FillPath(lg, path);
            var st = g.Save();
            g.SetClip(path);
            if (dirty || cache == null)
            {
                dirty = false;
                p.CopyFrom(app.Widget.P);
                float u = UI.S * app.Cfg.Scale / 100f;
                float fit = Math.Min(1, (area.Width - UI.I(20)) / (app.Cfg.Width * u + 28 * u));
                p.Layout(u, (int)((area.Height - UI.I(10)) / fit));
                if (cache != null) cache.Dispose();
                cache = new Bitmap(p.WinW, p.WinH, PixelFormat.Format32bppPArgb);
                using (var cg = Graphics.FromImage(cache)) p.Paint(cg);
            }
            float f2 = Math.Min(1, (area.Width - UI.I(20)) / cache.Width);
            float w = cache.Width * f2, h = cache.Height * f2;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(cache, area.X + (area.Width - w) / 2, area.Y + UI.I(6), w, h);
            g.Restore(st);
        }
    }
}

// ======================================================================= Settings window
class SettingsForm : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string id);

    readonly CalApp app;
    readonly Settings c;
    readonly Panel side = new Panel(), host = new Panel();
    readonly PreviewBox preview;
    readonly List<NavItem> navs = new List<NavItem>();
    readonly System.Windows.Forms.Timer previewTimer = new System.Windows.Forms.Timer { Interval = 40 };
    FlowLayoutPanel page;
    string current;
    int contentW;

    static readonly string[][] Pages =
    {
        new[] { "Calendars", "calendar" }, new[] { "Agenda", "list" }, new[] { "Look", "palette" },
        new[] { "Layout", "layout" }, new[] { "Behaviour", "gear" }, new[] { "About", "info" },
    };

    public SettingsForm(CalApp app, string startPage)
    {
        this.app = app; c = app.Cfg;
        UI.S = DeviceDpi / 96f;
        Text = "Kinetik Calendar";
        Icon = app.AppIcon;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UI.Page; ForeColor = UI.Text;
        Font = UI.F(13);
        ClientSize = new Size(UI.I(1030), UI.I(720));
        DoubleBuffered = true;

        side.Dock = DockStyle.Left; side.Width = UI.I(184); side.BackColor = UI.Side;
        side.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            UI.Glyph(g, "calendar", new RectangleF(UI.I(20), UI.I(24), UI.I(22), UI.I(22)), UI.Accent);
            TextRenderer.DrawText(g, "Kinetik", UI.F(17, FontStyle.Bold), new Point(UI.I(50), UI.I(18)), UI.Text);
            TextRenderer.DrawText(g, "Calendar", UI.F(12), new Point(UI.I(51), UI.I(40)), UI.Sub);
            TextRenderer.DrawText(g, "v" + Program.Version, UI.F(11.5f), new Point(UI.I(22), side.Height - UI.I(30)), UI.Sub);
            using (var pen = new Pen(KinetikSuite.Look.Current.Border, 1)) g.DrawLine(pen, side.Width - 1, 0, side.Width - 1, side.Height);
        };
        int y = UI.I(84);
        foreach (var pg in Pages)
        {
            var n = new NavItem(pg[0], pg[1]) { Location = new Point(UI.I(10), y) };
            n.Click += (s, e) => ShowPage(((NavItem)s).Page);
            navs.Add(n); side.Controls.Add(n);
            y += UI.I(44);
        }

        preview = new PreviewBox(app) { Dock = DockStyle.Right, Width = UI.I(330) };
        host.Dock = DockStyle.Fill; host.BackColor = UI.Page;
        Controls.Add(host); Controls.Add(preview); Controls.Add(side);
        previewTimer.Tick += (s, e) => { previewTimer.Stop(); preview.Changed(); };
        ShowPage(startPage);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        KinetikSuite.Look.Current.Window(Handle); // title bar in the Kinetik apps' theme
    }

    protected override void Dispose(bool disposing) { if (disposing) previewTimer.Dispose(); base.Dispose(disposing); }

    public void PreviewChanged() { previewTimer.Stop(); previewTimer.Start(); }

    public void SyncFinished() { if (current == "Calendars") ShowPage("Calendars"); else PreviewChanged(); }

    // ---- pages
    public void ShowPage(string name)
    {
        current = name;
        foreach (var n in navs) { n.Selected = n.Page == name; n.Invalidate(); }
        int scroll = page != null && name == (string)page.Tag ? -page.AutoScrollPosition.Y : 0;
        host.SuspendLayout();
        var old = page;
        page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = UI.Page,
            Padding = new Padding(UI.I(24), UI.I(18), UI.I(12), UI.I(24)), Tag = name,
        };
        contentW = host.ClientSize.Width - UI.I(24) - UI.I(12) - SystemInformation.VerticalScrollBarWidth - UI.I(2);
        Title(name);
        switch (name)
        {
            case "Calendars": BuildCalendars(); break;
            case "Agenda": BuildAgenda(); break;
            case "Look": BuildLook(); break;
            case "Layout": BuildLayout(); break;
            case "Behaviour": BuildBehaviour(); break;
            default: BuildAbout(); break;
        }
        host.Controls.Add(page);
        if (old != null) { host.Controls.Remove(old); old.Dispose(); }
        host.ResumeLayout();
        SetWindowTheme(page.Handle, "DarkMode_Explorer", null);
        if (scroll > 0) page.AutoScrollPosition = new Point(0, scroll);
    }

    void Title(string t)
    {
        page.Controls.Add(new Label { Text = t, Font = UI.F(22, FontStyle.Bold), ForeColor = UI.Text, AutoSize = true, Margin = new Padding(0, 0, 0, UI.I(4)) });
    }

    void Desc(string t)
    {
        var l = new Label { Text = t, Font = UI.F(12.5f), ForeColor = UI.Sub, MaximumSize = new Size(contentW, 0), AutoSize = true, Margin = new Padding(0, 0, 0, UI.I(14)) };
        page.Controls.Add(l);
    }

    void Section(string t)
    {
        page.Controls.Add(new Label { Text = t.ToUpperInvariant(), Font = UI.F(11f, FontStyle.Bold), ForeColor = KinetikSuite.Look.Current.Sub, AutoSize = true, Margin = new Padding(UI.I(2), UI.I(14), 0, UI.I(8)) });
    }

    Card Row(string glyph, string title, string sub, Control right)
    {
        var card = new Card(glyph, title, sub, right, contentW);
        page.Controls.Add(card);
        return card;
    }

    void Changed(bool rebuild) { c.Rev++; app.Apply(rebuild); }

    void Switch(string glyph, string title, string sub, bool value, Action<bool> set, bool rebuild = false)
    {
        var t = new Toggle(value);
        t.Changed += (s, e) => { set(t.Checked); Changed(rebuild); };
        Row(glyph, title, sub, t);
    }

    void Slide(string glyph, string title, string sub, int min, int max, int value, Func<int, string> fmt, Action<int> set, bool rebuild = false)
    {
        var sl = new Slider(min, max, value);
        if (fmt != null) sl.Format = fmt;
        sl.Changed += (s, e) => { set(sl.Value); Changed(rebuild); };
        Row(glyph, title, sub, sl);
    }

    DarkCombo Choice(string glyph, string title, string sub, string[] items, int sel, Action<int> set, bool rebuild = false)
    {
        var cb = new DarkCombo(items, sel);
        cb.SelectedIndexChanged += (s, e) => { set(cb.SelectedIndex); Changed(rebuild); };
        Row(glyph, title, sub, cb);
        return cb;
    }

    FlowLayoutPanel Buttons(params Button[] buttons)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = UI.Page, Margin = new Padding(0, UI.I(2), 0, UI.I(10)) };
        foreach (var b in buttons) { b.Margin = new Padding(0, 0, UI.I(8), 0); f.Controls.Add(b); }
        page.Controls.Add(f);
        return f;
    }

    // ---- Calendars
    void BuildCalendars()
    {
        Desc("Your Google, Outlook and iCloud calendars, read from each one's private iCal link. Switch one off to hide it, or click it to change its name, colour or link.");
        if (c.Feeds.Count == 0)
        {
            var empty = Row("plus", "No calendars yet", "Add your first one: the window that opens explains where to find its link.", null);
            empty.Cursor = Cursors.Hand;
            empty.Click += (s, e) => AddFeed();
        }
        for (int i = 0; i < c.Feeds.Count; i++)
        {
            var f = c.Feeds[i];
            int idx = i;
            var t = new Toggle(f.On);
            t.Changed += (s, e) => { f.On = t.Checked; Changed(true); if (f.On && f.Parsed == null) app.Sync(); };
            var card = Row(null, f.Name, Feed.KindTitle(f.Kind) + "  ·  " + (f.On ? (f.Failed ? "⚠ " : "") + f.Status : "Hidden"), t);
            card.Swatch = f.Color;
            if (f.Failed && f.On) card.SubColor = UI.Warn;
            card.Cursor = Cursors.Hand;
            card.Click += (s, e) => EditFeed(idx);
        }
        Buttons(UI.Btn("+  Add calendar", (s, e) => AddFeed(), true), UI.Btn("Sync now", (s, e) => app.Sync()));

        Section("Syncing");
        var mins = new[] { 5, 10, 15, 30, 60 };
        Choice("sync", "Sync every", "Unchanged calendars are skipped", new[] { "5 minutes", "10 minutes", "15 minutes", "30 minutes", "1 hour" },
            Math.Max(0, Array.IndexOf(mins, c.SyncMin)), i => c.SyncMin = mins[i]);
    }

    void AddFeed()
    {
        using (var d = new FeedDialog(null, c.Feeds.Count))
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                c.Feeds.Add(d.Result);
                c.Save(); Changed(true); ShowPage("Calendars"); app.Sync();
            }
    }

    void EditFeed(int i)
    {
        if (i < 0 || i >= c.Feeds.Count) return;
        var f = c.Feeds[i];
        using (var d = new FeedDialog(f, i))
        {
            var res = d.ShowDialog(this);
            if (res == DialogResult.Abort)
            {
                c.Feeds.RemoveAt(i);
                Net.DeleteCache(f.Url);
                c.Save(); Changed(true); ShowPage("Calendars");
            }
            else if (res == DialogResult.OK)
            {
                var r = d.Result;
                bool newUrl = r.Url != f.Url;
                if (newUrl) { Net.DeleteCache(f.Url); f.Parsed = r.Parsed; f.Status = r.Parsed != null ? "Synced " + DateTime.Now.ToString("t") : "Not synced yet"; f.Failed = false; }
                f.Name = r.Name; f.Url = r.Url; f.Kind = r.Kind; f.Color = r.Color;
                c.Save(); Changed(true); ShowPage("Calendars");
                if (newUrl) app.Sync();
            }
        }
    }

    // ---- Agenda
    void BuildAgenda()
    {
        Desc("What the list under the month shows.");
        Section("Days");
        Slide("calendar", "Days to show", "Today and the days after it", 1, 120, c.Days, v => v == 1 ? "Today" : v + " days", v => c.Days = v, true);
        Switch("layers", "Merge duplicates", "An event in two calendars shows once", c.MergeDupes, v => c.MergeDupes = v, true);
        Switch("eye", "Show days with nothing on", "Otherwise only days with events get a heading", c.ShowEmptyDays, v => c.ShowEmptyDays = v);
        Slide("list", "Events per day", "The rest are summed up as \"+3 more\"", 0, 20, c.MaxPerDay, v => v == 0 ? "All" : v.ToString(), v => c.MaxPerDay = v);
        Section("Events");
        Switch("calendar", "All-day events", "Birthdays, holidays, trips", c.ShowAllDay, v => c.ShowAllDay = v);
        Switch("clock", "Keep today's finished events", "Shown dimmed until midnight", c.ShowPast, v => c.ShowPast = v);
        Switch("text", "Times and durations", "Time range, or how long is left", c.ShowDetails, v => c.ShowDetails = v);
        Switch("pointer", "Locations", "Added to the second line", c.ShowLocation, v => c.ShowLocation = v);
        Switch("bolt", "Countdown", "\"in 25 min\" within the hour", c.Countdown, v => c.Countdown = v);
        Switch("width", "Progress bars", "A bar under events happening now", c.ProgressBars, v => c.ProgressBars = v);
        Switch("clock", "24-hour time", null, c.Use24h, v => c.Use24h = v);
    }

    // ---- Look
    void BuildLook()
    {
        Desc("Themes set the background, text and accent colours. The accent marks today and the clock.");
        Section("Theme");
        var gallery = new FlowLayoutPanel { Width = contentW + UI.I(8), AutoSize = true, MaximumSize = new Size(contentW + UI.I(8), 0), BackColor = UI.Page, Margin = new Padding(0, 0, 0, UI.I(6)) };
        var tiles = new List<ThemeTile>();
        foreach (var name in new[] { "Auto", "App" }.Concat(Theme.All.Select(t => t.Name)))
        {
            var tile = new ThemeTile(name, c.ThemeName == name);
            tile.Click += (s, e) =>
            {
                c.ThemeName = ((ThemeTile)s).ThemeName;
                foreach (var t in tiles) t.SetSelected(t == s);
                Changed(false);
            };
            tiles.Add(tile); gallery.Controls.Add(tile);
        }
        page.Controls.Add(gallery);

        Section("Colours");
        ColourRow("drop", "Accent colour", "Today and the clock", () => c.Accent, v => c.Accent = v);
        ColourRow("opacity", "Background colour", "Overrides the theme", () => c.BgColor, v => c.BgColor = v);

        Section("Card");
        var fonts = Draw.FontChoices.Where(Draw.Has).ToList();
        fonts.Insert(0, "Default");
        Choice("text", "Font", null, fonts.ToArray(), Math.Max(0, fonts.IndexOf(c.FontName)), i => c.FontName = i == 0 ? "" : fonts[i]);
        Slide("size", "Size", null, 70, 200, c.Scale, v => v + "%", v => c.Scale = v);
        Slide("width", "Width", null, 220, 700, c.Width, v => v + " px", v => c.Width = v);
        Slide("height", "Maximum height", "Taller agendas scroll: wheel or drag the bar", 0, 1400, c.MaxHeight, v => v < 200 ? "Fit screen" : v + " px", v => c.MaxHeight = v < 200 ? 0 : v);
        Slide("opacity", "Background opacity", null, 0, 100, c.Opacity, v => v + "%", v => c.Opacity = v);
        Slide("round", "Corner roundness", null, 0, 200, c.Round, v => v + "%", v => c.Round = v);
        Switch("shadow", "Shadow", null, c.Shadow, v => c.Shadow = v);
        Switch("border", "Outline", "A hairline around the card", c.Border, v => c.Border = v);
        Buttons(UI.Btn("Restore default look", (s, e) => { c.ResetLook(); Changed(true); ShowPage("Look"); }));
    }

    void ColourRow(string glyph, string title, string sub, Func<int> get, Action<int> set)
    {
        var flow = new Panel { Size = new Size(UI.I(250), UI.I(30)) };
        int fx = UI.I(70);
        var opts = new List<Swatch>();
        Action refresh = null;
        var themeBtn = UI.Btn("Theme", (s, e) => { set(0); Changed(false); refresh(); });
        themeBtn.AutoSize = false; themeBtn.MinimumSize = Size.Empty; themeBtn.Padding = Padding.Empty;
        themeBtn.SetBounds(0, UI.I(1), UI.I(64), UI.I(28));
        flow.Controls.Add(themeBtn);
        foreach (var col in new[] { Color.FromArgb(240, 88, 88), Color.FromArgb(77, 166, 255), Color.FromArgb(108, 201, 124), Color.FromArgb(255, 159, 10), Color.FromArgb(255, 121, 198) })
        {
            var sw = new Swatch(col, UI.I(28)) { Location = new Point(fx, UI.I(1)) };
            fx += UI.I(30);
            if (glyph == "opacity") sw.Color = Draw.Mix(KinetikSuite.Look.Current.Bg, col, 0.18f);
            sw.Click += (s, e) => { set(((Swatch)s).Color.ToArgb()); Changed(false); refresh(); };
            opts.Add(sw); flow.Controls.Add(sw);
        }
        var custom = UI.Btn("…", (s, e) =>
        {
            using (var d = new ColorDialog { FullOpen = true, Color = get() != 0 ? Color.FromArgb(get()) : Color.White })
                if (d.ShowDialog(this) == DialogResult.OK) { set(Color.FromArgb(255, d.Color).ToArgb()); Changed(false); refresh(); }
        });
        custom.AutoSize = false; custom.MinimumSize = Size.Empty; custom.Padding = Padding.Empty;
        custom.SetBounds(fx + UI.I(2), UI.I(1), UI.I(32), UI.I(28));
        flow.Controls.Add(custom);
        refresh = () =>
        {
            foreach (var sw in opts) { sw.Selected = get() != 0 && sw.Color.ToArgb() == get(); sw.Invalidate(); }
            themeBtn.BackColor = get() == 0 ? KinetikSuite.Look.Current.Hover : UI.Field;
        };
        refresh();
        Row(glyph, title, sub, flow);
    }

    // ---- Layout
    void BuildLayout()
    {
        Desc("How the card is put together, from the top down.");
        Section("Header");
        Choice("clock", "Title", "A big clock, the date, or nothing", new[] { "Date", "Clock and date", "None" }, c.TitleStyle, i => c.TitleStyle = i);
        Choice("grid", "Calendar", "The whole month, just this week, or nothing", new[] { "This month", "This week", "Hidden" }, c.MonthStyle, i => c.MonthStyle = i, true);
        var days = new List<string> { "Your region's default" };
        days.AddRange(Enumerable.Range(0, 7).Select(i => CultureInfo.CurrentCulture.DateTimeFormat.DayNames[i]));
        Choice("calendar", "Week starts on", null, days.ToArray(), c.FirstDay + 1, i => c.FirstDay = i - 1, true);
        Switch("hash", "Week numbers", "ISO week numbers down the left", c.WeekNumbers, v => c.WeekNumbers = v);
        Switch("layers", "Shade weekends", null, c.ShadeWeekends, v => c.ShadeWeekends = v);
        Section("Events");
        Choice("density", "Spacing", null, new[] { "Compact", "Comfortable", "Roomy" }, c.Density, i => c.Density = i);
        Choice("marker", "Calendar colour", "Shows which calendar it's from", new[] { "Bar", "Dot", "Tinted card", "None" }, c.Marker, i => c.Marker = i);
    }

    // ---- Behaviour
    void BuildBehaviour()
    {
        Desc("Where the widget sits and how it responds. Drag it to move it, scroll it with the mouse wheel when it's taller than the screen, and right-click it for quick options.");
        Section("Window");
        Choice("layers", "Placement", "Behind, among or above windows", new[] { "On the desktop (behind windows)", "Normal window", "Always on top" }, c.Layer,
            i => { c.Layer = i; c.Save(); app.RecreateWidget(); });
        Switch("lock", "Lock position", "Stops it being dragged by accident", c.Locked, v => c.Locked = v);
        Switch("pointer", "Click-through when locked", "Clicks pass through. Unlock from the tray icon", c.ClickThrough, v => c.ClickThrough = v);
        Switch("magnet", "Snap to screen edges", null, c.Snap, v => c.Snap = v);
        var reset = UI.Btn("Reset position", (s, e) => { c.X = c.Y = int.MinValue; Changed(false); });
        Row("width", "Position", "Moves it back to the top-right corner", reset);
        Section("Interaction");
        Switch("pointer", "Click an event to open it", "In Google Calendar, Outlook or iCloud on the web", c.ClickOpens, v => c.ClickOpens = v);
        Switch("bolt", "Animations", "Fade-in, hover, resizing, scrolling, sync spinner", c.Animate, v => c.Animate = v);
        Section("Startup");
        var t = new Toggle(Startup.IsOn);
        t.Changed += (s, e) => Startup.Set(t.Checked);
        Row("power", "Run at startup", "Starts with Windows", t);
        Section("Backup");
        Row("folder", "Back up settings", "Saves your calendars, look and layout next to the app. Last backup: " + Backup.SavedText,
            UI.Btn("Back up", (s, e) => { c.Save(); Backup.SaveNow(); ShowPage(current); }));
        Row("folder", "Restore settings", "Loads the backup from the app folder, for example after reinstalling Windows",
            UI.Btn("Restore", (s, e) => Backup.RestoreNow()));
    }

    // ---- About
    void BuildAbout()
    {
        Desc("A desktop agenda for Google, Outlook and iCloud calendars, one of the Kinetik apps.");
        Row("info", "Kinetik Calendar " + Program.Version, "Copyright © 2026 Akila Sella Hennedige. GPL-3.0-or-later.", null);
        long mem = 0;
        try { using (var pr = Process.GetCurrentProcess()) mem = pr.WorkingSet64; } catch { }
        int events = app.Widget.P.Events.Count;
        Row("memory", "Memory in use: " + (mem / 1048576.0).ToString("0.0") + " MB", events + " events on show from " + c.Feeds.Count(f => f.On) + " calendars. It idles between minutes and when the PC is locked.", null);
        Row("folder", "Saved copies", "Calendars are kept in " + Net.Dir + " for offline use. Links and settings are in HKCU\\Software\\KinetikCalendar.",
            UI.Btn("Open folder", (s, e) => { try { Directory.CreateDirectory(Net.Dir); Process.Start(new ProcessStartInfo(Net.Dir) { UseShellExecute = true }); } catch { } }));
    }
}

class FeedDialog : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    static readonly string[] Help =
    {
        "In a web browser, open calendar.google.com → Settings (gear icon) → under \"Settings for my calendars\" pick the calendar → " +
        "\"Integrate calendar\" → copy \"Secret address in iCal format\".\n\nEach calendar (personal, family, holidays…) has its own link, so add each one you want.",
        "Open outlook.com (or outlook.office.com for a work or school account) → Settings (gear icon) → Calendar → Shared calendars → " +
        "\"Publish a calendar\". Pick the calendar, choose \"Can view all details\", select Publish, then copy the ICS link.\n\n" +
        "Some workplaces turn publishing off. If the option is missing, ask IT or use a personal calendar instead.",
        "iPhone: Calendar app → Calendars → (i) next to the calendar → turn on \"Public Calendar\" → Share Link → Copy Link.\n" +
        "Mac: Calendar app → right-click the calendar → Share Calendar → tick \"Public Calendar\" → copy the webcal:// link.\n\n" +
        "Anyone with this link can see that calendar, so keep it to yourself.",
        "Paste any iCal subscription link (https://… or webcal://…, usually ending in .ics).",
    };

    readonly TextBox name = new TextBox(), url = new TextBox();
    readonly DarkCombo kind;
    readonly Label help = new Label(), result = new Label();
    readonly Button test;
    readonly List<Swatch> swatches = new List<Swatch>();
    Color color;
    bool colorPicked;
    List<Ics.VEvent> tested;
    string testedUrl;
    public Feed Result;

    public FeedDialog(Feed f, int index)
    {
        Text = f == null ? "Add a calendar" : "Edit calendar";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UI.Page; ForeColor = UI.Text;
        Font = UI.F(13);
        int L = UI.I(22), W = UI.I(520), LW = UI.I(110), y = UI.I(20);

        Func<string, int, Label> label = (t, yy) => { var l = new Label { Text = t, AutoSize = true, Location = new Point(L, yy + UI.I(4)), ForeColor = UI.Sub }; Controls.Add(l); return l; };

        label("Type", y);
        var k = f != null ? f.Kind : "Google";
        kind = new DarkCombo(Feed.KindNames, Math.Max(0, Array.IndexOf(Feed.Kinds, k))) { Location = new Point(L + LW, y), Width = UI.I(260) };
        Controls.Add(kind);
        y += UI.I(40);

        help.SetBounds(L, y, W, UI.I(104)); help.ForeColor = UI.Sub; help.Font = UI.F(12.5f);
        Controls.Add(help);
        y += UI.I(110);

        label("iCal link", y);
        url.SetBounds(L + LW, y, W - LW, UI.I(26));
        Style(url);
        Controls.Add(url);
        y += UI.I(40);

        label("Name", y);
        name.SetBounds(L + LW, y, UI.I(260), UI.I(26));
        Style(name);
        Controls.Add(name);
        y += UI.I(42);

        label("Colour", y);
        int sx = L + LW;
        foreach (var col in Feed.Palette)
        {
            var sw = new Swatch(col, UI.I(26)) { Location = new Point(sx, y), BackColor = UI.Page };
            sw.Click += (s, e) => { color = ((Swatch)s).Color; colorPicked = true; MarkSwatch(); };
            swatches.Add(sw); Controls.Add(sw);
            sx += UI.I(30);
        }
        var more = UI.Btn("…", (s, e) => PickColor());
        more.MinimumSize = new Size(UI.I(32), UI.I(26)); more.Location = new Point(sx + UI.I(4), y);
        Controls.Add(more);
        y += UI.I(44);

        test = UI.Btn("Test link", (s, e) => Test());
        test.Location = new Point(L + LW, y);
        Controls.Add(test);
        result.SetBounds(L + LW + UI.I(108), y + UI.I(7), W - LW - UI.I(108), UI.I(40)); result.ForeColor = UI.Sub;
        Controls.Add(result);
        y += UI.I(50);

        Controls.Add(new Label { Text = "The link is saved in this PC's registry, not in the app folder. Treat it like a password: anyone who has it can read the calendar.", ForeColor = UI.Sub, Font = UI.F(12), Location = new Point(L, y), Size = new Size(W, UI.I(36)) });
        y += UI.I(48);

        var ok = UI.Btn(f == null ? "Add" : "Save", (s, e) => Accept(), true);
        var cancel = UI.Btn("Cancel", (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
        ok.AutoSize = cancel.AutoSize = false;
        ok.SetBounds(L + W - UI.I(196), y, UI.I(94), UI.I(34)); cancel.SetBounds(L + W - UI.I(94), y, UI.I(94), UI.I(34));
        Controls.Add(ok); Controls.Add(cancel);
        if (f != null)
        {
            var remove = UI.Btn("Remove", (s, e) =>
            {
                if (MessageBox.Show(this, "Remove \"" + f.Name + "\" from the widget?\n\nOnly the widget forgets it. The calendar itself isn't changed.", "Remove calendar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                { DialogResult = DialogResult.Abort; Close(); }
            });
            remove.ForeColor = KinetikSuite.Look.Current.Warn;
            remove.AutoSize = false; remove.SetBounds(L, y, UI.I(94), UI.I(34));
            Controls.Add(remove);
        }
        AcceptButton = ok; CancelButton = cancel;
        ClientSize = new Size(L * 2 + W, y + UI.I(34) + UI.I(20));

        color = f != null ? f.Color : Feed.Palette[index % Feed.Palette.Length];
        colorPicked = f != null;
        name.Text = f != null ? f.Name : "";
        url.Text = f != null ? f.Url : "";
        kind.SelectedIndexChanged += (s, e) => KindChanged();
        url.TextChanged += (s, e) =>
        {
            var d = Feed.Detect(url.Text);
            if (d != null && d != Feed.Kinds[kind.SelectedIndex]) kind.SelectedIndex = Array.IndexOf(Feed.Kinds, d);
        };
        KindChanged();
    }

    static void Style(TextBox t) { t.BackColor = UI.Field; t.ForeColor = UI.Text; t.BorderStyle = BorderStyle.FixedSingle; t.Font = UI.F(13); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        KinetikSuite.Look.Current.Window(Handle); // title bar in the Kinetik apps' theme
    }

    void MarkSwatch()
    {
        foreach (var sw in swatches) { sw.Selected = sw.Color.ToArgb() == color.ToArgb(); sw.Invalidate(); }
    }

    void KindChanged()
    {
        int i = Math.Max(0, kind.SelectedIndex);
        help.Text = Help[i];
        if (!colorPicked) color = Feed.DefaultColor(Feed.Kinds[i]);
        MarkSwatch();
    }

    void PickColor()
    {
        using (var d = new ColorDialog { Color = color, FullOpen = true })
            if (d.ShowDialog(this) == DialogResult.OK) { color = Color.FromArgb(255, d.Color); colorPicked = true; MarkSwatch(); }
    }

    void Test()
    {
        var link = url.Text.Trim();
        if (link == "") { result.Text = "Paste a link first."; return; }
        test.Enabled = false;
        result.ForeColor = UI.Sub;
        result.Text = "Checking…";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string msg, calName = null;
            bool good = false;
            List<Ics.VEvent> parsed = null;
            try
            {
                var text = Net.Download(link);
                parsed = Ics.Parse(text);
                calName = Ics.CalName(text);
                var today = DateTime.Today;
                int n = Ics.Expand(parsed, new Feed(), today, today.AddDays(30)).Count;
                msg = "✓ Works: " + n + " event" + (n == 1 ? "" : "s") + " in the next 30 days";
                good = true;
                Net.WriteCache(link, text);
            }
            catch (Exception ex) { msg = "✗ " + Net.Friendly(ex); }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    test.Enabled = true;
                    result.Text = msg;
                    result.ForeColor = good ? UI.Good : UI.Warn;
                    if (good) { tested = parsed; testedUrl = link; }
                    if (good && name.Text.Trim() == "" && !string.IsNullOrEmpty(calName)) name.Text = calName;
                }));
            }
            catch { }
        });
    }

    void Accept()
    {
        var link = url.Text.Trim();
        if (link == "") { result.ForeColor = UI.Warn; result.Text = "Paste the calendar's iCal link first."; return; }
        var k = Feed.Kinds[Math.Max(0, kind.SelectedIndex)];
        var n = name.Text.Trim();
        if (n == "") n = Feed.KindTitle(k);
        Result = new Feed { Name = n, Url = link, Kind = k, Color = color, On = true };
        if (tested != null && testedUrl == link) { Result.Parsed = tested; Result.Status = "Synced " + DateTime.Now.ToString("t"); }
        DialogResult = DialogResult.OK;
        Close();
    }
}

// ======================================================================= Settings backup
// Copies the app's settings (HKCU\Software\<KeyName>) to "<KeyName>-settings.reg" next to the exe, so they survive a
// Windows reinstall when the app folder is kept (on OneDrive, a USB stick…). It's a normal .reg export, so
// double-clicking the file restores the settings as well. A restore runs after the app has closed, so nothing the app
// saves on the way out can overwrite it, and then the app starts again with the restored settings.
// Same code in every Kinetik app.
static class Backup
{
    public static string KeyName, AppTitle;
    public static Action BeforeExit; // e.g. hide the tray icon, as the app's own Exit does
    static bool restoreAfterExit;

    public static string FilePath
    {
        get { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), KeyName + "-settings.reg"); }
    }
    public static bool Exists { get { return System.IO.File.Exists(FilePath); } }
    public static string SavedText
    {
        get { return Exists ? System.IO.File.GetLastWriteTime(FilePath).ToString("d MMM yyyy, h:mm tt") : "never"; }
    }

    static bool HasSettings
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\" + KeyName)) return k != null; }
            catch { return true; }
        }
    }

    static bool Reg(string args) { return KinetikSuite.RegistryFiles.Run(args); }

    static bool FileIsOurs()
    {
        try
        {
            string own = "[HKEY_CURRENT_USER\\Software\\" + KeyName;
            bool any = false;
            foreach (var line in System.IO.File.ReadAllLines(FilePath))
            {
                var l = line.Trim();
                if (!l.StartsWith("[")) continue;
                if (!l.StartsWith(own + "]", StringComparison.OrdinalIgnoreCase) && !l.StartsWith(own + "\\", StringComparison.OrdinalIgnoreCase)) return false;
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    static bool Import()
    {
        try { return FileIsOurs() && KinetikSuite.RegistryFiles.Replace(FilePath, new[] { KeyName }); }
        catch (Exception ex) { Say(ex.Message, MessageBoxIcon.Warning); return false; }
    }

    static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, AppTitle, MessageBoxButtons.OK, icon); }

    // At launch: no settings in the registry (a fresh Windows) but a backup beside the exe, so offer it.
    public static void OfferRestore()
    {
        if (HasSettings || !Exists) return;
        if (MessageBox.Show("There are saved " + AppTitle + " settings in this folder, from " + SavedText + ".\n\nRestore them?",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!Import()) Say("Couldn't restore the settings from\n" + FilePath, MessageBoxIcon.Warning);
    }

    public static void SaveNow()
    {
        if (Reg("export \"HKCU\\Software\\" + KeyName + "\" \"" + FilePath + "\" /y"))
            Say("Settings backed up to\n" + FilePath + "\n\nKeep this file with the app. After reinstalling Windows, " + AppTitle + " offers to restore it the first time it starts.", MessageBoxIcon.Information);
        else
            Say("Couldn't save the backup to\n" + FilePath + "\n\nCheck that the folder isn't read-only.", MessageBoxIcon.Warning);
    }

    public static void RestoreNow()
    {
        if (!Exists) { Say("There's no backup yet. Use \"Back up settings\" first.\n\nIt would be saved as\n" + FilePath, MessageBoxIcon.Information); return; }
        if (!FileIsOurs()) { Say("The backup file isn't a " + AppTitle + " settings file, so it wasn't used.", MessageBoxIcon.Warning); return; }
        if (MessageBox.Show("Replace the current settings with the backup from " + SavedText + "?\n\n" + AppTitle + " will restart.",
                AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        restoreAfterExit = true;
        if (BeforeExit != null) try { BeforeExit(); } catch { }
        Application.Exit();
    }

    // Called from Main once the app has closed and released its single-instance lock.
    public static void AfterExit()
    {
        if (!restoreAfterExit) return;
        if (!Import()) Say("Couldn't restore the settings from\n" + FilePath, MessageBoxIcon.Warning);
        try { System.Diagnostics.Process.Start(Application.ExecutablePath); } catch { }
    }
}

// ======================================================================= The right-click menu every Kinetik widget shares
// The look, placement, startup and backup options, in the same order and the same words in every Kinetik app, so they
// feel like one set. Each app adds its own items above. Same code in every Kinetik app.
#pragma warning disable 0649 // the hooks an app has no use for stay null
class CommonMenu
{
    public static readonly int[] Scales = { 80, 90, 100, 115, 125, 150 };
    public static readonly int[] Opacities = { 100, 95, 80, 65, 50, 30, 0 };
    public const int DefaultOpacity = 95;
    public static readonly string[] Fonts = { "Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Calibri", "Aptos", "Inter", "Cascadia Code", "Consolas", "Verdana", "Georgia" };

    // Settings saved before the shared default changed from 92% to 95% move with it; any other choice is kept.
    public static int Migrate(int opacity) { return opacity == 92 ? DefaultOpacity : opacity; }

    public string Title, Version;
    public Action OpenTheme;                                        // opens the theme picker (or the page with it)
    public Action LookChanged;                                      // optional: after the App theme changes
    public Func<int> Scale; public Action<int> SetScale;
    public int[] Widths; public Func<int> CardWidth; public Action<int> SetCardWidth; // optional: Narrow, Normal, Wide
    public Func<string> FontName; public Action<string> SetFontName;   // optional; "" = the default font
    public Func<int> Opacity; public Action<int> SetOpacity;
    public Func<int> Layer; public Action<int> SetLayer;
    public Func<bool> Locked; public Action<bool> SetLocked;
    public Func<bool> ClickThrough; public Action<bool> SetClickThrough; // optional
    public Func<bool> Snap; public Action<bool> SetSnap;
    public string LockText = "Lock position";
    public Action OpenSettings;                                     // optional: the app's Settings window
    public Func<bool> RunAtStartup; public Action<bool> SetRunAtStartup;   // optional
    public Action BackUp, Restore; public Func<string> LastBackup; // optional
    public Action Exit;                                         // optional

    static ToolStripMenuItem Item(string text, bool check, Action click)
    {
        var it = new ToolStripMenuItem(text) { Checked = check };
        it.Click += (s, e) => click();
        return it;
    }

    static bool HasFont(string name)
    {
        try { using (var f = new FontFamily(name)) return f.Name == name; } catch { return false; }
    }

    // Appearance and placement: Theme, Size, Font, Background opacity, Placement.
    public void AddLook(ToolStripItemCollection items)
    {
        if (OpenTheme != null) items.Add(Item("Widget theme…", false, OpenTheme));
        items.Add(KinetikSuite.Look.MenuItem(LookChanged)); // the theme of every Kinetik app's windows and menus

        var size = new ToolStripMenuItem("Size");
        foreach (var s in Scales) { int v = s; size.DropDownItems.Add(Item(v + "%", Scale() == v, () => SetScale(v))); }
        if (Widths != null)
        {
            size.DropDownItems.Add(new ToolStripSeparator());
            string[] names = { "Narrow", "Normal", "Wide" };
            for (int i = 0; i < Widths.Length && i < 3; i++) { int v = Widths[i]; size.DropDownItems.Add(Item(names[i], CardWidth() == v, () => SetCardWidth(v))); }
        }
        items.Add(size);

        if (FontName != null)
        {
            var font = new ToolStripMenuItem("Font");
            font.DropDownItems.Add(Item("Default", FontName() == "", () => SetFontName("")));
            foreach (var n in Fonts.Where(HasFont)) { string v = n; font.DropDownItems.Add(Item(v, FontName() == v, () => SetFontName(v))); }
            if (FontName() != "" && !Fonts.Contains(FontName())) font.DropDownItems.Add(Item(FontName(), true, () => { }));
            items.Add(font);
        }

        var opacity = new ToolStripMenuItem("Background opacity");
        foreach (var o in Opacities) { int v = o; opacity.DropDownItems.Add(Item(v + "%", Opacity() == v, () => SetOpacity(v))); }
        if (!Opacities.Contains(Opacity())) opacity.DropDownItems.Add(Item(Opacity() + "%", true, () => { }));
        items.Add(opacity);

        var place = new ToolStripMenuItem("Placement");
        string[] layers = { "On the desktop", "Normal window", "Always on top" };
        for (int i = 0; i < 3; i++) { int v = i; place.DropDownItems.Add(Item(layers[i], Layer() == v, () => SetLayer(v))); }
        place.DropDownItems.Add(new ToolStripSeparator());
        place.DropDownItems.Add(Item(LockText, Locked(), () => SetLocked(!Locked())));
        if (ClickThrough != null) place.DropDownItems.Add(Item("Click-through when locked", ClickThrough(), () => SetClickThrough(!ClickThrough())));
        place.DropDownItems.Add(Item("Snap to screen edges", Snap(), () => SetSnap(!Snap())));
        items.Add(place);
    }

    // The rest: Settings…, Run at startup, Backup, then the version and Exit.
    public void AddEnd(ToolStripItemCollection items)
    {
        items.Add(new ToolStripSeparator());
        if (OpenSettings != null) items.Add(Item("Settings…", false, OpenSettings));
        if (RunAtStartup != null) items.Add(Item("Run at startup", RunAtStartup(), () => SetRunAtStartup(!RunAtStartup())));
        if (BackUp != null)
        {
            var backup = new ToolStripMenuItem("Backup");
            backup.DropDownItems.Add(Item("Back up settings", false, BackUp));
            backup.DropDownItems.Add(Item("Restore settings…", false, Restore));
            backup.DropDownItems.Add(new ToolStripMenuItem("Last backup: " + LastBackup()) { Enabled = false });
            items.Add(backup);
        }
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(Title + " " + Version) { Enabled = false });
        if (Exit != null) items.Add(Item("Exit", false, Exit));
    }

    // The same options as the shared pages of a settings window (Look, Placement, General, About).
    public KinetikSuite.SettingsWindow.Common ForSettings()
    {
        return new KinetikSuite.SettingsWindow.Common
        {
            Title = Title, Version = Version, OpenTheme = OpenTheme,
            Scale = Scale, SetScale = SetScale, Scales = Scales, Widths = Widths, CardWidth = CardWidth, SetCardWidth = SetCardWidth,
            FontName = FontName, SetFontName = SetFontName, Fonts = Fonts.Where(HasFont).ToArray(),
            Opacity = Opacity, SetOpacity = SetOpacity, Opacities = Opacities,
            Layer = Layer, SetLayer = SetLayer, Locked = Locked, SetLocked = SetLocked, LockText = LockText,
            ClickThrough = ClickThrough, SetClickThrough = SetClickThrough, Snap = Snap, SetSnap = SetSnap,
            RunAtStartup = RunAtStartup, SetRunAtStartup = SetRunAtStartup, BackUp = BackUp, Restore = Restore, LastBackup = LastBackup,
        };
    }

    public void AddAll(ToolStripItemCollection items) { AddLook(items); AddEnd(items); }
}
#pragma warning restore 0649
