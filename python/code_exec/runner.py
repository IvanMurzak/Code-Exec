"""Snippet runner: spec/contract.md sections 4 and 6. Standard library only, Python 3.10+."""

import ast
import base64
import builtins
import collections
import io
import json
import os
import sys
import time
import tokenize
import traceback
from contextlib import redirect_stderr, redirect_stdout

SNIPPET_FILE = "snippet.py"

# Output limits (spec section 6), in characters; 0 = no limit.
MAX_VALUE_CHARS = 20000
MAX_LOG_CHARS = 20000
MAX_LOG_LINE_CHARS = 2000
MAX_ERROR_CHARS = 20000
_LOG_ENTRY_CHARS = 12  # what one log entry costs on top of its message ("[warning] " and a line break, as hosts print it)
_FRAMES_KEPT = 10  # a traceback keeps the first and the last _FRAMES_KEPT of the agent's frames
_MAX_CAUSES = 5  # chained exceptions (__cause__ / __context__) reported after the error itself

# JSON request field -> run() keyword argument.
_LIMIT_FIELDS = {
    "maxValueChars": "max_value_chars",
    "maxLogChars": "max_log_chars",
    "maxLogLineChars": "max_log_line_chars",
    "maxErrorChars": "max_error_chars",
}

_value_converter = None


def set_value_converter(converter):
    """Host hook: ``converter(value) -> value | NotImplemented``. NotImplemented falls back to the default."""
    global _value_converter
    _value_converter = converter


def run(code, include=(), include_root=None, imports=(), max_value_chars=MAX_VALUE_CHARS, max_log_chars=MAX_LOG_CHARS,
        max_log_line_chars=MAX_LOG_LINE_CHARS, max_error_chars=MAX_ERROR_CHARS, include_outside_root=False):
    """Execute ``code`` and return the contract's response as a dict (never raises for snippet errors).

    ``include_outside_root``: the host accepts includes outside ``include_root`` (absolute paths, ``..``).
    """
    result = _empty()
    limits = (max_value_chars, max_log_chars, max_log_line_chars, max_error_chars)
    if any(isinstance(limit, bool) or not isinstance(limit, int) or limit < 0 for limit in limits):
        return _fail(result, "InvalidRequest", "output limits must be integers >= 0 (0 = no limit)")
    logs = _Logs(max_log_chars, max_log_line_chars)
    _execute(result, logs, code, include, include_root, imports, include_outside_root)

    result["logs"] = logs.entries()
    value, error = result["value"], result["error"]
    if isinstance(value, str):
        result["value"] = _clip(value, max_value_chars, "return a slice or a summary")
    if isinstance(error, str):
        result["error"] = _clip(error, max_error_chars, "raise a shorter message")
    changed = (("value", result["value"] is not value), ("logs", logs.left_out or logs.clipped), ("error", result["error"] is not error))
    result["truncated"] = [field for field, truncated in changed if truncated]
    return result


def _execute(result, logs, code, include, include_root, imports, include_outside_root):
    started = time.perf_counter()
    if not isinstance(code, str) or not code.strip():
        return _fail(result, "InvalidRequest", "code is empty")
    if not isinstance(imports, (list, tuple)) or not all(isinstance(name, str) and name for name in imports):
        return _fail(result, "InvalidRequest", "imports must be a list of module names")
    if include_root is not None and not isinstance(include_root, str):
        return _fail(result, "InvalidRequest", "includeRoot must be a path")

    root = os.path.abspath(include_root or os.getcwd())
    try:
        folders, files = _resolve_include(include, root, include_outside_root)
        sources = [(path, _read(full)) for path, full in files]
    except (OSError, ValueError) as error:
        return _fail(result, "InvalidRequest", str(error))

    try:
        file_code = [compile(source, path, "exec") for path, source in sources]
        tree = ast.parse(code, SNIPPET_FILE, "exec")
        last = None
        if tree.body and isinstance(tree.body[-1], ast.Expr):
            last = ast.Expression(tree.body.pop().value)
        body = compile(tree, SNIPPET_FILE, "exec")
        tail = compile(last, SNIPPET_FILE, "eval") if last is not None else None
    except SyntaxError as error:
        result["diagnostics"].append({
            "file": error.filename or SNIPPET_FILE,
            "line": error.lineno or 0,
            "column": error.offset or 0,
            "id": type(error).__name__,
            "severity": "error",
            "message": error.msg,
        })
        result["compileMs"] = _ms(started)
        return _fail(result, "Compile", "Compilation failed")
    except (ValueError, RecursionError, MemoryError) as error:  # null bytes (3.10), a too deeply nested expression
        result["compileMs"] = _ms(started)
        return _fail(result, "Compile", "%s: %s" % (type(error).__name__, error))
    result["compileMs"] = _ms(started)

    executing = time.perf_counter()
    scope = {"__name__": "__code_exec__", "__builtins__": builtins}
    out, err = _LogStream(logs, "info"), _LogStream(logs, "error")
    prefixes = [_prefix(folder) for folder in folders]
    saved_path = list(sys.path)
    saved_bytecode = sys.dont_write_bytecode
    saved_cwd = os.getcwd()
    try:
        sys.path[:0] = folders
        if folders:
            # Helpers are edited between runs, often within the same second and at the same size, which a cached
            # .pyc cannot tell apart: never write bytecode for them, and forget the folders' directory listings.
            sys.dont_write_bytecode = True
            for folder in folders:
                sys.path_importer_cache.pop(folder, None)
        for name in imports:
            scope.setdefault(name.split(".")[0], __import__(name))  # like `import a.b`: binds the top package
        with redirect_stdout(out), redirect_stderr(err):
            for compiled in file_code:
                exec(compiled, scope)
            exec(body, scope)
            value = eval(tail, scope) if tail is not None else None
        result["value"] = _convert(value)
        result["success"] = True
    except BaseException as error:  # SystemExit, KeyboardInterrupt, ...: a snippet must never take the host down
        result["errorKind"] = "Runtime"
        result["error"] = _format_error(error, root, prefixes, {path for path, _ in files})
    finally:
        out.flush()
        err.flush()
        sys.path[:] = saved_path
        sys.dont_write_bytecode = saved_bytecode
        _forget_modules(prefixes)
        try:
            os.chdir(saved_cwd)
        except OSError:
            pass  # the folder is gone: nothing to restore
    result["executeMs"] = _ms(executing)
    return result


