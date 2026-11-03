# Mini apps and timers

Open BrimDeck and choose the four-square icon. Settings → Tools → Mini apps controls the module, widget availability and order, the default widget and panel dimensions. Pomodoro, Countdown and Stopwatch each have a separate settings page listed beneath Mini apps. The panel’s settings button opens the module page. Expanded view also retains the visibility and dimension controls.

Drag the six-dot handle to the left of each widget to reorder both the sidebar pages and island tabs. Turning off a widget hides its child page and settings entry while preserving its position and preferences. Turning off the module hides every child page and keeps module management available. Input methods and Mini apps carry a Beta badge in the sidebar.

Restore defaults applies only to the current page: module management resets availability, order, the default widget and panel dimensions; a child page resets only that widget’s preferences.

The default can remember the last widget or open a fixed widget. Browsing tabs does not overwrite a fixed default; entering the page or reopening restores it. An active session still takes precedence.

Turning off the module or its active widget ends the current session and keeps preferences. Disabling another widget does not affect the running timer. All three widgets can be hidden; the empty page offers a settings entry.

## Timer modes

- **Pomodoro:** 25-minute focus, 5-minute short breaks and a 15-minute long break after four focus sessions by default. All are adjustable. Focus automatically starts a break; the next focus session requires a click.
- **Countdown:** 1, 5, 10 and 25-minute presets, or separate hour, minute and second controls (1 second to 99:59:59). A completed timer remains until dismissed with End.
- **Stopwatch:** pause, resume, reset and up to 200 laps, with split and cumulative times. Settings controls lap recording, the lap limit and hundredths in records.

Only one session runs at a time. End it before starting another mode.

## Controls and presentation

Start morphs the configuration panel into an expanded control strip. The circular button pauses or resumes, the phase label and time form one entry into settings and laps. End the session from that page. The strip follows the notch or capsule shape and has no close button. The strip stays open until the pointer first enters it; leaving then uses the normal closing delay. Esc collapses it without stopping the timer.

The compact island shows time, status and progress. Hover to reopen controls; select the phase label to return to Mini apps and browse the other pages. Temporary input-method notifications restore the timer when finished. Full-screen visibility rules, line mode, themes and reduced motion remain effective.

## Adaptive panel

None of the timer pages scroll. Narrow panels stack cards; wide panels place timing and configuration or records side by side. Size tiers adjust spacing, type and card arrangement. Normal panels show editors directly. Very short Pomodoro panels show every parameter as a summary that opens its own editing view; short countdown panels offer Set duration. Back or Escape returns to the original page.

Lap records use pages sized to the available space. Stopwatches beyond 99 hours retain the complete elapsed time, using fitted text when the rolling wheels have too few positions. Newer/Older controls reach every record. Recording a lap or changing pages updates only the records; the clock and actions stay in place. Reflow shares the usage, music and monitor content handover animation and respects reduced motion.

## Adjusting duration

Each widget’s settings page holds its own Pomodoro rhythm, default countdown duration or stopwatch recording options. Pomodoro and stopwatch session preferences are captured at start; editing defaults does not alter a running session.

Click minus/plus, hold to accelerate, or drag digits vertically with a short inertial coast. Double-click or press Enter/F2 to type; Enter commits and Escape cancels. Arrows adjust by one, Shift+arrow and Page Up/Down by ten, and Home/End select a limit.

Digits roll individually, with base-sixty time carries and borrows. Live time reserves its digits to keep the island stable. Reduced motion updates immediately. Editing and dragging keep the panel open. Unrelated settings keep the same editor; resizing, theme and language changes preserve uncommitted input and validation state. Preference edits synchronize across panels.

## Sleep and persistence

Sleep pauses the session; resume manually after waking. Normal exit saves progress, and reopening restores it paused. `timer-session.json` is stored beside the application settings. Display ticks do not write files. After an abnormal exit, recovery uses the last start, pause, lap or phase checkpoint.

Ending a session clears its laps and keeps duration preferences.
