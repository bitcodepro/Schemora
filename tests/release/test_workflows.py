"""Check the publication gates, supported platform inventory and token boundaries."""
from pathlib import Path
import json
import re
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[2]


class Workflows(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.ci = yaml.load((ROOT / ".github/workflows/ci.yml").read_text(), Loader=yaml.BaseLoader)
        cls.packages = yaml.load((ROOT / ".github/workflows/package.yml").read_text(), Loader=yaml.BaseLoader)

    def test_schedule_and_optional_sql_regression_are_retained(self):
        self.assertEqual(self.ci["on"]["schedule"], [{"cron": "17 3 * * 1"}])
        sql = self.ci["jobs"]["generated-sql-on-sql-server"]
        self.assertIn("workflow_dispatch", sql["if"])
        self.assertIn("schedule", sql["if"])
        self.assertTrue(any(step.get("run") == "./scripts/Test-GeneratedSql.ps1 -Run" for step in sql["steps"]))

    def test_version_gate_honors_failed_and_skipped_sql_job(self):
        job = self.ci["jobs"]["version"]
        self.assertIn("generated-sql-on-sql-server", job["needs"])
        condition = job["if"]
        self.assertIn("always()", condition)
        for event, sql, expected in (("push", "skipped", True), ("pull_request", "skipped", True),
                                     ("workflow_dispatch", "success", True), ("workflow_dispatch", "failure", False),
                                     ("workflow_dispatch", "cancelled", False), ("schedule", "success", False)):
            statuses = {"release-tooling": "success", "build-and-test": "success", "generated-sql-on-sql-server": sql}
            expression = re.sub(r"needs\.([a-z-]+)\.result", lambda m: repr(statuses[m[1]]), condition)
            expression = expression.replace("github.event_name", repr(event)).replace("always()", "True")
            expression = expression.replace("&&", " and ").replace("||", " or ")
            with self.subTest(event=event, sql=sql):
                self.assertEqual(eval(expression, {"__builtins__": {}}, {}), expected)

    def test_only_release_job_can_write_and_all_checkouts_drop_credentials(self):
        for workflow in (self.ci, self.packages):
            self.assertEqual(workflow["permissions"], {"contents": "read"})
            for name, job in workflow["jobs"].items():
                if "permissions" in job:
                    self.assertEqual(name, "release")
                    self.assertEqual(job["permissions"], {"contents": "write"})
                for step in job.get("steps", []):
                    if "uses" in step:
                        self.assertRegex(step["uses"], r"^actions/[^@]+@[0-9a-f]{40}$")
                    if step.get("uses", "").startswith("actions/checkout@"):
                        self.assertEqual(step["with"]["persist-credentials"], "false")
                    if step.get("uses", "").startswith("actions/upload-artifact@"):
                        self.assertEqual(step["with"]["retention-days"], "7")

    def test_final_downloaded_package_runtime_is_a_release_dependency(self):
        jobs = self.ci["jobs"]
        self.assertIn("runtime-artifact", jobs["release"]["needs"])
        self.assertIn("verify-packages", jobs["runtime-artifact"]["needs"])
        runtime = "\n".join(step.get("run", "") for step in jobs["runtime-artifact"]["steps"])
        self.assertIn("Expand-Archive", runtime)
        self.assertIn("Schemora-$env:VERSION-win-x64.zip", runtime)
        self.assertIn("work/release-runtime/Schemora.Host.exe", runtime)
        self.assertIn("-AllowDevelopmentTools", runtime)
        self.assertIn("$runtime.build", runtime)
        self.assertIn("$runtime.version", runtime)
        self.assertIn("github.repository == 'bitcodepro/Schemora'", jobs["release"]["if"])
        self.assertIn("github.ref == 'refs/heads/main'", jobs["release"]["if"])

    def test_only_supported_native_windows_package_and_escaped_restore_properties(self):
        matrix = self.packages["jobs"]["package"]["strategy"]["matrix"]["include"]
        self.assertEqual(matrix, [{"rid": "win-x64", "os": "windows-2025"}])
        commands = "\n".join(step.get("run", "") for step in self.ci["jobs"]["build-and-test"]["steps"])
        self.assertIn("NU1901%3BNU1902%3BNU1903%3BNU1904", commands)
        self.assertNotIn("WarningsAsErrors=NU1901,", commands)

    def test_ci_installer_version_agrees_with_project_sdk(self):
        sdk = json.loads((ROOT / "global.json").read_text())["sdk"]
        self.assertEqual(sdk["version"], "10.0.402")
        self.assertEqual(sdk["rollForward"], "latestPatch")
        for workflow in (self.ci, self.packages):
            for job in workflow["jobs"].values():
                for step in job.get("steps", []):
                    if step.get("uses", "").startswith("actions/setup-dotnet@"):
                        self.assertEqual(step["with"].get("dotnet-version"), sdk["version"])
                        self.assertNotIn("global-json-file", step["with"])

    def test_downstream_gates_run_after_optional_skip_but_never_after_required_failure(self):
        def evaluate(condition, statuses, event, cancelled=False, skip="false"):
            # Model the observed GitHub default: a skipped optional ancestor makes implicit success() false.
            if not re.search(r"(?:always|cancelled|failure|success)\(\)", condition):
                return False
            expression = re.sub(r"needs\.([a-z-]+)\.result", lambda m: repr(statuses[m[1]]), condition)
            expression = expression.replace("needs.version.outputs.skip", repr(skip))
            expression = expression.replace("github.event_name", repr(event))
            expression = expression.replace("github.repository", repr("bitcodepro/Schemora"))
            expression = expression.replace("github.ref", repr("refs/heads/main"))
            expression = expression.replace("always()", "True").replace("cancelled()", repr(cancelled))
            expression = expression.replace("&&", " and ").replace("||", " or ")
            expression = re.sub(r"!(?!=)", " not ", expression)
            return eval(" ".join(expression.split()), {"__builtins__": {}}, {})
        for name in ("packages", "verify-packages", "runtime-artifact", "release"):
            job = self.ci["jobs"][name]
            needs = job["needs"] if isinstance(job["needs"], list) else [job["needs"]]
            statuses = {need: "success" for need in needs}
            event = "push" if name == "release" else "pull_request"
            with self.subTest(job=name):
                self.assertTrue(evaluate(job["if"], statuses, event))
                self.assertFalse(evaluate(job["if"], statuses, event, cancelled=True))
                for dependency in needs:
                    for outcome in ("failure", "cancelled", "skipped"):
                        failed = {**statuses, dependency: outcome}
                        self.assertFalse(evaluate(job["if"], failed, event), (name, dependency, outcome))
                if name in ("packages", "release"):
                    self.assertFalse(evaluate(job["if"], statuses, event, skip="true"))
                if name == "packages":
                    self.assertFalse(evaluate(job["if"], statuses, "schedule"))
                if name == "release":
                    self.assertFalse(evaluate(job["if"], statuses, "pull_request"))


if __name__ == "__main__":
    unittest.main()
