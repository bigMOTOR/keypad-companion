"""Remove the installed application and login helper; keep settings for reinstall."""
from pathlib import Path
import os
import shutil
import subprocess
home = Path.home()
subprocess.run(['/bin/launchctl', 'bootout', f'gui/{os.getuid()}/local.keypad-brightness'], capture_output=True)
(home / 'Library/LaunchAgents/local.keypad-brightness.plist').unlink(missing_ok=True)
for name in ['Keypad Companion.app', 'Помічник Keypad.app', 'Яскравість Keypad.app']:
    app = home / 'Applications' / name
    if app.exists():
        shutil.rmtree(app)
print('Removed. Keypad keeps its current brightness. Saved settings remain in Library/Application Support/KeypadBrightness.')
