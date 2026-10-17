"""Exercise SDK revision validation against actual Git refs."""

import runpy
import subprocess
import tempfile
import unittest
from pathlib import Path


CHECK_CHECKOUT = runpy.run_path(
    str(Path(__file__).resolve().parents[1] / "tools" / "check-sdk-compatibility.py")
)["check_checkout"]


class SdkCompatibilityTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="novalist-sdk-ref-")
        self.addCleanup(directory.cleanup)
        self.repository = Path(directory.name)
        self.git("init")
        self.git("commit", "--allow-empty", "-m", "SDK baseline")
        self.commit = self.git("rev-parse", "HEAD")
        self.git("tag", "v3.6.0")
        self.git("tag", "-a", "annotated", "-m", "SDK release")

    def git(self, *arguments):
        return subprocess.check_output(
            ["git", "-C", str(self.repository), "-c", "user.name=SDK Tests",
             "-c", "user.email=sdk-tests@example.invalid", "-c", "commit.gpgsign=false",
             "-c", "tag.gpgsign=false", *arguments],
            text=True,
            stderr=subprocess.PIPE,
        ).strip()

    def test_accepts_commit_and_release_tags(self):
        for reference in (self.commit, "v3.6.0", "annotated"):
            with self.subTest(reference=reference):
                self.assertEqual(CHECK_CHECKOUT(self.repository, reference), self.commit)

    def test_rejects_a_different_checkout(self):
        self.git("commit", "--allow-empty", "-m", "Later SDK")
        with self.assertRaisesRegex(SystemExit, "pinned SDK revision"):
            CHECK_CHECKOUT(self.repository, "v3.6.0")

    def test_rejects_a_missing_release(self):
        with self.assertRaises(subprocess.CalledProcessError):
            CHECK_CHECKOUT(self.repository, "v99.0.0")


if __name__ == "__main__":
    unittest.main()
