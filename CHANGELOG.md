# Patch Release v1.2.1

## Changed

- Moved to github for the project homepage.

# Minor Release v1.2.0

## Improved
- The window can now be resized freely: the lists of detected apps, blacklisted apps and leveled apps all grow and shrink together with the window, instead of only the bottom list being stretched.
- The "Blacklisted apps" list is now taller by default, so more entries are visible at once.
- The window can no longer be made so small that the lists become unusable.

## Changed
- Volume corrections are now applied in gentler steps instead of one big jump. Large differences are closed over two or three adjustments, which softens the volume boost that could happen when a track starts w
ith a quiet intro.
- Volume changes now fade over 3 seconds instead of 1.5 seconds, so adjustments sound smoother.
- Detection of sudden loudness changes (for example a new track with no pause before it) now reacts to smaller differences (30% instead of 50%).
- The slow background check for gradual volume drift now averages over 2 minutes instead of 45 seconds and triggers at a smaller deviation (40% instead of 50%), so it ignores normal musical phrasing and only r
eacts to real, sustained changes.
- The status text shown for each app is now stricter: "On target" appears only within 15% of the reference (previously 30%).
- When you press Stop, the status line now clearly says that leveling has stopped ("Leveling stopped. Tracking N app(s) - volumes left unchanged."), instead of just showing "Tracking N app(s).".
- Before the first Start, the status line now shows the “Press Start...” message instead of "Tracking 0 app(s).".

---

# Minor Release v1.1.0

- Fixed app autostart with Windows - we're now using a scheduled task to run the app with a sligth delay after user logon.

---

# Initial Release v1.0

## App Features

- Automatically balances the volume of every app playing sound on your PC, so you never have to reach for the volume mixer again
- Works across multiple apps at once — music player, browser, games, chat apps — each leveled independently to the same target loudness
- One shared reference level, adjustable at any time with a simple slider
- "Seed from app" button lets you set the reference by picking a level you already like from something currently playing
- Three savable reference presets (e.g. "Night", "Party", "Focus") to switch quickly between listening situations
- Smooth, gradual volume changes — corrections are never abrupt or jarring
- Automatically detects new apps as they start playing sound, and stops tracking apps once they close
- Blacklist any app you never want touched (e.g. notification sounds), with an easy right-click option
- Runs quietly from the system tray, with an optional global shortcut (Ctrl+Alt+L) to pause leveling instantly without closing the app
- Optional automatic start with Windows, and optional automatic start of leveling itself — set it up once and forget about it
- Remembers your settings, volume levels, and window position between sessions
- Clean, dark-themed interface with a live view of every tracked app's status and volume
- Built-in "reset volumes" option to bring every app back to a neutral starting point
- Super lightweight on memory and CPU