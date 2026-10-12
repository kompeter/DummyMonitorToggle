# DummyMonitorToggle

A one-click on/off switch for a dummy display plug (or any second monitor) on Windows.

- **Off**: the monitor is disconnected from the desktop. The mouse can't wander onto it and no window can open there.
- **On**: it is back as an extended display.

One small exe, no installer, no admin rights, no background process.

## Why

A dummy HDMI/DisplayPort plug is great for game streaming with [Sunshine](https://github.com/LizardByte/Sunshine): it gives the PC a "monitor" to render to at whatever resolution the client wants. The rest of the time it is a nuisance, because Windows treats it as a real screen you can't see:

- the mouse slides off the edge of your real monitor and disappears
- windows and dialogs open on the invisible screen
- after the displays sleep, the dummy wakes up first, and windows (virtual machine consoles are a common victim) end up on it
- waking from sleep takes longer

You can fix all of that by picking "Show only on 1" in Display settings and "Extend" again before every stream. This tool does the same thing with a double-click or a command line, so it can be automated.

## What it does and doesn't do

It uses the standard Windows display APIs to detach or attach one specific monitor, exactly like *Disconnect this display* / *Extend these displays* in **Settings > System > Display**.

While the plug is physically connected, Windows still lists the monitor in Display settings as a small greyed-out box. That is normal: greyed-out means it is not part of the desktop. No software can make a connected plug vanish from that list reliably (disabling the monitor in Device Manager does not stop Windows from using it).

## Install

**Option A - download.** Get `DummyToggle.exe` from the [Releases](../../releases) page and put it in a permanent folder such as `C:\Tools\DummyToggle`. The exe is not code-signed, so SmartScreen may warn the first time: *More info > Run anyway*.

**Option B - build it yourself** (takes five seconds, nothing to install). Download this repository, put `DummyToggle.cs` and `build.cmd` in a permanent folder and double-click `build.cmd`. It uses the C# compiler that is already part of Windows. The whole program is one readable source file, so you can check what it does first.

## Setup

1. Make sure the monitor you want to toggle is currently on.
2. Double-click `DummyToggle.exe`.
3. Pick the monitor to toggle from the list (your dummy plug, not your main screen).
4. A **Monitor Toggle** shortcut appears on your desktop. Rename it, move it or pin it as you like.

Double-click the shortcut to switch the monitor off; double-click again to switch it on. A small banner shows the new state.

## Commands

| Command | What it does |
| --- | --- |
| `DummyToggle.exe` | First run: setup. Afterwards: toggle. |
| `DummyToggle.exe toggle` | Toggle and show the new state on screen. |
| `DummyToggle.exe on` | Attach the monitor. Silent, waits until done. |
| `DummyToggle.exe off` | Detach the monitor. Silent, waits until done. |
| `DummyToggle.exe setup` | Choose the monitor again and recreate the shortcut. |
| `DummyToggle.exe uninstall` | Remove the shortcut and saved settings. |

`on` and `off` never show a window, so they are safe in scripts. They exit with code 0 on success and 1 on failure, and write errors to `%APPDATA%\DummyMonitorToggle\DummyToggle.log`.

## Using it with Sunshine

Keep the dummy off for everyday use and let Sunshine switch it on only while streaming. In the Sunshine web UI, under **Configuration > General > Command Preparations**, add:

| Do command | Undo command |
| --- | --- |
| `"C:\Tools\DummyToggle\DummyToggle.exe" on` | `"C:\Tools\DummyToggle\DummyToggle.exe" off` |

Put it **above** any commands that move the stream to the dummy or change its resolution: Sunshine runs the do commands top to bottom and the undo commands in reverse, so the dummy is attached first and detached last. "Run as admin" is not needed.

## Troubleshooting

- **"... is currently the main display"** - the tool refuses to detach your primary display. Make another monitor the main one first.
- **"... did not turn on/off"** - check the plug is connected and the monitor is not disabled in Device Manager, then run `DummyToggle.exe setup`.
- **You moved the plug to another port or moved the exe** - run `DummyToggle.exe setup` again.
- **More than two monitors** - `on` tries to attach only the chosen monitor; if Windows rejects that, it falls back to extending the desktop to all connected displays.

## Uninstall

Run `DummyToggle.exe uninstall`, then delete the exe. Nothing else is left behind.

## License

[MIT](LICENSE)
