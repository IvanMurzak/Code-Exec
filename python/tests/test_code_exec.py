"""Golden cases (fixtures/golden/python.json) plus Python-specific behaviour. Run: python -m unittest discover python/tests"""

import base64
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
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
                # Through the JSON entry point, as hosts call it: every request field of the case is passed as is.
                request = {key: value for key, value in case.items() if key not in ("name", "expect")}
                request["includeRoot"] = INCLUDE_ROOT
                result = json.loads(code_exec.run_json(json.dumps(request)))
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
                if "logs" in expect:
                    self.assertEqual(expect["logs"], [entry["message"] for entry in result["logs"]], describe)
                if "truncated" in expect:
                    self.assertEqual(expect["truncated"], result["truncated"], describe)
                if "valueEndsWith" in expect:
                    self.assertTrue((result["value"] or "").endswith(expect["valueEndsWith"]), describe[:500])
                if "valueLength" in expect:
                    self.assertEqual(expect["valueLength"], len(result["value"] or ""), describe[:500])


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
            result = code_exec.run("1", include=["latin1.py"], include_root=folder)
            self.assertEqual("InvalidRequest", result["errorKind"])
            self.assertIn("latin1.py", result["error"])
            self.assertIn("not readable as Python source", result["error"])
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
            first = code_exec.run("import helper_mod_reload\nhelper_mod_reload.VALUE", include=[folder], include_outside_root=True)
            with open(path, "w") as handle:
                handle.write("VALUE = 2\n")
            second = code_exec.run("import helper_mod_reload\nhelper_mod_reload.VALUE", include=[folder], include_outside_root=True)
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


class _Unprintable:
    def __str__(self):
        raise RuntimeError("no str")

    __repr__ = __str__


