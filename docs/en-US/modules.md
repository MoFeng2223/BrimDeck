# Modules (experimental)

Module authors can use the standalone SDK with the three primary documents: the [development guide](../zh-CN/modules/contributing.md), [API reference](../zh-CN/modules/api.md) and [platform specification](../../mods/README.md). No host source project reference or source reading is required. The SDK includes compile-only reference assemblies, a static checker, and complete clock, counter and quota-alert templates extracted directly from the guide. Maintainers export and verify it with `build/ExportModuleSdk.ps1` and `build/TestModuleAuthoring.ps1`; no public SDK distribution channel is available yet.

If a previous module startup did not finish, BrimDeck asks whether to skip modules for this run before executing their code. Saved enablement, approvals and bundle receipts are retained; enabled modules can still be disabled from management. `BrimDeck.exe --no-modules` explicitly starts one session without modules. Startup journals remain pending until activation and first presentation complete with responsive UI heartbeats. Runtime hang recovery is not implemented.

Authors can run `tools/BrimDeck.ModuleCheck` against a built package. It reads manifests and PE metadata without loading module assemblies or calling constructors, activation, setting getters or user storage. Use `--json`, `--modules-root` and `--warnings-as-errors` for CI. See [checker and recovery rules](../zh-CN/modules/checker.md) for limits and exit codes.

Modules are separate assemblies that contribute pages, persistent activities, widgets and settings through public contracts. `mods/timers` is the complete bundled reference module for Pomodoro, countdown and stopwatch. It is discovered and loaded as an ordinary external DLL, uses the public SDK, and owns its independent domain library and storage.

## Installation

Open **Tools → Modules → Open module folder**. Put each module in its own subdirectory with `module.json`, its entry DLL and any private dependencies. Refresh and enable it explicitly. Discovery reads metadata without executing the module; changed files require refreshing and enabling again. Dependencies must also be enabled explicitly.

```json
{
  "apiVersion": 1,
  "id": "sample-notes",
  "name": "Notes sample",
  "version": "1.0.0",
  "assembly": "BrimDeck.Sample.dll",
  "entryType": "BrimDeck.Sample.NotesModule",
  "dependencies": []
}
```

IDs use lowercase ASCII letters, digits and hyphens, up to 80 characters. `builtin-` is reserved. Versions use .NET `Version` syntax. Dependencies accept `minimumVersion` and optional `maximumVersionExclusive`; arbitrary range expressions are unsupported. Each module is limited to 2000 files and 64 MiB, without linked files or directories.

The loader uses a verified shadow copy so original DLLs can be replaced while active. Identical content reuses its cached copy. Each module's details show data/cache sizes and a Clear cache action. Unused copies are removed immediately; live or locked files remain queued until a later normal startup or refresh. Business data is retained when clearing caches.

Uninstall asks for confirmation, stops the module and its dependents, and removes the package, business data, caches and saved approval. Owned directories are quarantined before deletion, so retries cannot erase a later installation. Locked files remain visibly pending, while other data is still cleaned. Bundled modules do not automatically reappear; Restore bundled is an explicit action. A known package folder that is actually missing during a successful normal discovery is treated as manual removal; a rename preserving its ID or a discovery error does not imply uninstall. Marketplace and ZIP installation are not available.

Removal jobs bind to directory identity, not only a path. Replacements and legacy sources without sufficient ownership information are preserved with an explicit message. Failed journal writes roll back in-memory stages, and removal preferences are persisted again on every retry. Refresh also retries maintenance with modules switched off; busy actions report rejection rather than success. Runtime and static checks reject duplicate manifest fields, all packages sharing a conflicting ID, linked ancestors and excessive directory depth/entry counts.

These are trusted, in-process WPF extensions. Cleanup and ordinary exception handling cannot isolate a dead loop, process crash or arbitrary file access. Content confirmation is not a signature or security sandbox.

## Contracts and ownership

The manager uses compact expandable cards for state, version, toggles, component controls and settings. The performance tab displays last startup duration, a five-second average of callback time per second, longest callback, totals, slow calls (at least 16 ms) and errors. Startup includes loading, activation and asynchronous waiting. Runtime figures cover host-managed synchronous events, timers and view operations, with nested time counted once; they are not per-process CPU figures and exclude independently created background work.

Counters cover this app session and survive module disable. The rate window has fixed storage, without a background sampler, per-call history or continuous disk writes. Settings update values in place once a second and pause UI refresh when leaving modules, minimizing or closing settings.

`BrimDeck.Modules.Abstractions` contains non-UI contracts. `BrimDeck.Modules` supplies discovery, dependency ordering, activation and cleanup. `BrimDeck.UI` supplies shared WPF contracts, controls, themes and motion. Modules must not reference the `BrimDeck` desktop assembly. Shared SDK or theme changes still require regression testing.

Implement `IModule` with a public parameterless constructor and `ActivateAsync`. Contributions registered during activation are staged until successful completion. IDs become `module-id:local-id`; the same local ID may be used for different contribution types. Disposing a registration removes it and permits re-registering that ID.

