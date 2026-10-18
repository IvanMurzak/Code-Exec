"""Golden cases (fixtures/golden/python.json) plus Python-specific behaviour. Run: python -m unittest discover python/tests"""

import base64
import json
import os
import shutil
import sys
import tempfile
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "python"))

import code_exec  # noqa: E402
from code_exec import runner  # noqa: E402

with open(os.path.join(ROOT, "fixtures", "golden", "python.json"), encoding="utf-8") as handle:
    GOLDEN = json.load(handle)
INCLUDE_ROOT = os.path.join(ROOT, GOLDEN["includeRoot"])


class GoldenTests(unittest.TestCase):
    def test_golden(self):
        for case in GOLDEN["cases"]:
            with self.subTest(case["name"]):
                result = code_exec.run(case["code"], include=case.get("include", ()), include_root=INCLUDE_ROOT)
                expect = case["expect"]
                describe = "%s -> %s" % (case["name"], json.dumps(result))
                self.assertEqual(expect["success"], result["success"], describe)
                if "value" in expect:
                    self.assertEqual(expect["value"], result["value"], describe)
                if "errorKind" in expect:
                    self.assertEqual(expect["errorKind"], result["errorKind"], describe)
                if "errorContains" in expect:
                    self.assertIn(expect["errorContains"], result["error"] or "", describe)
                if "diagnostic" in expect:
                    wanted = expect["diagnostic"]
                    self.assertTrue(any(d["file"] == wanted["file"] and d["line"] == wanted["line"] for d in result["diagnostics"]), describe)
                for text in expect.get("logsContain", ()):
                    self.assertTrue(any(text in entry["message"] for entry in result["logs"]), describe)


class BehaviourTests(unittest.TestCase):
    def test_run_b64_round_trip(self):
        payload = base64.b64encode(json.dumps({"code": "x = 2\nx ** 5"}).encode("utf-8")).decode("ascii")
        self.assertEqual("32", json.loads(code_exec.run_b64(payload))["value"])

    def test_run_json_rejects_garbage(self):
        self.assertEqual("InvalidRequest", json.loads(code_exec.run_json("not json"))["errorKind"])
        self.assertEqual("InvalidRequest", json.loads(code_exec.run_json("[1]"))["errorKind"])

    def test_host_imports_are_bound(self):
        result = json.loads(code_exec.run_json(json.dumps({"code": "os.path.join('a', 'b')", "imports": ["os.path"]})))
        self.assertTrue(result["success"], result)
        self.assertEqual(os.path.join("a", "b"), result["value"])

    def test_missing_host_import_is_a_runtime_error(self):
        result = code_exec.run("1", imports=["module_that_does_not_exist_xyz"])
        self.assertEqual("Runtime", result["errorKind"])

    def test_imports_must_be_a_list(self):
        self.assertEqual("InvalidRequest", code_exec.run("1", imports="os")["errorKind"])

    def test_non_utf8_include_is_an_invalid_request(self):
        folder = tempfile.mkdtemp()
        try:
            path = os.path.join(folder, "latin1.py")
            with open(path, "wb") as handle:
                handle.write(b"\xff\xfe = 1\n")
            result = code_exec.run("1", include=[path])
            self.assertEqual("InvalidRequest", result["errorKind"])
            self.assertIn("latin1.py", result["error"])
        finally:
            shutil.rmtree(folder)

    def test_null_byte_is_a_compile_error(self):
        self.assertEqual("Compile", code_exec.run("x = 1\x00")["errorKind"])

    def test_system_exit_does_not_escape(self):
        result = code_exec.run("raise SystemExit(3)")
        self.assertEqual("Runtime", result["errorKind"])

    def test_runs_are_stateless(self):
        code_exec.run("leaked = 1")
        self.assertEqual("Runtime", code_exec.run("leaked")["errorKind"])

    def test_sys_path_is_restored(self):
        before = list(sys.path)
        code_exec.run("import sys\nsys.path.append('somewhere')", include=["geo"], include_root=INCLUDE_ROOT)
        self.assertEqual(before, sys.path)

    def test_edited_include_module_is_reloaded(self):
        folder = tempfile.mkdtemp()
        try:
            path = os.path.join(folder, "helper_mod_reload.py")
            with open(path, "w") as handle:
                handle.write("VALUE = 1\n")
            first = code_exec.run("import helper_mod_reload\nhelper_mod_reload.VALUE", include=[folder])
            with open(path, "w") as handle:
                handle.write("VALUE = 2\n")
            second = code_exec.run("import helper_mod_reload\nhelper_mod_reload.VALUE", include=[folder])
            self.assertEqual(("1", "2"), (first["value"], second["value"]))
        finally:
            shutil.rmtree(folder)

    def test_value_converter_hook(self):
        runner.set_value_converter(lambda v: "custom" if isinstance(v, complex) else NotImplemented)
        try:
            self.assertEqual("custom", code_exec.run("1j")["value"])
            self.assertEqual("5", code_exec.run("5")["value"])
        finally:
            runner.set_value_converter(None)

    def test_stderr_goes_to_error_logs(self):
        result = code_exec.run("import sys\nsys.stderr.write('bad\\n')\nprint('partial', end='')")
        self.assertIn({"level": "error", "message": "bad"}, result["logs"])
        self.assertIn({"level": "info", "message": "partial"}, result["logs"])


if __name__ == "__main__":
    unittest.main()