class RobustnessTests(unittest.TestCase):
    """A snippet must never take the host down or flood it (spec sections 4 and 6)."""

    def setUp(self):
        self.folder = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.folder)

    def write(self, name, data):
        path = os.path.join(self.folder, name)
        with open(path, "wb") as handle:
            handle.write(data)
        return path

    def test_every_base_exception_is_a_runtime_error(self):
        for code in ("raise KeyboardInterrupt", "raise GeneratorExit", "class B(BaseException): pass\nraise B()",
                     "import sys\nsys.exit(0)", "exit()"):
            with self.subTest(code):
                result = code_exec.run(code)
                self.assertEqual("Runtime", result["errorKind"], result)

    def test_keyboard_interrupt_keeps_the_snippet_frames(self):
        error = code_exec.run("def stop():\n    raise KeyboardInterrupt('now')\nstop()")["error"]
        self.assertTrue(error.startswith("KeyboardInterrupt: now"), error)
        self.assertIn('snippet.py", line 2, in stop', error)
        self.assertNotIn("runner.py", error)

    def test_unprintable_exception_in_a_base_exception_subclass(self):
        result = code_exec.run("class E(KeyboardInterrupt):\n    def __str__(self):\n        raise KeyboardInterrupt\nraise E()")
        self.assertEqual("Runtime", result["errorKind"])
        self.assertIn("E: <unprintable message>", result["error"])

    def test_value_whose_str_and_repr_raise(self):
        result = code_exec.run("class X:\n    def __repr__(self):\n        raise RuntimeError('no repr')\nX()")
        self.assertEqual("Runtime", result["errorKind"])
        self.assertIn("RuntimeError: no repr", result["error"])

    def test_implicit_context_is_reported(self):
        error = code_exec.run("try:\n    1 / 0\nexcept ZeroDivisionError:\n    raise ValueError('then')")["error"]
        self.assertTrue(error.startswith("ValueError: then"), error)
        self.assertIn("Raised while handling: ZeroDivisionError: division by zero", error)
        suppressed = code_exec.run("try:\n    1 / 0\nexcept ZeroDivisionError:\n    raise ValueError('then') from None")["error"]
        self.assertNotIn("ZeroDivisionError", suppressed)

    def test_mutual_recursion_traceback_is_bounded(self):
        result = code_exec.run("def a(n):\n    return b(n)\ndef b(n):\n    return a(n)\na(0)")
        self.assertEqual("Runtime", result["errorKind"])
        self.assertIn("frame(s) left out", result["error"])
        self.assertLess(len(result["error"]), 3000)

    def test_runaway_print_loop_keeps_first_and_last_lines(self):
        result = code_exec.run("for i in range(20000):\n    print('line', i)")
        messages = [entry["message"] for entry in result["logs"]]
        self.assertEqual("line 0", messages[0])
        self.assertEqual("line 19999", messages[-1])
        self.assertTrue(any("log line(s) left out" in m for m in messages))
        self.assertLessEqual(sum(len(m) + runner._LOG_ENTRY_CHARS for m in messages if "left out" not in m), runner.MAX_LOG_CHARS)
        self.assertEqual(["logs"], result["truncated"])

    def test_log_budget_matches_the_batch_rule(self):
        # Spec section 6 states the rule over the whole list: the streaming budget must give the same entries.
        import random
        rng = random.Random(7)
        for _ in range(300):
            sizes = [rng.randint(0, 40) for _ in range(rng.randint(0, 30))]
            budget = rng.randint(1, 300)
            logs = runner._Logs(budget, 0)
            for i, size in enumerate(sizes):
                logs.add("info", "%d" % i + "x" * size)
            messages = [e["message"] for e in logs.entries() if e["level"] == "info"]
            costs = [len(m) + runner._LOG_ENTRY_CHARS for m in ("%d" % i + "x" * size for i, size in enumerate(sizes))]
            expected = list(range(len(sizes)))
            if sum(costs) > budget:
                head, used = 0, 0
                while head < len(costs) and used + costs[head] <= budget * 3 // 4:
                    used += costs[head]
                    head += 1
                tail = len(costs)
                while tail > head and used + costs[tail - 1] <= budget:
                    used += costs[tail - 1]
                    tail -= 1
                expected = list(range(head)) + list(range(tail, len(costs)))
            self.assertEqual(expected, [int(m.rstrip("x")) for m in messages], (sizes, budget))

    def test_huge_value_and_error_are_clipped_by_default(self):
        self.assertLess(len(code_exec.run("'x' * 5000000")["value"]), 20200)
        self.assertLess(len(code_exec.run("raise ValueError('y' * 1000000)")["error"]), 20200)

    def test_limits_come_through_the_json_request(self):
        response = json.loads(code_exec.run_json(json.dumps({"code": "'abcdef'", "maxValueChars": 3})))
        self.assertEqual("abc... [truncated: 3 of 6 characters shown; return a slice or a summary]", response["value"])
        for bad in (-1, "10", 1.5, True):
            with self.subTest(bad):
                response = json.loads(code_exec.run_json(json.dumps({"code": "1", "maxLogChars": bad})))
                self.assertEqual("InvalidRequest", response["errorKind"])

    def test_malformed_json_fields_are_invalid_requests(self):
        for request in ({"code": "1", "includeRoot": 5}, {"code": "1", "imports": [5]}, {"code": "1", "imports": 5},
                        {"code": "1", "include": [1]}, {"code": "1", "include": 5}):
            with self.subTest(request):
                self.assertEqual("InvalidRequest", json.loads(code_exec.run_json(json.dumps(request)))["errorKind"])

    def test_odd_converter_value_still_serializes(self):
        runner.set_value_converter(lambda v: object())
        try:
            response = json.loads(code_exec.run_json(json.dumps({"code": "1"})))
            self.assertTrue(response["success"], response)
            self.assertIn("object", response["value"])
        finally:
            runner.set_value_converter(None)

    def test_working_directory_is_restored(self):
        before = os.getcwd()
        code_exec.run("import os\nos.chdir(%r)" % self.folder)
        self.assertEqual(before, os.getcwd())

    def test_include_with_bom_or_coding_declaration(self):
        self.write("bom.py", b"\xef\xbb\xbfA = 1\n")
        self.write("latin.py", b"# -*- coding: latin-1 -*-\nB = '\xe9'\n")
        result = code_exec.run("A, B", include=["bom.py", "latin.py"], include_root=self.folder)
        self.assertTrue(result["success"], result)
        self.assertEqual('[1, "\\u00e9"]', result["value"])

    def test_include_must_stay_inside_the_root(self):
        inside = os.path.join(self.folder, "inside")
        os.mkdir(inside)
        self.write("outside.py", b"X = 1\n")
        for entry in ("../outside.py", os.path.join(self.folder, "outside.py")):
            with self.subTest(entry):
                result = code_exec.run("X", include=[entry], include_root=inside)
                self.assertEqual("InvalidRequest", result["errorKind"])
                self.assertIn("outside the include root", result["error"])
        allowed = json.loads(code_exec.run_json(json.dumps(
            {"code": "X", "include": ["../outside.py"], "includeRoot": inside, "includeOutsideRoot": True})))
        self.assertEqual("1", allowed["value"], allowed)
        # An absolute path inside the root and the root itself are fine.
        self.write(os.path.join("inside", "h.py"), b"Y = 2\n")
        self.assertEqual("2", code_exec.run("Y", include=[os.path.join(inside, "h.py")], include_root=inside)["value"])
        self.assertTrue(code_exec.run("1", include=["."], include_root=inside)["success"])

    def test_broken_interpreter_state_is_restored_and_reported(self):
        before_path, before_limit = list(sys.path), sys.getrecursionlimit()
        for code in ("import sys\ndel sys.path\n1", "import sys\nsys.path = None\n1",
                     "import sys, inspect\nsys.setrecursionlimit(len(inspect.stack()) + 8)\nraise ValueError('low')"):
            with self.subTest(code):
                result = code_exec.run(code)  # must answer, not raise
                self.assertIn(result["errorKind"], ("None", "Runtime"), result)
                self.assertEqual(before_path, sys.path)
                self.assertEqual(before_limit, sys.getrecursionlimit())

    def test_non_text_coding_declaration_is_an_invalid_request(self):
        self.write("rot.py", b"# coding: rot13\nK = 1\n")
        self.assertEqual("InvalidRequest", code_exec.run("K", include=["rot.py"], include_root=self.folder)["errorKind"])

    def test_json_entry_points_never_raise(self):
        self.assertEqual("InvalidRequest", json.loads(code_exec.run_json("[" * 100000))["errorKind"])
        self.assertEqual("InvalidRequest", json.loads(code_exec.run_b64(None))["errorKind"])
        for converted in ({(1, 2): 3}, None):
            runner.set_value_converter(lambda v, converted=converted: converted if converted is not None else _Unprintable())
            try:
                response = json.loads(code_exec.run_json(json.dumps({"code": "1"})))
                self.assertTrue(response["success"], response)
            finally:
                runner.set_value_converter(None)

    def test_falsy_wrong_json_types_are_invalid_requests(self):
        for request in ({"include": ""}, {"include": {}}, {"include": False}, {"imports": {}}, {"imports": 0},
                        {"includeOutsideRoot": "true"}, {"includeRoot": 0}):
            request["code"] = "1"
            with self.subTest(request):
                self.assertEqual("InvalidRequest", json.loads(code_exec.run_json(json.dumps(request)))["errorKind"])

    def test_host_modules_under_an_include_folder_stay_loaded(self):
        self.write("hostmod_ce.py", b"STATE = []\n")
        sys.path.insert(0, self.folder)
        try:
            import hostmod_ce
            code_exec.run("1", include=["."], include_root=self.folder)
            self.assertIs(hostmod_ce, sys.modules.get("hostmod_ce"))
        finally:
            sys.path.remove(self.folder)
            sys.modules.pop("hostmod_ce", None)

    def test_traceback_shows_the_agents_own_lines(self):
        cwd = os.getcwd()
        self.write("snippet.py", b"UNRELATED_LINE_1\nUNRELATED_LINE_2\n")
        os.chdir(self.folder)  # a stray snippet.py where linecache would look
        try:
            error = code_exec.run("import sys\nsys.tracebacklimit = 0\nraise ValueError('mine')")["error"]
        finally:
            os.chdir(cwd)
        self.assertIn('snippet.py", line 3', error)
        self.assertIn("raise ValueError('mine')", error)
        self.assertNotIn("UNRELATED", error)

    @unittest.skipUnless(os.name == "nt", "case-insensitive paths")
    def test_same_include_in_another_case_runs_once(self):
        self.write("count.py", b"import builtins\nbuiltins._ce_count = getattr(builtins, '_ce_count', 0) + 1\n")
        try:
            code_exec.run("1", include=["count.py", "COUNT.py"], include_root=self.folder)
            import builtins
            self.assertEqual(1, builtins._ce_count)
        finally:
            import builtins
            builtins.__dict__.pop("_ce_count", None)

    def test_include_file_must_be_python(self):
        self.write("data.json", b"{}")
        result = code_exec.run("1", include=["data.json"], include_root=self.folder)
        self.assertEqual("InvalidRequest", result["errorKind"])
        self.assertIn("not a .py file", result["error"])


