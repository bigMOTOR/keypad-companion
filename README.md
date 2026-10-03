# Keypad Companion

A macOS helper for **Logitech MX Keypad**: automatic display brightness, configurable Caffeinate duration, and optional Claude weekly usage checks.

**The settings UI is in Ukrainian.** This README is in English so others looking for an MX Keypad brightness or AI workflow helper can find and use the project.

The helper starts when you log in and runs without a Dock or menu bar icon. Open its settings when you need them; closing the panel leaves the helper running.

![Ukrainian settings panel with Claude, Coffee, and Brightness tabs](docs/panel.png)

## Open settings

Press **⌘Space**, search for **Keypad Companion**, and press Enter. You can also open the [local settings panel](http://127.0.0.1:57973/) on the Mac where the helper is installed.

Settings are split into three tabs: **Claude**, **Кава** (Coffee), and **Яскравість** (Brightness). The Ukrainian controls are explained below.

The Brightness tab shows the current Mac display and Keypad brightness, with three modes:

| UI label | Behavior |
| --- | --- |
| **Автоматично** — Automatic | Match Keypad brightness to the Mac display. |
| **Приглушити до 08:00** — Dim until 08:00 | Use the minimum brightness until the next 08:00, then resume automatic mode. |
| **Пауза** — Pause | Stop automatic adjustments and keep the current brightness. |

## Adjust brightness

Edit the **Твоя яскравість** (Your brightness) table to set your own curve. For example, set **Mac display 40% → Keypad 15%**, then click **Зберегти** (Save). Add or remove points as needed; the 0% and 100% display endpoints must remain.

Brightness is interpolated between points. The installation defaults are:

| Mac display | Keypad |
| --- | --- |
| 0% | 10% |
| 25% | 16% |
| 40% | 20% |
| 50% | 23% |
| 75% | 31% |
| 100% | 40% |

After a display brightness change, the helper waits for **3 seconds without another change**, then jumps directly to the new Keypad brightness. Moving the slider again restarts the wait. The display is checked once per second, so the usual response takes 3–4 seconds. The delay is configurable from 0 to 10 seconds.

Changing brightness manually in Logitech pauses synchronization for one hour. Click **Автоматично** (Automatic) to resume immediately.

## Caffeinate settings

The **Кава** (Coffee) card lets you set hours and minutes, then click **Зберегти час** (Save duration). The default is 2 hours; the supported range is 1 minute to 24 hours.

The saved duration applies the next time the companion Keypad button is turned on. Changing it does not alter an active countdown. The button shows the remaining time and progress relative to that session's duration.

## Claude weekly usage

The optional **Claude** tab shows the status of weekly usage checks. Click **Увійти через Claude Code** (Sign in through Claude Code) to open the official Anthropic browser sign-in using the installed `claude auth login --claudeai` command. Complete sign-in yourself and select your Team account; the helper then enables usage checks and checks immediately. This sign-in also changes the account used by Claude Code CLI. Claude Desktop may use a separate login.

The sign-in command does not start a model session. You can cancel an in-progress login in the panel. This is a wrapper around Claude Code's official sign-in, not an independent OAuth client. You can also sign in separately and click **Оновити ліміти** (Refresh limits). Otherwise, checks run every 5 minutes, or every 30 minutes when authorization is missing or expired.

Each usage check reads the current Claude Code authorization afresh. Background checks do not sign in, refresh credentials, invoke a model, or consume model credits. Sign-in runs only after you click its button; Claude Code manages the credential. OAuth URLs and command output are not exposed in the panel or logs.

This integration currently requires a **Claude Code Team account** and is disabled by default. A successful sign-in from the panel enables it. To enable it with an existing login instead, create the following private file outside the repository:

`~/Library/Application Support/KeypadBrightness/ai-settings.json`

```json
{"claudeEnabled": true}
```

Use **Вимкнути перевірки** (Disable checks) to stop credential reads and remove cached usage; Claude Code stays signed in. The same button enables checks again using the existing credential. Expired credentials must be renewed by Claude Code or a new official sign-in; simply launching the CLI is not guaranteed to renew them. HTTP 401, HTTP 403, and local expiry are reported separately.

The usage endpoint is internal to Anthropic and may change. Other subscription plans are not supported by the current implementation. Missing or expired authorization is reported as a status, not as a fabricated quota.

**Keypad button integration:** the AI and Caffeinate buttons are supplied by a separate companion Logitech plugin, which is not included in this repository. This repository contains the background helper and settings panel. It saves the selected duration and sanitized Claude usage data for that plugin to read; installing this helper alone does not create the buttons.

## Install

Requirements: macOS, a connected MX Keypad, running **Logi Options+** background components, **Node.js 20+**, Python 3, and Xcode Command Line Tools with the Swift compiler.

From the repository directory, run:

```sh
python3 install.py
```

The installer builds the app and configures automatic startup for the current user. Running it again updates the app while preserving your saved brightness curve. It also migrates an installation under either previous Ukrainian app name.

| Item | Location |
| --- | --- |
| App | `~/Applications/Keypad Companion.app` |
| Settings and runtime data | `~/Library/Application Support/KeypadBrightness` |
| Startup definition | `~/Library/LaunchAgents/local.keypad-brightness.plist` |

The internal data directory and startup identifier retain their original names for compatibility. You can keep the source anywhere, including iCloud Drive: the installed app runs independently of the repository. Node.js runs the helper in the background; you do not need to open it separately.

## Privacy

Brightness synchronization reads the Mac display brightness and Logitech backlight setting. It does not read keystrokes or passwords.

If explicitly enabled, Claude usage checks read the existing **Claude Code** credential from macOS Keychain and send it only to Anthropic for `GET https://api.anthropic.com/api/oauth/usage`. The credential is not written to files or logs and is not passed to the Logitech plugin. Checks do not invoke a model, purchase credits, or refresh authorization.

Usage checking shares the existing brightness helper. A short-lived child process reads authorization and performs the request, then exits. There is no additional persistent service, startup job, or icon.

Private runtime files contain settings, sanitized numeric usage, quota reset and update times, and check status. They live outside the repository. `.gitignore` additionally excludes runtime data, authorization files, and common secret files. Do not copy credentials or private runtime data into the source directory.

The settings server listens only on `127.0.0.1`. Requests that change settings require the expected origin and a random per-process token. That token is local panel protection, not a saved API credential.

## Limitations

- Reads the main display brightness. Tested on a MacBook Pro's built-in display; external displays have not been verified.
- Waits for reconnection when the display is asleep or Logitech is unavailable.
- Uses Logitech's internal local interface and macOS DisplayServices. Logitech or macOS updates may require changes.
- This is an independent project, not an official Logitech application.

## Uninstall

```sh
python3 uninstall.py
```

Removes the app and startup job. The Keypad keeps its current brightness. Saved settings remain available for a later reinstall.

## Development

Run the tests with:

```sh
node --test test/*.test.mjs
python3 -m unittest discover -s test -p '*_test.py'
```

`helper.mjs` coordinates brightness and the settings panel. `brightness.mjs` validates and interpolates the curve; `logi-client.mjs` talks to Logitech; `display-brightness.py` reads the display brightness. For compatibility with early Companion sign-ins that omitted `USER`, the usage reader considers the current macOS account and the legacy `unknown` account under the exact `Claude Code-credentials` service, selecting the valid Team token with the latest expiry. New sign-ins explicitly pass the macOS username.

The plugin reads private `caffeine-settings.json` (`durationMinutes`, integer minutes) and `claude-quota.json` (`remaining`, integer percentage; `resetsAt`, Unix seconds; `updatedAt`, ISO UTC). Both live in `~/Library/Application Support/KeypadBrightness`; quota is removed after a failed check or disabling checks, and expires after ten minutes or at the reset time. No credential is included.

The Claude modules delegate official sign-in and check and sanitize usage, and `caffeine.mjs` validates the saved duration.

`lib/ws` includes the WebSocket library `ws` and its license. The repository contains source code and installation defaults; current runtime state and logs are excluded from Git.
