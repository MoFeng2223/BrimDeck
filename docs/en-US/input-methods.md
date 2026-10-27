# Input-method switching

Open **Settings → Input methods**, enable the module, and choose **Notify only** or **Control switching**. Existing configurations retain control mode. Switching modes preserves your recorded shortcut, participants and order.

## Notify only

Keep the original Windows/input-method shortcuts and switcher. The island briefly reports verified active-profile changes without opening the selector or restricting Windows' choices. This mode creates no keyboard shortcut service and never requests profile activation. Recording and ordering controls are hidden.

The observer reuses the target-thread read about every 250 ms. A changed profile must remain stable for at least 150 ms and switching modifiers must be released before a notice is shown. The first observation is silent, duplicates are suppressed, and rapid changes are combined into a stable result. A different active profile after changing applications is also recognized. Notices are skipped while the normal panel is open.

Unreadable targets are skipped with a retry backoff. Keys alone never prove success. Disabling the module, changing modes or locking the session clears the observation state.

## Control switching

Choose participants. Drag rows or use Move up / Move down to set a fixed cycle order. These controls affect control mode only.

By default, hold **Win** and tap **Space** to preview the next item. Release the final Win key to apply. Repeated taps follow the saved order; holding Space does not advance repeatedly. Previewing does not activate intermediate items.

Click **Switching shortcut**, then press and release a combination to save it. Examples include `Ctrl+Space`, `Ctrl+Shift+Space`, `Alt+K` and modifier-only chords such as `Ctrl+Shift`. For the latter, hold Ctrl and tap Shift to cycle, then release Ctrl to apply; the reverse press order also works. For a main key with several modifiers, releasing any required modifier applies the selection. The selector shows instructions for the saved shortcut.

Recording temporarily suspends switching and consumes the recorded keys. Esc, moving focus away, deactivating settings or a 30-second timeout cancels without changing the saved shortcut. Clear releases interception; **Restore Win + Space** restores the default. Combinations that duplicate hide/show or expand are rejected, as are ordinary typing keys, a single modifier and some reserved system combinations.

The highlight and arrow indicate the pending choice; **Active** identifies the actual input method at the start of the session. Focus stays in the original application. A confirmed result appears for about 650 ms, using only a success mark in a narrow island, then the original clock, music or usage content returns. No input-method indicator stays on screen.

The vertical selector shows every participant without scrolling. Row heights, icons and name/status columns stay consistent; only the highlight moves. A dense list scales uniformly to fit the available screen area. The selector opens without size overshoot, and the original compact frame stays fixed throughout feedback, regardless of name length.

Confirmed feedback fades in during the existing collapse, remains fully visible for about 650 ms after settling, then crossfades back using the normal page transition. Indicator mode settles directly into the result capsule and returns to the line afterwards, avoiding a second expansion. Disabling animations completes each state immediately.

Excluding an item affects only BrimDeck's cycle. Unavailable selections remain saved and are skipped; newly installed profiles are not automatically selected. Windows handles the shortcut when the participant list is empty, the island is hidden, or the target is unsupported. Disabling the module or exiting releases the shortcut.

## Compatibility and implementation

The first version targets standard x64 desktop applications on Windows 10 2004 or later. Windows handles 32-bit applications, higher-privilege windows, protected applications and targets the native adapter cannot reach. Settings retains a message about the last unsupported target. Running BrimDeck as administrator is not required.

Enumeration uses Windows TSF. A small native adapter installs a temporary, thread-specific call-window hook to read or activate the exact profile on the foreground window's own thread. It does not read input text, record typed content, change the default input method or modify Windows' participant list. Success requires the target thread's actual profile identity to match the requested one.

The module shares the existing island window, motion, theme resources and settings controls. Ordinary pages retain their state while the temporary vertical selector is shown.

## Building and checking

Install .NET 10 SDK, Visual Studio 2022's **Desktop development with C++** workload and a Windows SDK. Normal builds run `build/BuildInputMethodNative.ps1` to compile `src/BrimDeck.InputMethods.Native`. Include `BrimDeck.InputMethods.Native.dll` when distributing the application.

```powershell
dotnet build BrimDeck.sln -c Release
dotnet run --project tests/BrimDeck.Tests -c Release --no-build
dotnet run --project tests/BrimDeck.MediaTests -c Release --no-build
dotnet run --project tests/BrimDeck.WindowsTests -c Release --no-build
```

Automated checks cover configuration copying, cycle order, exclusions, key state, TSF enumeration and the native request layout. Physical testing remains necessary for shortcuts, privilege boundaries, full-screen behavior, multiple monitors and DPI changes.
