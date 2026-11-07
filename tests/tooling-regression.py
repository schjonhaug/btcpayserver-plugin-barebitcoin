"""Offline integration checks. Build ConfigBuilder before running this script."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRATCH = '/private/tmp' if Path('/private/tmp').is_dir() else None

class ToolingTests(unittest.TestCase):
    def test_config_builder_preserves_settings_and_invalid_input(self):
        dll = ROOT / 'ConfigBuilder/bin/Debug/net10.0/ConfigBuilder.dll'
        with tempfile.TemporaryDirectory(prefix='barebitcoin-config-', dir=SCRATCH) as tmp:
            root = Path(tmp)
            cwd = root / 'ConfigBuilder/bin/Debug/net10.0'
            cwd.mkdir(parents=True)
            (root / 'Plugins').mkdir()
            host = root / 'submodules/btcpayserver/BTCPayServer'
            host.mkdir(parents=True)
            (host / 'BTCPayServer.csproj').touch()
            settings = host / 'appsettings.dev.json'
            settings.write_text('{"Unrelated":{"enabled":true},"DEBUG_PLUGINS":"old"}')
            subprocess.run(['dotnet', str(dll)], cwd=cwd, check=True, capture_output=True)
            self.assertEqual(json.loads(settings.read_text()), {'Unrelated': {'enabled': True}, 'DEBUG_PLUGINS': ''})
            settings.write_text('damaged input')
            result = subprocess.run(['dotnet', str(dll)], cwd=cwd, capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(settings.read_text(), 'damaged input')

    def test_release_resume_pushes_atomically_without_rebuilding(self):
        with tempfile.TemporaryDirectory(prefix='barebitcoin-release-', dir=SCRATCH) as tmp:
            root = Path(tmp)
            (root / 'plugin').mkdir()
            (root / 'plugin/BTCPayServer.Plugins.BareBitcoin.csproj').write_text('<Version>2.0.2</Version>')
            shutil.copy(ROOT / 'pluginpacker.sh', root / 'pluginpacker.sh')
            bin_dir = root / 'bin'
            bin_dir.mkdir()
            git = bin_dir / 'git'
            git.write_text('''#!/bin/sh
case "$*" in
  *--show-toplevel*) echo "$TEST_ROOT" ;;
  "rev-parse --abbrev-ref HEAD") echo master ;;
  "status --porcelain") ;;
  "rev-parse v2.0.2^{commit}"|"rev-parse HEAD") echo commit ;;
  "log -1 --format=%s") echo 'Release v2.0.2' ;;
  "push --atomic origin master v2.0.2") echo "$*" > "$TEST_ROOT/push" ;;
  *) echo "Unexpected git call: $*" >&2; exit 1 ;;
esac
''')
            git.chmod(0o755)
            env = dict(os.environ, PATH=str(bin_dir) + os.pathsep + os.environ['PATH'], TEST_ROOT=str(root))
            subprocess.run(['bash', str(root / 'pluginpacker.sh'), '2.0.2', '--resume'], env=env, check=True, capture_output=True)
            self.assertEqual((root / 'push').read_text().strip(), 'push --atomic origin master v2.0.2')

if __name__ == '__main__':
    unittest.main()
