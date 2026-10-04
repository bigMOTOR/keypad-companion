# Keypad Companion plugin

A Logitech MX Keypad dashboard with Ukrainian button labels. One page stays selected; its six lower buttons follow the active application. The top row and the capture button keep their positions.

| Context | Second row | Third row |
| --- | --- | --- |
| General | Capture / record · Last capture · AI context | Empty |
| Safari, Firefox | Capture / record · Last capture · AI context | Empty · New Meet · Empty |
| PrusaSlicer | Capture / record · Default view · Preview / editor | Left · Top · Right |
| Xcode | Capture / record · Error for AI · Empty | Empty |
| Slack, active Google Meet, or Zoom | Capture / record · Empty · Empty | Microphone · Camera · Share screen |

The top row is GPT, Claude, and Caffeinate. Idle AI buttons show remaining weekly allowance. During work, the activity label replaces the large percentage; the remaining percentage and progress bar stay below it. Unknown or old usage is labelled explicitly. GPT activity reads lifecycle markers, without persisting conversation text. Caffeinate shows the real timer and reads the duration chosen in Companion settings.

Capture opens Apple's Screenshot panel. During a detected macOS screen recording, the same button becomes a red Stop button with elapsed time since recording detection. AI context starts an area capture to the clipboard; it does not send an image to an AI service. Last capture opens the newest OS-named screenshot or recording in the configured capture folder or Desktop. Error for AI copies explicitly selected error text in Xcode.

New Meet sends the known New Tab shortcut and verifies that the active window gained exactly one tab. It assigns the complete fixed HTTPS address through macOS Accessibility and verifies exact readback, address-field focus and the active window twice. Safari confirms through the address field’s native Accessibility action. Firefox exposes no such confirmation action, so the plugin sends one unmodified Return directly to that Firefox process after the same checks; this does not use Logitech’s asynchronous keyboard queue. URL characters are never typed through Logitech’s keyboard API. Meeting controls require a freshly verified loaded `meet.google.com` document, and only a validated meeting-code URL can be copied. Unsupported fields or changed focus/window stop the action. Camera, microphone and Google sign-in prompts remain user actions. A passing build or URL validation test is not an end-to-end verification of the physical button. Safari has been physically confirmed in both Work and Personal profiles. Firefox has also been physically confirmed: the button creates an instant meeting and copies its validated link. Chrome is excluded from the owner’s setup and has not been tested.

Call states come from accessible controls in the active meeting or huddle. Unreadable states remain unknown. Screen sharing opens the application's picker; it never chooses a window automatically.

## Build and install

Requires macOS, Logi Options+ with LogiPluginService, its bundled `PluginApi.dll`, and .NET 10 SDK. The official Codex Desktop and Claude Desktop Logitech plugins provide session navigation. Install the Companion helper separately for brightness, Claude usage checks, and Caffeinate duration settings.

```sh
dotnet build plugin/src/MotorControlsPlugin.csproj -c Release -p:InstallPlugin=false
python3 plugin/install.py
```

The installer copies the build into `Keypad Companion.app/Contents/Resources/Plugin` and links Logitech to that directory. It does not add a startup job, process, Dock icon, or native executable. Reload the MotorControls plugin or restart Logitech afterwards. Keep Logitech's native Adapt setting off and select one General page. Assign GPT, Claude, Caffeinate to positions 1–3, then Dashboard 1–6 to positions 4–9.

## Tests

```sh
dotnet run --project plugin/test/dashboard-test.csproj
```

Checks capture position across all application and recording states, exact dashboard layouts, unknown call-state handling, independent call-state recovery, and strict Meet URL validation. PrusaSlicer’s five view controls and fixed top row, General’s Last capture and AI context, Xcode’s Error for AI, and capture/record/stop have been confirmed with the physical Keypad. These checks do not click apps, create a meeting, record the screen, or replace physical button checks.

## Privacy and permissions

The plugin uses Apple's signed system frameworks inside the existing Logitech host and its existing Accessibility permission. Shortcuts use Logitech's keyboard API. It does not alter macOS protections or request new permissions. Secure Input can prevent actions while macOS protects a password field.

No Claude credential is read by this plugin. It reads only the helper's sanitized numeric usage file. The existing authenticated Codex app server supplies Codex usage. No model requests or paid API calls are made for usage checks.

Runtime files belong in the user's Application Support directory, outside the repository. `dashboard-state.json` contains layout, recording time, call states, and fixed action notices. It excludes URLs, window titles, screenshots, clipboard content, and error text. `meet-operation.json` and `meet-result.json` store the latest fixed operation step and result for troubleshooting; they contain no typed address or page content. `call-capabilities.json` records only fixed action availability, native role and call state; it contains no URL or page text. Source, images, and package metadata may be shared; runtime files and private profiles must not be committed.

## Compatibility

This integration uses accessibility labels and Logitech's local plugin API. Application or OS updates and other UI languages can require adjustments. Call actions and recording detection should be verified on the installed versions. The initial display check alone does not establish that every action has been physically tested.

Icons follow the accepted tinted dashboard design. Context icons are based on Lucide (ISC license); GPT and Claude logos identify their respective applications. This is an independent project.

## Call control states

Meet controls are read from the loaded meeting document in one bounded pass, not from browser toolbar indicators. Only actionable toggle labels qualify; device-setting menus do not. A two-second grace period avoids flashing unknown states during UI redraws and never crosses into a different meeting. Muted microphones and stopped cameras are red; active controls are blue. Active screen sharing shows a red Stop sharing button. Zoom uses its meeting menu controls so hidden meeting toolbars do not determine status. Zoom microphone, camera and screen-sharing controls have been confirmed with the physical Keypad. The updated Meet state indicators and Stop sharing still require physical confirmation.