def run_json(payload):
    """JSON request (spec section 1 plus Python-only ``imports`` / ``includeRoot``) -> JSON response."""
    try:
        request = json.loads(payload)
    except ValueError as error:
        return _invalid("request is not JSON: %s" % error)
    if not isinstance(request, dict):
        return _invalid("request must be a JSON object")
    limits = {name: request[field] for field, name in _LIMIT_FIELDS.items() if request.get(field) is not None}
    response = run(
        request.get("code", ""),
        include=request.get("include") or (),
        include_root=request.get("includeRoot"),
        imports=request.get("imports") or (),
        include_outside_root=request.get("includeOutsideRoot") is True,
        **limits)
    return json.dumps(response, default=str)  # a host converter's odd value must not fail the response


def run_b64(payload_b64):
    """Same as :func:`run_json`, with the request base64-encoded: hosts pass it through one string literal."""
    try:
        payload = base64.b64decode(payload_b64).decode("utf-8")
    except (ValueError, UnicodeDecodeError) as error:
        return _invalid("request is not base64 UTF-8: %s" % error)
    return run_json(payload)


def _empty():
    return {
        "success": False, "value": None, "logs": [], "errorKind": "None", "error": None, "diagnostics": [],
        "autoImported": [], "compileMs": 0, "executeMs": 0, "cacheHit": False, "truncated": [],
    }


def _fail(result, kind, message):
    result["success"] = False
    result["errorKind"] = kind
    result["error"] = message
    return result


def _invalid(message):
    return json.dumps(_fail(_empty(), "InvalidRequest", message))


def _ms(since):
    return int((time.perf_counter() - since) * 1000)


def _clip(text, limit, hint):
    """Spec section 6: the first ``limit`` characters plus a note saying how much was shown (``text`` itself if it fits)."""
    if not limit or len(text) <= limit:
        return text
    return "%s... [truncated: %d of %d characters shown; %s]" % (text[:limit], limit, len(text), hint)


def _read(path):
    """Reads an include as Python reads a source file: UTF-8, or the encoding its BOM / coding declaration names."""
    try:
        with tokenize.open(path) as handle:
            return handle.read()
    except (SyntaxError, UnicodeDecodeError) as error:  # SyntaxError: an unknown or contradicting coding declaration
        raise ValueError("include is not readable as Python source: %s (%s)" % (path, error)) from None


def _resolve_include(include, root, outside_root):
    """Folders go on sys.path (import by name); ``.py`` files run in the snippet's namespace first, in order. Both must
    lie inside ``root`` unless the host allows ``outside_root``."""
    root_prefix = _prefix(root)  # normcased, ends with a separator: _prefix(path) starts with it for the root and below
    if not isinstance(include, (list, tuple)):
        raise ValueError("include must be a list of paths")
    folders, files, seen = [], [], set()
    for entry in include:
        if not isinstance(entry, str):
            raise ValueError("include entry is not a path: %r" % (entry,))
        if not entry.strip():
            raise ValueError("include entry is empty")
        full = os.path.abspath(entry if os.path.isabs(entry) else os.path.join(root, entry))
        if not outside_root and not _prefix(full).startswith(root_prefix):
            raise ValueError("include is outside the include root: %s" % entry)
        if full in seen:
            continue
        seen.add(full)
        if os.path.isdir(full):
            folders.append(full)
        elif os.path.isfile(full):
            if not full.lower().endswith(".py"):
                raise ValueError("include is not a .py file: %s" % entry)
            files.append((_relative(full, root), full))
        else:
            raise FileNotFoundError("include not found: %s" % entry)
    return folders, files


def _prefix(folder):
    return os.path.normcase(os.path.join(folder, ""))


def _under(path, prefixes):
    path = os.path.normcase(os.path.abspath(path))
    return any(path.startswith(prefix) for prefix in prefixes)