@unittest.skipIf(runner._SET_ASYNC_EXC is None, "no ctypes: the time budget is not enforced")
class TimeBudgetTests(unittest.TestCase):
    """Spec section 4: a snippet still running when its time budget is spent is stopped with a Timeout error."""

    def run_timed(self, code, **kwargs):
        started = time.perf_counter()
        result = code_exec.run(code, **kwargs)
        return result, time.perf_counter() - started

    def test_endless_loops_time_out_within_the_budget(self):
        codes = ["while True: pass",
                 "def spin():\n    n = 0\n    while True:\n        n += 1\nspin()",
                 "import itertools\nfor _ in itertools.count():\n    pass"]
        if sys.version_info >= (3, 11):  # 3.10 never checks for pending exceptions (nor drops the GIL) in this loop
            codes.append("while True:\n    try:\n        pass\n    except Exception:\n        pass")
        for code in codes:
            with self.subTest(code):
                result, elapsed = self.run_timed(code, timeout_ms=300)
                self.assertEqual("Timeout", result["errorKind"], result)
                self.assertTrue(result["error"].startswith(
                    "SnippetTimeout: The snippet ran longer than its time budget of 300 ms (timeoutMs)"), result["error"])
                self.assertIn('snippet.py", line', result["error"])
                self.assertLess(elapsed, 5)

    def test_no_stop_reaches_the_host_afterwards(self):
        self.assertEqual("Timeout", code_exec.run("while True: pass", timeout_ms=100)["errorKind"])
        time.sleep(0.4)  # a stray stop would be raised here
        result = code_exec.run("import time\ntime.sleep(0.3)\n1", timeout_ms=5000)
        self.assertTrue(result["success"], result)
        self.assertEqual("1", result["value"])
        deadline = time.monotonic() + 2
        while any(t.name == "code_exec watchdog" for t in threading.enumerate()) and time.monotonic() < deadline:
            time.sleep(0.01)
        self.assertFalse(any(t.name == "code_exec watchdog" for t in threading.enumerate()))

    def test_finished_snippet_is_not_stopped(self):
        for _ in range(20):  # the stop is disarmed when the snippet ends, whatever the timing
            result = code_exec.run("sum(range(1000))", timeout_ms=1)
            self.assertIn(result["errorKind"], ("None", "Timeout"), result)
        time.sleep(0.2)

    def test_host_default_budget(self):
        self.assertEqual(120000, runner.TIMEOUT_MS)
        code_exec.set_default_timeout_ms(200)
        try:
            result, elapsed = self.run_timed("while True: pass")
            self.assertEqual("Timeout", result["errorKind"])
            self.assertIn("200 ms", result["error"])
        finally:
            code_exec.set_default_timeout_ms(runner.TIMEOUT_MS)
        with self.assertRaises(ValueError):
            code_exec.set_default_timeout_ms(-1)

    def test_json_timeout_field(self):
        result = json.loads(code_exec.run_json(json.dumps({"code": "while True: pass", "timeoutMs": 200})))
        self.assertEqual("Timeout", result["errorKind"])
        for bad in ("200", -1, True, 1.5):
            with self.subTest(bad):
                result = json.loads(code_exec.run_json(json.dumps({"code": "1", "timeoutMs": bad})))
                self.assertEqual("InvalidRequest", result["errorKind"])

    def test_lowered_recursion_limit_still_succeeds(self):
        code = "import sys, inspect\nsys.setrecursionlimit(len(inspect.stack()) + 20)\n7"
        result = code_exec.run(code, timeout_ms=5000)
        self.assertTrue(result["success"], result)
        self.assertEqual("7", result["value"])

    def test_nested_run_reports_its_own_budget(self):
        code = "import code_exec\ncode_exec.run('while True: pass', timeout_ms=200)['error'].splitlines()[0]"
        result = code_exec.run(code, timeout_ms=5000)
        self.assertTrue(result["success"], result)
        self.assertIn("200 ms", result["value"])

    def test_deep_recursion_is_a_runtime_error(self):
        result = code_exec.run("def f(n):\n    return f(n + 1)\nf(0)")
        self.assertEqual("Runtime", result["errorKind"])
        self.assertTrue(result["error"].startswith("RecursionError"), result["error"])
        self.assertIn("frame(s) left out", result["error"])