- Use `IModuleContext` for module-level timers, events, resources and persistent state. Own disposable resources with `Own` or `OnDispose`. Disposal cancels the lifetime and cleans resources in reverse order, continuing after individual cleanup errors.
- Use `ModuleViewContext` for view-level timers and event cleanup. Navigating away disposes the view but keeps module state alive. Each monitor gets its own view over the same session.
- The desktop host invokes lifecycle and view operations on its UI dispatcher. Run expensive work asynchronously, return to the dispatcher before changing WPF controls and observe the lifetime cancellation token.
- Activation runs independently. Five seconds marks a slow start without cancelling it; synchronous blocking still cannot be interrupted. Missing, disabled, incompatible, failed or cyclic dependencies block their dependents while healthy modules continue.
- Ended contexts reject new registrations and storage writes. Late cleanup registrations are disposed immediately. View creation, refresh, layout and disposal exceptions are recorded.

## Contributions

`ModulePage` supplies a title, order and `IModuleView` factory. Adapt layout to the dimensions passed to `SetViewport`. Multiple pages share the host's page selection menu.

`IModuleActivity` supplies compact and expanded views, progress, accent and completion state. The host owns geometry, handovers and notification priority. It presents one activity at a time, preferring the explicitly selected active ID, then the first active entry. Expanded views receive a structured `ModuleViewport` (320 × 76 DIP), separating content and device scale from insets. Temporary notifications restore the persistent activity afterward.

Contract 1.1 adds manifest `apiMinor`, required/optional capabilities and permissions. Legacy manifests without a minor retain minor-0 contribution compatibility. `IModuleUsage`, `IModuleMedia` and `IModuleSystem` expose immutable host snapshots; `IModuleNotifications` supplies bounded, rate-limited island notices. The [usage alerts sample](../../mods/usage-alerts/README.md) demonstrates a configurable Codex threshold without reading credentials or referencing desktop services. See the [API reference](../zh-CN/modules/api.md) for lifecycle and access rules.

Contract 1.2 adds a single capability catalog for IDs, descriptions, permissions, versions and public contract names. Host bindings are checked against it before module activation, and generated documentation detects drift. The static catalog lists SDK-known capabilities; `GetInfo().CapabilityDetails` lists the current host's actual adapters. Existing 1.1 modules retain their manifest requirements and services.

Contract 1.3 starts independent modules without serializing their asynchronous waits. Dependents wait for exact dependency generations; unchanged pending activations are reused. The native window appears before external discovery and activation. Five seconds now marks a slow start rather than failing activation; late success can still commit. A finite hard timeout is optional host configuration. Waiting is appended to existing status values, diagnostics carry a generation, and retired errors cannot overwrite replacement state. Synchronous plugin code must still avoid blocking the UI thread.

Contract 1.4 adds `ModulePage.Async` and optional `IModuleAsyncActivity`. Loading has a static host placeholder and a shared content handover. View resources belong to an exact activation before the factory starts; navigation, disable or reload cancels the view context and disposes late results. The latest normalized viewport is applied on completion, and view timers start only after mounting. Existing synchronous factories remain valid. Each presentation must return its own UI-dispatcher view and root element.

`ModuleWidget` exposes a component toggle; the module determines which pages to register or remove. `IModuleSettings.Visible` controls its sidebar entry. Number and boolean fields reuse existing settings controls; module code owns validation, writes and reset behavior. `ModuleUi` exposes common theme and motion entry points, respecting application and Windows animation preferences.

## Data and compatibility

Data lives under `modules-data/<id>/` in the application data directory. Scoped storage writes JSON keys with a 1 MiB per-file limit and temporary-file replacement; it does not provide multi-file transactions. Source modules live under `mods/`, and shadow copies under `modules-cache/<id>/<content-digest>/`.

The timer module preserves existing preferences and imports the old `timer-session.json` once. State transitions and shutdown save snapshots; display ticks do not write every second. Sleep, shutdown and restart leave a session paused for manual resume. Disabling the entire module system saves paused progress; disabling the active timer widget or original mini-app switch retains its existing behavior of ending the session.

Existing page enum values remain unchanged. New preferences support defaults, normalization and deep copying. Music and input methods remain on their existing services in this release.

See the [external sample](../../mods/notes/README.md), [detailed Chinese guide](../zh-CN/modules.md) and `tests/BrimDeck.ModuleTests` for executable examples and lifecycle checks.

Contract 1.5 exposes `context.Platform().Registries`, `.Events` and `.Services` without changing the original entry interface. Registrations have namespaced disposable handles. Event handlers have stable priorities; host-only `ModulePageOpening` and `ModuleActivityOpening` hooks support one-way cancellation. The scoped `ui.presentation` service coalesces contribution refreshes. Widgets can supply an icon and module-owned reorder callbacks; settings add integer-valued choices. Timers use all these public surfaces and no UI friend access. All example source packages are under `mods/`, using `build/ModulePackage.targets`.

## Contract 1.6

Settings contributions may declare placement, order and an independent icon with `IModuleSettingsNavigation`; multiple widgets sharing settings produce one sidebar entry. The host validates Number, Toggle, Choice, Text and Color fields, supports steps, groups, descriptions and placeholders, and isolates malformed fields with deduplicated, nonfatal diagnostics. Authors still own business validation and persistence.

`ModuleViewContext.IsVisible`, `OnVisibilityChanged` and `EveryVisible` track actual host presentation. Display timers stop while collapsed or covered; module-level business tasks continue. Existing `Every` retains mount-lifetime semantics. Declare `apiMinor: 6` when using these additions.

See the [UI guide](../zh-CN/modules/ui.md), [host rules](../zh-CN/modules/host-rules.md) and [recipes](../zh-CN/modules/recipes.md) for exact behavior.
