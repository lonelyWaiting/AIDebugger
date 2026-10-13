"""Installer rollback must attempt every owned destination after a failure."""
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from tools import install_aidebugger as installer


class InstallerRollbackTests(unittest.TestCase):
    def test_rollback_failure_does_not_skip_remaining_files_or_hide_install_error(self):
        for failure_kind in ("restore", "unlink", "read"):
            with self.subTest(failure_kind=failure_kind), tempfile.TemporaryDirectory() as temporary:
                home = Path(temporary)
                runtime = home / ".ai-debugger/kernel"
                first, second = runtime / "first.py", runtime / "second.py"
                runtime.mkdir(parents=True)
                first.write_bytes(b"old-first")
                if failure_kind != "unlink":
                    second.write_bytes(b"old-second")
                manifest = runtime / "install-manifest.json"
                manifest.write_text(json.dumps({"files": {
                    str(first): installer.digest(b"old-first"),
                    str(second): installer.digest(b"old-second"),
                }}), encoding="utf-8")
                original_manifest = manifest.read_bytes()
                write, unlink, read = installer.atomic_write, Path.unlink, Path.read_bytes
                installing = True
                primary_error = OSError("injected installation failure")

                def injected_write(path, content):
                    nonlocal installing
                    if path == manifest:
                        installing = False
                        raise primary_error
                    if not installing and path == second and failure_kind == "restore":
                        raise PermissionError("injected restore failure")
                    return write(path, content)

                def injected_unlink(path, *args, **kwargs):
                    if not installing and path == second and failure_kind == "unlink":
                        raise PermissionError("injected unlink failure")
                    return unlink(path, *args, **kwargs)

                def injected_read(path):
                    if not installing and path == second and failure_kind == "read":
                        raise PermissionError("injected read failure")
                    return read(path)

                with patch.dict(os.environ, {"CODEX_HOME": ""}), \
                     patch.object(installer, "payload_files", return_value={
                         "first.py": b"new-first", "second.py": b"new-second"}), \
                     patch.object(installer, "atomic_write", side_effect=injected_write), \
                     patch.object(Path, "unlink", injected_unlink), \
                     patch.object(Path, "read_bytes", injected_read):
                    with self.assertRaises(OSError) as caught:
                        installer.install(home, clients=("codex",), backends=("kernel",))
                self.assertEqual(b"old-first", first.read_bytes())
                self.assertIs(primary_error, caught.exception)
                self.assertEqual(original_manifest, manifest.read_bytes())
                self.assertFalse((home / ".codex/config.toml").exists())
                self.assertTrue(any(str(second) in note and "rollback" in note.lower()
                                    for note in caught.exception.__notes__))


if __name__ == "__main__":
    unittest.main()