def _relative(path, root):
    relative = os.path.relpath(path, root) if _under(path, [_prefix(root)]) else path
    return relative.replace("\\", "/")


def _forget_modules(prefixes):
    """Drop modules imported from include folders, so an edited helper is re-imported on the next run."""
    if not prefixes:
        return
    for name, module in list(sys.modules.items()):
        location = getattr(module, "__file__", None)
        if location and _under(location, prefixes):
            del sys.modules[name]


def _convert(value):
    if _value_converter is not None:
        converted = _value_converter(value)
        if converted is not NotImplemented:
            return converted
    if value is None or isinstance(value, str):
        return value
    if isinstance(value, (dict, list, tuple)):
        try:
            return json.dumps(value)
        except (TypeError, ValueError):
            pass
    return str(value)


def _format_error(error, root, prefixes, include_files):
    """``Type: message`` plus only the frames the agent wrote (snippet.py, include files, include folders), then the
    exceptions it was raised from (``__cause__``) or while handling (``__context__``), at most _MAX_CAUSES of them."""
    lines = _format_one(error, root, prefixes, include_files)
    seen = {id(error)}
    while True:
        cause, label = _cause(error)
        if cause is None or id(cause) in seen:
            break
        if len(seen) > _MAX_CAUSES:
            lines.append("... more chained exception(s) left out")
            break
        seen.add(id(cause))
        first, *rest = _format_one(cause, root, prefixes, include_files)
        lines.append("%s: %s" % (label, first))
        lines.extend(rest)
        error = cause
    return "\n".join(lines)


def _cause(error):
    if error.__cause__ is not None:
        return error.__cause__, "Caused by"
    if error.__context__ is not None and not error.__suppress_context__:
        return error.__context__, "Raised while handling"
    return None, None


def _format_one(error, root, prefixes, include_files):
    frames = []
    for frame in traceback.StackSummary.extract(traceback.walk_tb(error.__traceback__), lookup_lines=False):
        if frame.filename == SNIPPET_FILE or frame.filename in include_files:
            frames.append(frame)
        elif _under(frame.filename, prefixes):
            frame.filename = _relative(os.path.abspath(frame.filename), root)
            frames.append(frame)
    try:
        message = str(error)
    except BaseException:  # the exception's own __str__ raised
        message = "<unprintable message>"
    lines = ["%s: %s" % (type(error).__name__, message)]
    if len(frames) > 2 * _FRAMES_KEPT:
        left_out = len(frames) - 2 * _FRAMES_KEPT
        lines.extend(_format_frames(frames[:_FRAMES_KEPT]))
        lines.append("  ... %d frame(s) left out" % left_out)
        lines.extend(_format_frames(frames[-_FRAMES_KEPT:]))
    else:
        lines.extend(_format_frames(frames))
    return lines


def _format_frames(frames):
    return [line.rstrip("\n") for line in traceback.format_list(frames)]


class _Logs:
    """The log budget of spec section 6, applied as lines arrive, so a runaway print loop cannot grow memory: the
    first lines up to 3/4 of the budget, then the most recent ones that still fit."""

    def __init__(self, max_chars, max_line_chars):
        self._max = max_chars
        self._max_line = max_line_chars
        self._head, self._tail = [], collections.deque()
        self._head_open = True
        self._head_used = self._tail_used = 0
        self.left_out = 0
        self.clipped = False

    def add(self, level, message):
        clipped = _clip(message, self._max_line, "print less")
        self.clipped = self.clipped or clipped is not message
        entry = {"level": level, "message": clipped}
        if not self._max:
            self._head.append(entry)
            return
        size = _cost(entry)
        if self._head_open and self._head_used + size <= self._max * 3 // 4:
            self._head.append(entry)
            self._head_used += size
            return
        self._head_open = False
        self._tail.append(entry)
        self._tail_used += size
        while self._tail and self._head_used + self._tail_used > self._max:
            self._tail_used -= _cost(self._tail.popleft())
            self.left_out += 1

    def entries(self):
        if not self.left_out:
            return self._head + list(self._tail)
        marker = {"level": "warning", "message": "... %d log line(s) left out (output over %d characters): print less, "
                                                 "or return a summary" % (self.left_out, self._max)}
        return self._head + [marker] + list(self._tail)


def _cost(entry):
    return len(entry["message"]) + _LOG_ENTRY_CHARS


class _LogStream(io.TextIOBase):
    """Collects written text as one log entry per line."""

    def __init__(self, logs, level):
        super().__init__()
        self._logs = logs
        self._level = level
        self._parts = []

    def writable(self):
        return True

    def write(self, text):
        if not isinstance(text, str):
            raise TypeError("write() argument must be str, not %s" % type(text).__name__)
        *ended, rest = text.split("\n")
        for piece in ended:
            self._parts.append(piece)
            self._logs.add(self._level, "".join(self._parts).rstrip("\r"))
            self._parts = []
        if rest:
            self._parts.append(rest)
        return len(text)

    def flush(self):
        if self._parts:
            self._logs.add(self._level, "".join(self._parts))
            self._parts = []
