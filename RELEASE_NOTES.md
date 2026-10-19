# Release notes

## v2.0.0

The Kinetik apps' new look.

- **Settings in Kinetik Stats' layout:** rows join into cards with thin dividers, small-caps headings, and drop-downs, switches and buttons in the app theme.
- **App theme:** one theme and accent colour for every Kinetik app's windows and menus: Match Windows, Graphite, OLED black, Midnight, Nord, Mocha, Forest, Light or Paper, with a Sky, Violet, Mint, Amber, Rose or Coral accent or your Windows accent. Pick it in Settings → General, under **App theme** in the right-click menu, in Kinetik Hub or in Kinetik Stats, and every app follows.
- **Widget themes:** new **OLED black** and **Mocha**, and **Match app theme**, which uses the app theme and accent colour.
- **New icon** in the Kinetik apps' "Graphite glass" style.

## v1.1.4

Fixes and the shared Kinetik menu.

- **Same right-click menu as the other Kinetik apps:** Theme, Size, Font, Background opacity, Placement (with lock, click-through and snapping), Settings, Run at startup and Backup, in the same order and words everywhere
- **Default background opacity is now 95%** (was 92%); if yours was at the old default, it moves with it
- **Right-click menus close when you click elsewhere,** the desktop included. They used to stay open
- **Fixed: monthly events on a day counted from the end of the month** (for example "the last day") in daily-repeat rules
- **Restoring settings** replaces only the calendar's own settings, instead of merging old calendar entries back in
- **Closes cleanly when Kinetik Hub asks it to,** so Hub can restart and update it without force-closing it
- **Safer settings:** out-of-range values are backed up and corrected when the app starts, and settings that can't be read are never saved over
- `build.bat` keeps the exe it replaces in `..\Old Versions`, and `build.bat package` checks every file in the release zip against its SHA-256 hash

## v1.1.2

- **Click a day** in the month or week view to jump to its events
- **Move between months** (or weeks) with the arrows, the mouse wheel and a **Today** button
- **Back up and restore settings:** Settings → Behaviour → Backup saves `KinetikCalendar-settings.reg` next to the exe; on a fresh Windows with no settings, the app offers to restore it
- **Safer settings loading:** if the saved settings can't be read (for example while OneDrive or a restore has them busy), the app retries, then runs on defaults without ever saving over your real settings. One unreadable calendar no longer loses the others
- **Faster animation:** the sync spinner, shimmer and scrolling draw a frame in about 1.7 ms instead of 11 ms
- Fixed: the "Open calendar" submenu leaked a window each time the menu opened

## v1.1.1

- Today's circle in the month view is now centred on the date in every font (it sat off-centre in fonts like Bahnschrift)

## v1.1.0: themes and a new Settings window

- **Settings redesigned** in Kinetik's style: a sidebar, cards with switches and sliders, and a live preview of the widget
- **13 themes plus Match Windows:** Midnight, Navy, Graphite, Nord, Dracula, Solarized, Forest, Ocean, Sunset, Mono, Light, Paper, Frost. Custom accent and background colours
- **Layout options:** big clock title, week view, week numbers, first day of the week, weekend shading, spacing, and bar / dot / tinted-card event markers
- **Agenda options:** events per day, empty days, all-day events, locations, countdown and progress bars
- **Duplicates merged:** an event in two calendars shows once, in both colours
- **Animations:** fade-in, rising rows, hover highlights, smooth resizing and scrolling, sync spinner, progress-bar glint
- **Scrolling** when the agenda is taller than the screen, with the title and month kept in place
- **Sharper text:** ClearType on the semi-transparent card
- **Click-through when locked**, **Reset position**, and **click to open** can be switched off
- **`--demo` mode** with made-up calendars
- **Lighter:** wakes once a minute, skips unchanged calendars (ETag, Last-Modified and content hash), keeps only the parts of events it shows, pauses while the PC is locked. About 13 MB in use

## v1.0.0: first release

- Desktop agenda widget for Google, Outlook / Microsoft 365 and iCloud calendars via private iCal links
- Month view with event dots, agenda for up to 31 days, now / countdown / progress bar
- Recurring events, time zones, exceptions and moved occurrences
- Offline copies, tray icon, drag and snap, placement options, run at startup
