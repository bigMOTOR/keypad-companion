"""Install a built plugin into the existing Companion app, without starting a process."""
from pathlib import Path
import shutil
import sys

root = Path(__file__).resolve().parent
build = root / 'bin' / 'Release'
if not (build / 'bin' / 'MotorControlsPlugin.dll').is_file():
    sys.exit('Build the plugin first: dotnet build plugin/src/MotorControlsPlugin.csproj -c Release -p:InstallPlugin=false')
apps = [Path.home() / 'Applications' / 'Keypad Companion.app', Path('/Applications/Keypad Companion.app')]
app = next((path for path in apps if path.is_dir()), None)
if app is None:
    sys.exit('Install Keypad Companion before installing its plugin.')
destination = app / 'Contents' / 'Resources' / 'Plugin'
destination.mkdir(parents=True, exist_ok=True)
for relative in ['bin/MotorControlsPlugin.dll', 'bin/MotorControlsPlugin.deps.json', 'metadata/LoupedeckPackage.yaml', 'metadata/Icon256x256.png']:
    source = build / relative
    if source.is_file():
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = target.with_suffix(target.suffix + '.new')
        shutil.copy2(source, temporary)
        temporary.replace(target)
link = Path.home() / 'Library' / 'Application Support' / 'Logi' / 'LogiPluginService' / 'Plugins' / 'MotorControlsPlugin.link'
link.parent.mkdir(parents=True, exist_ok=True)
temporary = link.with_suffix('.new')
temporary.write_text(str(destination) + '\n')
temporary.replace(link)
print('Installed into Keypad Companion. Reload MotorControls in Logitech or restart Logitech.')