CAPPED = runner._CAPPED  # Python 3.12+ raise RecursionError before the C stack runs out: no cap there


class RecursionLimitCapTests(unittest.TestCase):
    """Spec section 4: on Python < 3.12 a snippet cannot raise the recursion limit past the host's cap."""

    def setUp(self):
        self.addCleanup(code_exec.set_max_recursion_limit, runner.MAX_RECURSION_LIMIT)

    def warnings(self, result):
        return [entry["message"] for entry in result["logs"] if entry["level"] == "warning"]

    def test_raised_limit_and_c_recursion_do_not_end_the_host(self):
        # A child process: without the cap this ends the interpreter (3.11: a C stack overflow) before any assertion.
        code = "import sys\nsys.setrecursionlimit(1000000)\nx = []\nfor _ in range(300000):\n    x = [x]\nlen(repr(x))"
        script = "import json, sys\nsys.path.insert(0, %r)\nimport code_exec\nprint(json.dumps(code_exec.run(%r)))" % (
            os.path.join(ROOT, "python"), code)
        child = subprocess.run([sys.executable, "-B", "-c", script], capture_output=True, text=True, timeout=120)
        self.assertEqual(0, child.returncode, "the snippet ended the process: %r" % (child.stderr[-2000:],))
        result = json.loads(child.stdout)
        self.assertEqual("Runtime", result["errorKind"], result)
        self.assertTrue(result["error"].startswith("RecursionError"), result["error"])
        if CAPPED:
            self.assertEqual(1, len(self.warnings(result)), result["logs"])

    def test_limit_past_the_cap_is_clamped_with_a_warning_and_restored(self):
        before, setter = sys.getrecursionlimit(), sys.setrecursionlimit
        result = code_exec.run("import sys\nsys.setrecursionlimit(50000)\nsys.setrecursionlimit(60000)\nsys.getrecursionlimit()")
        self.assertTrue(result["success"], result)
        self.assertEqual(before, sys.getrecursionlimit())
        self.assertIs(setter, sys.setrecursionlimit)
        if not CAPPED:
            self.assertEqual("60000", result["value"])
            self.assertEqual([], self.warnings(result))
            return
        self.assertEqual(3000, runner.MAX_RECURSION_LIMIT)
        self.assertEqual("3000", result["value"])
        warnings = self.warnings(result)
        self.assertEqual(1, len(warnings), result["logs"])  # one warning per run
        self.assertIn("sys.setrecursionlimit(50000) was capped at 3000", warnings[0])

    def test_limit_within_the_cap_and_bad_values_pass_through(self):
        result = code_exec.run("import sys\nsys.setrecursionlimit(2500)\nsys.getrecursionlimit()")
        self.assertEqual("2500", result["value"])
        self.assertEqual([], self.warnings(result))
        for code, error in (("sys.setrecursionlimit(-1)", "ValueError"), ("sys.setrecursionlimit('x')", "TypeError")):
            with self.subTest(code):
                result = code_exec.run("import sys\n" + code)
                self.assertEqual("Runtime", result["errorKind"])
                self.assertTrue(result["error"].startswith(error), result["error"])

    @unittest.skipUnless(CAPPED, "the cap applies to Python < 3.12 only")
    def test_host_sets_the_cap(self):
        code = "import sys\nsys.setrecursionlimit(2500)\nsys.getrecursionlimit()"
        code_exec.set_max_recursion_limit(2000)
        self.assertEqual("2000", code_exec.run(code)["value"])
        code_exec.set_max_recursion_limit(0)  # no cap
        result = code_exec.run("import sys\nsys.setrecursionlimit(50000)\nsys.getrecursionlimit()")
        self.assertEqual("50000", result["value"])
        self.assertEqual([], self.warnings(result))
        for bad in (-1, True, 1.5, "3000"):
            with self.subTest(bad), self.assertRaises(ValueError):
                code_exec.set_max_recursion_limit(bad)

    @unittest.skipUnless(CAPPED, "the cap applies to Python < 3.12 only")
    def test_index_objects_are_capped_and_a_kept_reference_caps_nothing_afterwards(self):
        before, getter = sys.getrecursionlimit(), sys.getrecursionlimit
        code = ("import sys\nclass Big:\n    def __index__(self):\n        return 50000\nsys.setrecursionlimit(Big())\n"
                "kept = sys.setrecursionlimit\nsys.getrecursionlimit = lambda: 1\nsys.recursion_cap = kept\n"
                "sys.getrecursionlimit()")
        try:
            result = code_exec.run(code)
            self.assertEqual("1", result["value"])
            self.assertIn("sys.setrecursionlimit(50000) was capped at 3000", self.warnings(result)[0])
            self.assertIs(getter, sys.getrecursionlimit)
            self.assertEqual(before, sys.getrecursionlimit())
            sys.recursion_cap(before + 5000)  # the snippet's reference, called by the host after the run
            self.assertEqual(before + 5000, sys.getrecursionlimit())
        finally:
            sys.setrecursionlimit(before)
            vars(sys).pop("recursion_cap", None)

    @unittest.skipUnless(CAPPED, "the cap applies to Python < 3.12 only")
    def test_a_host_limit_above_the_cap_stays_available(self):
        before = sys.getrecursionlimit()
        sys.setrecursionlimit(5000)
        try:
            result = code_exec.run("import sys\nsys.setrecursionlimit(5000)\nsys.setrecursionlimit(6000)\nsys.getrecursionlimit()")
            self.assertEqual("5000", result["value"])
            self.assertIn("capped at 5000", self.warnings(result)[0])
            self.assertEqual(5000, sys.getrecursionlimit())
        finally:
            sys.setrecursionlimit(before)


if __name__ == "__main__":
    unittest.main()
