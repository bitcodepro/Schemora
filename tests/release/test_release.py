"""Release boundary tests use only synthetic archives and mocked GitHub calls."""
import importlib.util
import json
from pathlib import Path
import stat
import struct
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/release.py"
spec = importlib.util.spec_from_file_location("schemora_release", SCRIPT)
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
SHA = "a" * 40


class Versions(unittest.TestCase):
    def test_initial_patch_and_markers(self):
        self.assertEqual(release.select_version([])["version"], "0.1.0")
        self.assertEqual(release.select_version(["v0.1.0"])["version"], "0.1.1")
        self.assertEqual(release.select_version(["v0.1.0"], "Feature [minor]")["version"], "0.2.0")
        self.assertEqual(release.select_version(["v0.1.0"], "Breaking #major")["version"], "1.0.0")

    def test_invalid_versions_and_collision(self):
        for value in ("v1.0.0", "1.0", "01.0.0", "1.0.0-01", "1.0.0+sha", "65535.0.0"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                release.version_tuple(value)
        for value in ("0.1.0", "0.0.1", "0.1.0-rc.1"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                release.select_version(["v0.1.0"], explicit=value)

    def test_skip_and_explicit_prerelease(self):
        self.assertEqual(release.select_version([], "Docs [skip release]"), {"skip": "true"})
        self.assertEqual(release.select_version(["v0.1.0"], explicit="0.2.0-rc.1")["prerelease"], "true")


class ArchiveFixture:
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.folder = Path(self.tmp.name)
        self.archive = self.folder / release.archive_name("0.1.0", "win-x64")
        header = bytearray(256)
        header[:2] = b"MZ"
        struct.pack_into("<I", header, 60, 128)
        header[128:134] = b"PE\0\0\x64\x86"
        self.files = {
            "Schemora.Host.exe": bytes(header),
            "README.md": b"Schemora documentation",
            "schemora.json": b'{"port":0}',
            "build-info.json": json.dumps({"version": "0.1.0", "rid": "win-x64", "commit": SHA}).encode(),
            release.NOTICES: b"Microsoft.SqlServer.DacFx react License for: synthetic fixture",
        }

    def write(self, extra=None):
        with zipfile.ZipFile(self.archive, "w") as bundle:
            for name, value in self.files.items():
                bundle.writestr(name, value)
            if extra:
                extra(bundle)
        self.archive.with_name(self.archive.name + ".sha256").write_text(
            f"{release.digest(self.archive)}  {self.archive.name}\n", encoding="ascii")

    def verify(self):
        return release.verify_packages(self.folder, "0.1.0", SHA)


class Packages(ArchiveFixture, unittest.TestCase):
    def test_valid_inventory_with_or_without_existing_project_license(self):
        self.write()
        self.assertEqual(len(self.verify()), 2)
        self.files["LICENSE"] = b"Existing user-selected project license"
        self.write()
        self.verify()

    def test_checksum_and_exact_inventory(self):
        self.write()
        self.archive.write_bytes(self.archive.read_bytes() + b"tampered")
        with self.assertRaises(ValueError):
            self.verify()
        self.write()
        (self.folder / "session.json").write_text("synthetic")
        with self.assertRaises(ValueError):
            self.verify()

    def test_local_validation_archive_cannot_pass_publication_verifier(self):
        self.files["build-info.json"] = json.dumps({"version": "0.1.0", "rid": "win-x64", "commit": SHA, "validationOnly": True}).encode()
        self.write()
        with self.assertRaises(ValueError):
            self.verify()
        release.verify_packages(self.folder, "0.1.0", SHA, validation_only=True)

    def test_path_traversal_residue_and_duplicate(self):
        for name in ("../session.json", "C:/data", "data/catalog.sqlite", "work/session.json", "local.pdb", ".git/config"):
            with self.subTest(name=name):
                self.write(lambda bundle: bundle.writestr(name, "synthetic"))
                with self.assertRaises(ValueError):
                    self.verify()
        self.write(lambda bundle: bundle.writestr("README.md", "duplicate"))
        with self.assertRaises(ValueError):
            self.verify()

    def test_symlink_rejected(self):
        def link(bundle):
            info = zipfile.ZipInfo("LICENSE")
            info.create_system = 3
            info.external_attr = (stat.S_IFLNK | 0o777) << 16
            bundle.writestr(info, "README.md")
        self.write(link)
        with self.assertRaises(ValueError):
            self.verify()

    def test_wrong_binary_manifest_configuration_and_missing_notices(self):
        for name, value in (
            ("Schemora.Host.exe", b"wrong architecture"),
            ("build-info.json", b'{}'),
            ("schemora.json", b'{"port":8088}'),
            (release.NOTICES, b"incomplete"),
        ):
            with self.subTest(name=name), patch.dict(self.files, {name: value}):
                self.write()
                with self.assertRaises(ValueError):
                    self.verify()


class Publication(ArchiveFixture, unittest.TestCase):
    def environment(self):
        return {
            "GITHUB_ACTIONS": "true", "GITHUB_REPOSITORY": "bitcodepro/Schemora",
            "GITHUB_REF": "refs/heads/main", "GITHUB_EVENT_NAME": "push", "GITHUB_SHA": SHA,
            "GITHUB_WORKFLOW_REF": "bitcodepro/Schemora/.github/workflows/ci.yml@refs/heads/main",
        }

    def test_publication_rejects_untrusted_context_before_any_network_call(self):
        env = self.environment()
        for key, value in (("GITHUB_REPOSITORY", "someone/fork"), ("GITHUB_REF", "refs/heads/feature"),
                           ("GITHUB_EVENT_NAME", "pull_request"), ("GITHUB_WORKFLOW_REF", "other.yml"),
                           ("GITHUB_SHA", "short"), ("GITHUB_ACTIONS", "false")):
            with self.subTest(key=key), patch.dict(env, {key: value}), patch.object(release, "gh_api") as api:
                with self.assertRaises(ValueError):
                    release.publish(self.folder, "0.1.0", env)
                api.assert_not_called()

    def test_main_moved_or_existing_release_produces_no_writes(self):
        self.write()
        for moved in (True, False):
            calls = []
            def api(endpoint, method="GET", data=None):
                calls.append((endpoint, method))
                if endpoint == "git/ref/heads/main":
                    return {"object": {"sha": "b" * 40 if moved else SHA}}
                if endpoint.startswith("git/matching-refs"):
                    return []
                return [{"tag_name": "v0.1.0"}]
            with self.subTest(moved=moved), patch.object(release, "gh_api", api), patch.object(release, "run") as run:
                with self.assertRaises(ValueError):
                    release.publish(self.folder, "0.1.0", self.environment())
                self.assertTrue(all(method == "GET" for _, method in calls))
                run.assert_not_called()

    def test_incomplete_draft_never_becomes_public(self):
        self.write()
        def api(endpoint, method="GET", data=None):
            if endpoint == "git/ref/heads/main":
                return {"object": {"sha": SHA}}
            if endpoint.startswith("git/matching-refs"):
                return []
            return {}
        with patch.object(release, "gh_api", api), patch.object(release, "releases", return_value=[]), \
             patch.object(release, "find_draft", return_value={"assets": []}), patch.object(release, "run") as run:
            with self.assertRaises(ValueError):
                release.publish(self.folder, "0.1.0", self.environment())
            self.assertEqual(run.call_count, 1)
            self.assertIn("--draft", run.call_args.args[0])
            self.assertNotIn("edit", run.call_args.args[0])


if __name__ == "__main__":
    unittest.main()
