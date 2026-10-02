"""Build and install Keypad Brightness for the current macOS user."""
from pathlib import Path
import json
import os
import plistlib
import shutil
import subprocess
import tempfile
import time

source = Path(__file__).resolve().parent
home = Path.home()
node = shutil.which('node')
if not node:
    raise SystemExit('Node.js is required. Install Node.js, then run this installer again.')
app = home / 'Applications/Яскравість Keypad.app'
runtime = home / 'Library/Application Support/KeypadBrightness'
agent = home / 'Library/LaunchAgents/local.keypad-brightness.plist'
service = f'gui/{os.getuid()}/local.keypad-brightness'
subprocess.run(['/bin/launchctl', 'bootout', service], capture_output=True)
runtime.mkdir(parents=True, exist_ok=True)
app.parent.mkdir(parents=True, exist_ok=True)
(app / 'Contents').mkdir(parents=True, exist_ok=True)
shutil.copy2(source / 'app/Info.plist', app / 'Contents/Info.plist')
resources = app / 'Contents/Resources'
resources.mkdir(parents=True, exist_ok=True)
for name in ['helper.mjs', 'brightness.mjs', 'logi-client.mjs', 'display-brightness.py', 'settings.html']:
    shutil.copy2(source / name, resources / name)
shutil.copytree(source / 'lib', resources / 'lib', dirs_exist_ok=True)
executable = app / 'Contents/MacOS/KeypadBrightnessSettings'
executable.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='keypad-build-') as build:
    subprocess.run(['/usr/bin/swiftc', '-O', '-module-cache-path', build,
                    str(source / 'launcher.swift'), '-o', str(executable)], check=True)
config_file = runtime / 'config.json'
if not config_file.exists() or 'points' not in json.loads(config_file.read_text()):
    shutil.copy2(source / 'config.json', config_file)
definition = {
    'Label': 'local.keypad-brightness',
    'ProgramArguments': [node, str(resources / 'helper.mjs')],
    'WorkingDirectory': str(runtime),
    'EnvironmentVariables': {'KEYPAD_BRIGHTNESS_DATA_DIR': str(runtime)},
    'RunAtLoad': True, 'KeepAlive': True, 'ThrottleInterval': 10,
    'ProcessType': 'Background',
    'StandardOutPath': str(runtime / 'helper.log'),
    'StandardErrorPath': str(runtime / 'helper-error.log'),
}
agent.parent.mkdir(parents=True, exist_ok=True)
agent.write_bytes(plistlib.dumps(definition))
for attempt in range(3):
    result = subprocess.run(['/bin/launchctl', 'bootstrap', f'gui/{os.getuid()}', str(agent)], capture_output=True)
    if result.returncode == 0:
        break
    time.sleep(1)
else:
    raise RuntimeError(result.stderr.decode())
subprocess.run(['/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister', '-f', str(app)], check=True)
print('Installed. Open Яскравість Keypad or http://127.0.0.1:57973/')
