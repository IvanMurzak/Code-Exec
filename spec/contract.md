# Code-Exec contract (v0.3)

One request/response shape for every runtime (C# and Python) and every host (Unity, Godot, BRUTAL, Unreal, plain
.NET). **This file is normative: a runtime that disagrees with it is the bug.** Behaviour changes start here, then
`fixtures/golden/*.json`, then every runtime.

## 1. Request

| Field | Type | Meaning |
|---|---|---|
| `code` | string, required | The snippet. See §3 (C#) / §4 (Python). |
| `usings` | string[] | C# only. Extra namespaces, added to the host's defaults (full-source mode: to the code alone, §3.2). Aliases (`X = A.B`) and `static A.B` are allowed. |
| `include` | string[] | Files or folders to compile/load with the snippet (§1.1). |
| `entry` | `{ type, method }` | C# only. Full-source mode: the code is compiled verbatim and `type.method` is invoked. |
| `parameters` | `{ name, type, value }[]` | C# only. Arguments passed to the snippet; `type` is a host type, `value` an already-deserialized object. |
| `timeoutMs` | int | The snippet's time budget. A snippet still running when it is spent stops with `Timeout`: C# through the execution guards (§3.6) and by no longer awaiting an asynchronous result, Python through a watchdog (§4). `0` or absent = the host's default (`120000` unless the host sets another; a host default of `0` = no bound). A host may cap it (C# `CodeRunnerOptions.MaxTimeoutMs`, Python `code_exec.set_max_timeout_ms`; `0` = no cap, the default): a longer budget, or none, becomes the cap. |
| `imports` | string[] | Python only. Modules the host binds into the snippet's namespace before it runs (`unreal`). |
| `includeRoot` | string | Python JSON entry point only (`run_json` / `run_b64`): folder relative `include` paths resolve against. |
| `includeOutsideRoot` | bool | Python JSON entry point only: the host accepts includes outside the include root (§1.1). Default `false`. |
| `maxValueChars` | int | Output limit for a text `value` (§6). Default `20000`. |
| `maxLogChars` | int | Output limit for all `logs` together (§6). Default `20000`. |
| `maxLogLineChars` | int | Output limit for one log message (§6). Default `2000`. |
| `maxErrorChars` | int | Output limit for `error` (§6). Default `20000`. |

Every field but `code` is optional. For the four limits, `0` means no limit; a negative (or non-integer) limit or
`timeoutMs` is `InvalidRequest`. A request without them gets the defaults. A field of the wrong JSON type is
`InvalidRequest`.

Hosts that serve agents should let the agent pass `timeoutMs` (a `timeout` argument of their execute tool) and keep
the budget short by default: an endless loop costs the agent the whole budget before it hears back. Recommended: a host
default of 30000 for interactive use, and a cap a few seconds below the host's own tool timeout, so the snippet's
`Timeout` answers before the host gives up.

### 1.1. Includes
- Relative paths resolve against the host's include root. An include must lie **inside** that root (the root itself
  included): a path that leaves it (`../x`, an absolute path elsewhere) is `InvalidRequest`, unless the host allows it
  (C# `CodeRunnerOptions.AllowIncludeOutsideRoot`, Python `include_outside_root=True` / `includeOutsideRoot`). Includes
  are the agent's helper code, not a way to compile arbitrary project files.
- A file must be source of the runtime's language: `.cs` (C#), `.py` (Python); anything else is `InvalidRequest`.
  Folders are recursive and contribute only such files (C#: `*.cs`; Python: the folder goes on `sys.path`).
- Diagnostics and stack frames name an include by its path relative to the root (`/` separators), or by its absolute
  path when the host allowed one outside the root.

## 2. Response

| Field | Type | Meaning |
|---|---|---|
| `success` | bool | |
| `value` | any | The result, converted by the host **before** the snippet's code is unloaded. `null` when there is none. |
| `returnType` | string | C# only. Result type, `Task<T>` → `T`: `System.Void` when nothing is returned; for a snippet whose result is its last expression, that expression's static type (`System.Int32`, `System.Collections.Generic.List<System.Int32>`); `System.Object` when a snippet (also) returns through `return` statements, `return;` included; the method's type in full-source mode. |
| `logs` | `{ level, message }[]` | Output written while the snippet ran (`Console`, `print`, engine log hooks). |
| `errorKind` | enum | `None`, `InvalidRequest`, `Compile`, `EntryPointNotFound`, `Runtime`, `Timeout`, `NotAvailable`. |
| `error` | string | Human-readable error; for `Runtime`: `ExceptionType: message` plus the snippet's stack frames. |
| `diagnostics` | `{ file, line, column, id, severity, message }[]` | Compile errors in the **agent's** coordinates: `snippet.cs` / `snippet.py` or the include's relative path (§3.4). At most 20, then one `CE0002` entry counting the rest. |
| `autoImported` | string[] | C# only. Namespaces added automatically (§3.3). |
| `compileMs`, `executeMs` | int | Timings. |
| `cacheHit` | bool | The compiled snippet was reused. |
| `truncated` | string[] | The fields an output limit shortened (§6), in this order: `value`, `logs`, `error`. Empty when nothing was. |

## 3. C# snippet semantics

### 3.1. Shape
The snippet is parsed as C# **script** code and rewritten into one ordinary compilation unit:

- `using` directives (including aliases and `using static`) go to the top, after the host defaults and `usings`;
  `global using` goes before those. A using declaration or statement (`using var x = ...;`, `using T x = ...;`,
  `await using ...`, `using (...)`) is a statement wherever it stands, also as the first line: only `using N;`,
  `using static`, `using unsafe`, an alias (`using A = B;`) and `global using` are directives;
- type declarations (class, struct, record, interface, enum, delegate, namespace) stay top-level;
- members **without** modifiers live in the entry method's body, in source order, so they see the snippet's locals:
  `var x = 1;` is a local variable and `int Twice(int n) => n * 2;` a local function;
- members **with** modifiers (methods, properties, indexers, events, fields: `static int counter = 3;`) become static
  members of the generated class;
- the **last statement** (trailing local functions aside), when it is an expression without `;`, or an expression that
  is not a valid statement (`x * 2;`), is returned; if that expression is `void`, it runs as a plain statement and the
  result is `null`;
- a snippet with neither such a result expression nor a `return expr;` returns nothing: its entry method is `void`
  (`returnType` = `System.Void`); otherwise `return;` means `return null;` and any `return expr;` returns `expr`;
- `await` anywhere in the statements (outside lambdas and local functions) makes the entry method asynchronous.

The generated names are an implementation detail (`__CodeExecSnippet.__Run`); the agent never needs them.

### 3.2. Full-source mode
With `entry`, the code is compiled as a regular file and the named static method is invoked (namespace-qualified type
names allowed). It is not rewritten into a method; it only gets the request's `usings` and the auto-imported namespaces
(§3.3) in front of it, and the execution guards (§3.6). The host's defaults are **not** added: when names stay
unresolved, an extra diagnostic `CE0003` says so, naming the defaults and the unresolved names. Code that starts with `extern alias` or
`global using` gets nothing in front of it (no `usings`, no auto-import). Among overloads, the most specific one for the
parameters' types is invoked; with none fitting, the only method of that name and parameter count. Anything else
(ambiguous, several that do not fit) is `EntryPointNotFound`.

### 3.3. Auto-import
When compilation fails with unknown names (`CS0246`, `CS0103`) in the agent's code (snippet or full source, not
includes), the runtime looks the simple name up among public
types of the loaded assemblies, except the runtime's own `Internal` / `Internal.*` namespaces in CoreLib
(`Internal.Console` writes past log capture). **Exactly one** namespace → it is added, compilation is retried, and the namespace is
reported in `autoImported`. Several namespaces → no guess: an extra diagnostic `CE0001` lists the candidates.

### 3.4. Errors
Compile errors are reported with `file` = `snippet.cs` (the agent's own line numbers) or the include's path. An error
in a namespace the runtime adds (the host's defaults and the request's `usings`) has `file` = `usings`, line and column
`0`, and a message that names it: `using 'No.Such.Namespace': The type or namespace name 'No' could not be found ...`.
Any exception the snippet throws (or its result's conversion throws) is a `Runtime` error: `ExceptionType: message`,
the snippet's frames, then each inner exception the same way after ` ---> ` (at most 5). An exception whose `Message`
itself throws is reported as `ExceptionType: <unprintable message>`. Runaway recursion and endless loops are stopped
by the execution guards (§3.6), so neither ends nor freezes the host.

Host defaults that collide (`System` and `UnityEngine` both define `Object` and `Random`: `CS0104`) are the host's to
settle with alias defaults, e.g. `Object = UnityEngine.Object`, `Random = UnityEngine.Random`.

### 3.5. Lifetime
Each execution is stateless. On CoreCLR the snippet's assembly is loaded into its own collectible
`AssemblyLoadContext` and unloaded after the result is converted; its references bind to the assemblies the process
already runs (any load context still alive; same identity first, else the same name at a version no lower than
referenced; ties: the default context, then the higher version, then the later loaded) before the default context is
asked, so the snippet sees the host's live statics, never a second copy. On Mono (Unity) assemblies cannot be unloaded;
identical requests reuse the already-loaded assembly (cache) — unless the snippet (or an include) declares static state
of its own (a static field, auto-property or field-like event in one of its types): then every run loads a fresh copy,
so no run ever sees another run's statics. Such a snippet costs one assembly per run until the host's domain reloads.
A `static readonly` field (or a getter-only static auto-property) is **not** state when its declared type is immutable:
`string`, a primitive other than `IntPtr` / `UIntPtr`, an enum, `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`,
`Guid`, a nullable of one of these, or a read-only / immutable collection of immutable elements
(`IReadOnlyCollection<T>`, `IReadOnlyList<T>`, `IReadOnlyDictionary<K,V>`, `IReadOnlySet<T>`, `ReadOnlyCollection<T>`,
`ReadOnlyDictionary<K,V>`, `System.Collections.Immutable.*`). So a lookup table like
`static readonly IReadOnlyDictionary<string, int> Map` keeps the snippet cached. Arrays are mutable
(`static readonly int[]` is state), and no collection type counts as immutable when the snippet itself implements or
derives from one of them. Consequences of reuse on Mono, by design: such a field's initializer runs once per loaded copy,
so a value read from live host state keeps its first value (`static readonly int Cameras = FindObjectsOfType<Camera>().Length`,
`static readonly DateTime Started = DateTime.Now`: read live values in a method or property instead); and a host object
behind a read-only declared type, or a cast back to a mutable type, is not detected.

### 3.6. Execution guards
.NET cannot catch a stack overflow (it ends the process), and code running on the host's main thread cannot be
aborted from outside. So the runtime compiles guards into the agent's code (snippet, full source and includes), unless
the host turns them off (`CodeRunnerOptions.ExecutionGuards`):

- **Recursion.** Every method, local function, accessor, operator, constructor, lambda and anonymous method, and the
  field and property initializers of a class, check the stack every few calls: against the host's allowance
  (`CodeRunnerOptions.MaxStackBytes`, 1 MiB by default, on the thread that runs the snippet, counted from where the run
  started) and the runtime's own margin (`RuntimeHelpers.EnsureSufficientExecutionStack`, every thread). Past the
  allowance or close to the limit the run stops with a `Runtime` error
  `System.InsufficientExecutionStackException: Recursion too deep: ...`, before a stack overflow could end the host.
  An expression-bodied lambda gets the check when it is a delegate (`Func<int, int> f = n => ... f(n - 1)`); not when it
  is an expression tree, sits inside one, inside a query expression or inside an interpolation hole, or when its file has
  a compile error. Not guarded either: members the compiler generates (a record's `ToString`). Recursion that runs only
  through those is not caught.
- **Time.** Every loop iteration (`while`, `do`, `for`, `foreach`) and every `goto` counts down and, every few
  iterations, reads the clock (every iteration while iterations are slow); so do the calls above. Once `timeoutMs` is
  spent, the run stops with `Timeout`: `System.TimeoutException: The snippet ran longer than its time budget of N ms
  (timeoutMs) and was stopped.`, with the frame where it was (`snippet.cs:line N`). Code that blocks inside a host or
  framework call (`Thread.Sleep`, a lock, an engine call that never returns) is stopped only when the call returns.
- **Once stopped**, every further check throws again, and the agent's `catch` clauses that could catch the stop (no
  type, `Exception`, `SystemException` or the stop's own type) get a filter that lets it pass, so it reaches the runtime;
  `finally` blocks still run. That filter makes a few invalid programs compile (a later `catch` that an earlier
  catch-all would hide: `CS0160`).
- **Threads and tasks.** A delegate the agent's code hands to `new Thread(...)`, `ThreadPool.QueueUserWorkItem` /
  `UnsafeQueueUserWorkItem`, `Task.Run`, `Task.Factory.StartNew` (any `TaskFactory.StartNew`) or `new Task(...)` belongs
  to the run, and so do the threads and tasks it starts the same way. It runs under the same guards and, once the run
  has ended (finished or timed out), stops at its next check with
  `System.TimeoutException: The snippet's run has ended, so this thread or task it started was stopped: ...`. It passes
  the agent's catch clauses like any stop. A thread or thread-pool entry swallows a stop (an exception left unhandled
  on a thread would end the process); a task faults with it. Asynchronous task work (`Task.Run(async () => ...)`)
  belongs to the run until its task ends, across its awaits. A stop inside such work while the snippet waited for it
  (`Join`, `Wait`) ends the run too: `Timeout` for the budget, `Runtime` (`Recursion too deep`) for the stack. When
  work of the run had not finished as it ended, the response gets one `warning` log entry: `N thread(s) or task(s) the
  snippet started had not finished when it ended: they are stopped at their next loop iteration or call. Join or await
  work the snippet starts.` (work created but never started counts too). Not covered, so it may outlive the run: timers
  (`System.Threading.Timer`, `System.Timers.Timer`, engine timers) and other callbacks; an `async void` delegate's code
  after its first `await` (`new Thread(async () => ...)`); work blocked in a wait or a host call (it stops once that
  returns); threads started by native code or by host code the snippet calls; a start written through an alias of
  `Thread` / `Task`, inside a query expression, or whose delegate type cannot be named (an anonymous type); a delegate
  stored first and started later through another API; and work that a callback left behind starts after the run (it
  is the callback's own). Caveat: a timer or other callback created inside asynchronous task work inherits the run
  through its execution context; its code is stopped like the task's, and an exception unhandled in a timer callback
  ends the process.
- **After the run.** A run that finished lifts the time budget: callbacks it left behind (events, stored delegates)
  keep only the stack check. A timed-out run keeps its stop, so what still runs of it (an asynchronous continuation,
  a thread it started) stops at its next check; on a host that cannot unload (Mono), its assembly is not reused.
- The guards change no line or column the agent sees (diagnostics, stack frames). They cost about one countdown per
  loop iteration and per call: on .NET 8 about 1 ns per iteration and 1–7 ns per call of the snippet's own methods.

## 4. Python snippet semantics

- The snippet is a module body. When the **last statement is an expression**, its value is the result; otherwise `null`.
- `include` folders are put on `sys.path` for the run; modules loaded from them are dropped from `sys.modules`
  before each run, so edited helpers are picked up. `include` files are executed into the snippet's namespace first.
- `print` / `sys.stdout` / `sys.stderr` go to `logs`.
- Each run gets a fresh namespace; `sys.path`, the working directory and the recursion limit are restored afterwards,
  and the modules the run imported from include folders are dropped (host modules imported before stay).
- `include` files are read as Python reads source files: UTF-8, or the encoding named by a UTF-8 BOM or a PEP 263
  coding declaration. A file that does not decode (or names an unknown or non-text encoding) is `InvalidRequest`.
- `SyntaxError` → `Compile` with the snippet's line/column. Any exception → `Runtime`, **including every
  `BaseException`** (`SystemExit`, `KeyboardInterrupt`, `GeneratorExit`, custom subclasses): a snippet never stops or
  interrupts the host. The traceback keeps only frames from `snippet.py` and the includes, then follows the chain (at
  most 5): `Caused by: ...` for `raise ... from cause`, `Raised while handling: ...` for an implicit `__context__`. An
  exception whose `str()` raises is reported as `ExceptionType: <unprintable message>`.
- **Time budget.** When `timeoutMs` is spent, a watchdog thread raises `SnippetTimeout` (a `BaseException`) in the
  snippet's thread, and again every 100 ms while the snippet catches it: `Timeout`, `SnippetTimeout: The snippet ran
  longer than its time budget of N ms (timeoutMs) and was stopped.` plus the snippet's frames. Python delivers it
  between bytecodes: pure Python loops stop; a call into C (`time.sleep`, an engine call) finishes first, and a snippet
  that catches `BaseException` in its loop is not stopped. Python 3.10 never delivers it, nor releases the GIL, in a loop
  whose body begins with `try` or `with` (`while True: try: ...`): such a run hangs the interpreter there (3.11+ stop
  it). It needs `ctypes` (`PyThreadState_SetAsyncExc`); without it
  the budget is not enforced. The stop is disarmed before the runner returns: none reaches the host afterwards.
- **Threads.** Threads the snippet starts (`threading.Thread.start`, `threading.Timer` included, called from the
  snippet's thread or from one of those threads while it runs) may not outlive it. When it ends (finished or timed
  out), a `threading.Timer` that has not fired is cancelled, and every other such thread still alive gets
  `code_exec.SnippetThreadStopped` (a `BaseException`, the same mechanism as the time budget) whenever the snippet's or
  an include's code is what it runs, checked every 100 ms while such code is on its stack. The thread ends quietly
  (nothing is printed). One `warning` log entry counts the threads still running (cancelled timers aside): `N thread(s)
  the snippet started were still running when it ended: each is stopped while it runs the snippet's code. Join threads
  before the snippet returns.` A thread inside library or host code (`queue.get`) gets no stop until it returns to the
  snippet's code; one with none of it left on its stack (a `concurrent.futures` worker waiting for work) is left alone
  from then on; one that catches `BaseException` every time is not stopped. `threading.Thread.start` is replaced only
  while a snippet runs. Not covered: `_thread.start_new_thread`, threads started by C code, and threads started by the
  snippet's threads after it has ended. Threads that host code starts while the snippet calls it count as the
  snippet's. Needs `ctypes`, like the time budget.
- **Recursion** ends with Python's own `RecursionError` (`Runtime`) at the interpreter's recursion limit. Python 3.11 and
  older do not check the C stack: past a few thousand, a raised limit lets C-level recursion (`repr` / `json.dumps` of a
  deeply nested list) overflow it, which ends the host. So on Python < 3.12 a snippet's (and its includes')
  `sys.setrecursionlimit(n)` sets at most the host's cap (default `3000`, `code_exec.set_max_recursion_limit(n)`, `0` = no
  cap; a limit the host set itself above the cap stays allowed). A capped call adds one `warning` log entry per run:
  `sys.setrecursionlimit(N) was capped at C: on Python 3.11 and older a higher limit can overflow the C stack and end the
  host`. The default suits hosts with an 8–12 MB stack (Unreal's game thread, Linux main threads); a host with a smaller
  one (Windows `python.exe`: 2 MB) should lower it. The cap guards against accidents, not against a snippet that sets out
  to get around it; modules the host lists in `imports` are not capped. Python 3.12+ check the C stack themselves
  (`RecursionError` for most C-level recursion) and are not capped. The recursion limit, `sys.setrecursionlimit` and
  `sys.getrecursionlimit` are restored after the run.
- `value` conversion: `None` → null; `str`, `int`, `float`, `bool` → their `str()`; `dict` / `list` / `tuple` →
  JSON when serializable; anything else → `str()` (hosts may plug a converter, e.g. Unreal objects → path).

## 5. Value text in golden tests
Golden fixtures compare `value` as text: C# `ToString()` (`True`, `42`), Python `str()` (`True`, `42`).

## 6. Output limits
A response goes straight into an agent's context, so a runaway `print` loop or a huge value must not flood it. The
runtime applies the request's limits (§1) before it answers; hosts may limit further (their own logs, other fields).
Lengths are in characters: UTF-16 code units in C#, code points in Python. A limit of `0` disables that limit.

- **Clip.** A text longer than its limit `N` keeps its first `N` characters followed by
  `... [truncated: N of M characters shown; HINT]` (`M` = the full length). C# never splits a surrogate pair: it then
  keeps `N - 1` characters and says so.
- **`value`** — clipped to `maxValueChars` when the converted value is a string; hint `return a slice or a summary`.
  A host converter that returns something else (an object it serializes later) limits that itself.
- **`error`** — clipped to `maxErrorChars`; hint `raise a shorter message`. Before that, a `Runtime` traceback with more
  than 20 of the agent's frames keeps the first 10 and the last 10, with one line `... K frame(s) left out` between.
- **`logs`** — every message is clipped to `maxLogLineChars` (hint `print less`). An entry then costs its message length
  plus 12 (its level and line break as hosts print them). When the entries cost more than `maxLogChars` in total, the
  runtime keeps the longest run of first entries that costs at most `floor(maxLogChars * 3 / 4)`, then the longest run
  of last entries that keeps the total within `maxLogChars`, and puts between them one entry
  `{ "level": "warning", "message": "... K log line(s) left out (output over maxLogChars characters): print less, or
  return a summary" }` (with the numbers filled in; this entry costs nothing). Runtimes apply this as lines arrive, so
  a snippet that prints line after line without end does not grow the host's memory either (one unfinished line is
  held until it ends).
- `truncated` lists `value`, `logs` and `error` when their limit changed them (a clipped log line counts as `logs`).

## Changelog

- **Unreleased** — execution guards (§3.6): C# snippets, full source and includes stop with `Runtime`
  (`Recursion too deep`) before a stack overflow and with `Timeout` once `timeoutMs` is spent, also in synchronous code
  on the main thread; Python stops with `Timeout` through a watchdog (§4). `timeoutMs` applies to both runtimes; `0`
  means the host's default (120000), a negative value is `InvalidRequest`. Full-source mode gets the request's `usings`
  and auto-import, and `CE0003` when names stay unresolved (§3.2). C#: expression-bodied lambdas that are delegates are
  guarded too (§3.6). Python < 3.12: a snippet's `sys.setrecursionlimit` is capped (default 3000) with a warning (§4).
  Threads and tasks a snippet starts stop when its run ends, with a `warning` when some had not finished (C# §3.6,
  Python §4). Hosts can cap `timeoutMs` (§1). C#: a using declaration (`using var x = ...;`) is a statement also as the
  first line, and `global using` goes before the runtime's usings (§3.1).

- **v0.3.1** — C#: `static readonly` fields of immutable types are not static state (§3.5). Python is unchanged.
- **v0.3** — output limits (§6): request fields `maxValueChars`, `maxLogChars`, `maxLogLineChars`, `maxErrorChars`
  (defaults 20000 / 20000 / 2000 / 20000, the caps Unreal-MCP and Godot-MCP applied on their side) and response field
  `truncated`; long tracebacks keep their first and last 10 frames; at most 20 diagnostics. Includes stay inside the
  include root unless the host allows otherwise, and must be `.cs` / `.py` files (§1.1). Errors report inner / chained
  exceptions. C#: a bad `using` is reported as itself (`file` = `usings`); a snippet with static state of its own never
  shares it between runs on Mono. Python: every `BaseException` (`KeyboardInterrupt`, `GeneratorExit`, ...) is a
  `Runtime` error, the working directory is restored, includes honour a BOM / coding declaration, a malformed JSON
  request field is `InvalidRequest`. Both runtimes: an exception whose message cannot be printed is still reported.

- **v0.2.1** — auto-import skips CoreLib's `Internal.*` namespaces; CoreCLR snippets bind to the already-loaded host
  assemblies (any load context) and always reference System.Runtime / System.Console / System.Collections /
  System.Linq, loaded or not, when the runtime directory is on disk.
- **v0.2** — `returnType` (C#). A snippet that returns nothing compiles to a `void` (or `async Task`) entry method.
- **v0.1** — first contract.
