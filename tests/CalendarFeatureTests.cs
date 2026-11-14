using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;

// Compile alongside the app with /main:CalendarFeatureTests (KinetikCalendar.cs and SuiteSupport.cs). Nothing is read from or saved to this PC.
// With a folder as the first argument it also draws the demo widget there (calendar-<theme>.png).
static class CalendarFeatureTests
{
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    static CalEvent Ev(string title, DateTime start, int minutes, bool allDay = false)
    {
        return new CalEvent { Title = title, Start = start, End = start.AddMinutes(minutes), AllDay = allDay, Feed = new Feed { Name = "Test", Color = Color.Blue } };
    }

    [STAThread]
    static void Main(string[] args)
    {
        // categories and colours from the .ics
        var day = DateTime.Today.AddDays(1);
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nSUMMARY:Planning\r\nDTSTART:" + day.AddHours(10).ToString("yyyyMMdd'T'HHmmss") + "\r\nDTEND:" + day.AddHours(11).ToString("yyyyMMdd'T'HHmmss") + "\r\nCATEGORIES:Work\\, Big,Other\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:b\r\nSUMMARY:Party\r\nDTSTART:" + day.AddHours(18).ToString("yyyyMMdd'T'HHmmss") + "\r\nCOLOR:#FF0000\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:c\r\nSUMMARY:Plain\r\nDTSTART:" + day.AddHours(20).ToString("yyyyMMdd'T'HHmmss") + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var feed = new Feed { Name = "T", Color = Color.Blue };
        var events = Ics.Expand(Ics.Parse(ics), feed, DateTime.Today, DateTime.Today.AddDays(3)).OrderBy(e => e.Start).ToList();
        Check(events.Count == 3 && events[0].Category == "Work, Big" && events[0].Own == null, "Category is the first of CATEGORIES, with escaped commas kept");
        Check(events[1].Own.HasValue && events[1].Own.Value.R == 255 && events[1].Own.Value.G == 0 && Hue.Of(events[1]) == events[1].Own.Value, "COLOR property gives the event its own colour");
        Check(events[2].Category == null && Hue.Of(events[2]) == feed.Color, "An event with neither keeps its calendar's colour");

        var map = Cats.Parse("Work, Big=" + Color.Red.ToArgb() + ";Other=" + Color.Green.ToArgb() + ";bad;x=notanumber");
        Check(map.Count == 2 && map["work, big"].R == 255 && Cats.Parse(Cats.Join(map)).Count == 2, "Category colours round trip, ignoring bad entries");

        // reminders
        var now = new DateTime(2026, 11, 14, 9, 0, 0);
        var list = new List<CalEvent> { Ev("Soon", now.AddMinutes(8), 30), Ev("Later", now.AddMinutes(40), 30), Ev("Started", now.AddMinutes(-5), 30), Ev("All day", now.Date, 1440, true), Ev("Also soon", now.AddMinutes(10), 30) };
        var seen = new Dictionary<string, DateTime>();
        var due = Reminders.Due(list, now, 10, seen);
        Check(due.Count == 2 && due[0].Title == "Soon" && due[1].Title == "Also soon", "Only timed events starting within the lead time are due, soonest first");
        Check(Reminders.Due(list, now.AddMinutes(1), 10, seen).Count == 0, "An event is announced once");
        Check(Reminders.Due(list, now, 0, new Dictionary<string, DateTime>()).Count == 0, "Reminders off announces nothing");
        Check(Reminders.Due(list, now.AddMinutes(31), 10, seen).Count == 1, "The next event becomes due on its own");
        var one = Reminders.Message(due.Take(1).ToList(), now);
        Check(one[0] == "Soon" && one[1] == "Starts in 8 min", "One reminder reads as the event and its time");
        Check(Reminders.Message(due, now)[0] == "2 events starting soon", "Several reminders are one notification");
        seen["old"] = now.AddDays(-3); Reminders.Due(list, now, 10, seen);
        Check(!seen.ContainsKey("old"), "Old reminders are forgotten");

        // next event for the other apps
        var next = NextEvent.Pick(list, now);
        Check(next.Title == "Soon" && NextEvent.Line(next).Split('\t').Length == 5 && NextEvent.Line(null) == "", "Next event is the soonest timed one in the future");

        // a calendar file on this PC
        Check(Net.LocalPath(@"C:\x\a.ics") == @"C:\x\a.ics" && Net.LocalPath("https://example.com/a.ics") == null && Net.LocalPath("file:///C:/x/a.ics") == @"C:\x\a.ics", "Local paths are told from links");
        var dir = Path.Combine(Path.GetTempPath(), "cal-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var read = typeof(Net).GetMethod("ReadLocal", BindingFlags.NonPublic | BindingFlags.Static);
            var file = Path.Combine(dir, "x.ics"); File.WriteAllText(file, ics);
            Check(Ics.Parse((string)read.Invoke(null, new object[] { file })).Count == 3, "A calendar file is read");
            File.WriteAllText(file, "not a calendar");
            try { read.Invoke(null, new object[] { file }); Check(false, "A non-calendar file is refused"); }
            catch (TargetInvocationException ex) { Check(ex.InnerException.Message.Contains("isn't a calendar"), "A non-calendar file says so"); }
            try { read.Invoke(null, new object[] { Path.Combine(dir, "gone.ics") }); Check(false, "A missing file is reported"); }
            catch (TargetInvocationException ex) { Check(ex.InnerException.Message.Contains("isn't there"), "A missing file says so"); }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // weather
        var wx = Wx.Parse("{\"daily\":{\"time\":[\"2026-11-14\",\"2026-11-15\",\"2026-11-16\"],\"weather_code\":[3,61,0],\"temperature_2m_max\":[21.4,18.0,25.5],\"temperature_2m_min\":[12.1,null,14.0]}}");
        Check(wx.Count == 2 && wx[new DateTime(2026, 11, 14)].Code == 3 && Wx.Text(wx[new DateTime(2026, 11, 16)]) == "26°/14°", "Daily forecast is read, skipping days with gaps");
        using (var bmp = new Bitmap(60, 60))
        using (var g = Graphics.FromImage(bmp))
            foreach (int code in new[] { 0, 1, 2, 3, 45, 51, 61, 71, 80, 85, 95, 99, 123 }) Wx.Icon(g, code, new RectangleF(10, 10, 40, 40), 230);
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KinetikWeather", "forecast.json");
        if (File.Exists(real)) Console.WriteLine("Kinetik Weather's saved forecast has " + Wx.Parse(File.ReadAllText(real)).Count + " days");

        Console.WriteLine("PASS: categories, colours, reminders, next event, calendar files and weather.");
        if (args.Length == 0) return;

        // the demo widget drawn from the real painter, in a couple of themes
        Program.Demo = true;
        var app = new CalApp(false);
        Directory.CreateDirectory(args[0]);
        foreach (var theme in new[] { "Midnight", "Light" })
        {
            app.Cfg.ThemeName = theme; app.Cfg.MonthStyle = 0; app.Cfg.Days = 7;
            app.Rebuild();
            var p = new Painter(app);
            p.CopyFrom(app.Widget.P);
            p.Layout(1.25f, 1100);
            using (var bmp = new Bitmap(p.WinW, p.WinH, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp)) p.Paint(g);
                bmp.Save(Path.Combine(args[0], "calendar-" + theme + ".png"));
            }
        }
        Environment.Exit(0);
    }
}
