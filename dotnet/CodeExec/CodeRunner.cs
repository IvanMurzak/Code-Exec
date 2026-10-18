using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace com.IvanMurzak.CodeExec
{
    public sealed class CodeRunnerOptions
    {
        /// <summary>Namespaces every snippet gets. Hosts add their own (UnityEngine, Godot, ...).</summary>
        public IReadOnlyList<string> DefaultUsings { get; set; } = new[] { "System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks" };

        /// <summary>Folder that relative include paths resolve against.</summary>
        public string IncludeRoot { get; set; } = Directory.GetCurrentDirectory();

        /// <summary>Defaults to <see cref="AssemblyHosts.CreateDefault"/>.</summary>
        public IAssemblyHost? Host { get; set; }

        /// <summary>Defaults to <see cref="LoadedAssembliesReferenceProvider"/>.</summary>
        public IReferenceProvider? References { get; set; }

        /// <summary>
        /// Turns the snippet's return value into something that outlives the snippet (text, a DTO, JSON).
        /// Runs BEFORE an unloadable snippet is released, so it must not return the snippet's own objects.
        /// </summary>
        public Func<object?, object?> ValueConverter { get; set; } = value => value?.ToString();

        public IList<ILogCapture> LogCaptures { get; set; } = new List<ILogCapture> { new ConsoleLogCapture() };

        /// <summary>Compiled snippets kept for identical requests (least recently used are dropped).</summary>
        public int CacheCapacity { get; set; } = 64;
    }

    public sealed class CodeRunnerStats
    {
        internal long executions, cacheHits, loadedAssemblies;
        public long Executions => Interlocked.Read(ref executions);
        public long CacheHits => Interlocked.Read(ref cacheHits);

        /// <summary>Snippet assemblies loaded into a host that cannot unload them (Mono). They stay until the domain goes.</summary>
        public long LoadedAssemblies => Interlocked.Read(ref loadedAssemblies);
    }

    /// <summary>The compiled form of a request. Produced by <see cref="CodeRunner.Prepare"/> (any thread).</summary>
    public sealed class PreparedSnippet
    {
        internal PreparedSnippet(ExecuteRequest request, ExecuteResult? failure, CodeRunner.CacheEntry? entry, bool cacheHit, long compileMs)
        {
            Request = request;
            Failure = failure;
            Entry = entry;
            CacheHit = cacheHit;
            CompileMs = compileMs;
        }

        internal ExecuteRequest Request { get; }
        internal ExecuteResult? Failure { get; }
        internal CodeRunner.CacheEntry? Entry { get; }
        public bool Succeeded => Failure == null;
        public bool CacheHit { get; }
        public long CompileMs { get; }
    }

    /// <summary>
    /// Engine-agnostic pipeline: includes → compile (auto-import retry) → load → invoke → convert → unload.
    /// <see cref="Prepare"/> is thread-safe and can run on a background thread; <see cref="Run"/> / <see cref="RunAsync"/>
    /// invoke the snippet on the calling thread (the host's main thread) and must not run concurrently.
    /// </summary>
    public sealed class CodeRunner
    {
        public const string SnippetAssemblyPrefix = "CodeExec.Snippet";
        const int MaxImportRounds = 3;
        static int _assemblyCounter;

        readonly ICSharpCompiler _compiler;
        readonly CodeRunnerOptions _options;
        readonly IAssemblyHost _host;
        readonly IReferenceProvider _references;
        readonly object _cacheLock = new object();
        readonly Dictionary<string, LinkedListNode<KeyValuePair<string, CacheEntry>>> _cache = new Dictionary<string, LinkedListNode<KeyValuePair<string, CacheEntry>>>(StringComparer.Ordinal);
        readonly LinkedList<KeyValuePair<string, CacheEntry>> _lru = new LinkedList<KeyValuePair<string, CacheEntry>>();

        public CodeRunner(ICSharpCompiler compiler, CodeRunnerOptions? options = null)
        {
            _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
            _options = options ?? new CodeRunnerOptions();
            _host = _options.Host ?? AssemblyHosts.CreateDefault();
            _references = _options.References ?? new LoadedAssembliesReferenceProvider();
        }

        public IAssemblyHost Host => _host;
        public string CompilerVersion => _compiler.CompilerVersion;
        public CodeRunnerStats Stats { get; } = new CodeRunnerStats();

        public ExecuteResult Execute(ExecuteRequest request) => Run(Prepare(request));

        public Task<ExecuteResult> ExecuteAsync(ExecuteRequest request, CancellationToken cancellationToken = default) =>
            RunAsync(Prepare(request), cancellationToken);

        public PreparedSnippet Prepare(ExecuteRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            var stopwatch = Stopwatch.StartNew();
            PreparedSnippet Fail(ErrorKind kind, string error) =>
                new PreparedSnippet(request, ExecuteResult.Fail(kind, error), null, false, stopwatch.ElapsedMilliseconds);

            if (string.IsNullOrWhiteSpace(request.Code))
                return Fail(ErrorKind.InvalidRequest, "code is empty");
            if (string.IsNullOrWhiteSpace(request.EntryType) && !string.IsNullOrWhiteSpace(request.EntryMethod))
                return Fail(ErrorKind.InvalidRequest, "entry method given without entry type");

            List<ParameterDeclaration> parameters;
            List<SourceFile> includes;
            try
            {
                parameters = request.Parameters.Select(p => new ParameterDeclaration(p.Name, TypeNames.ToCSharp(p.Type))).ToList();
                includes = IncludeResolver.Read(request.Include, _options.IncludeRoot);
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is UnauthorizedAccessException)
            {
                return Fail(ErrorKind.InvalidRequest, e.Message);
            }

            var usings = _options.DefaultUsings.Concat(request.Usings).Select(NormalizeUsing).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            var compileRequest = new CompileRequest
            {
                Code = request.Code,
                Includes = includes,
                ReferencePaths = _references.GetReferencePaths(),
                Parameters = parameters,
                EntryType = string.IsNullOrWhiteSpace(request.EntryType) ? null : request.EntryType,
                EntryMethod = string.IsNullOrWhiteSpace(request.EntryMethod) ? null : request.EntryMethod,
            };

            var key = CacheKey(compileRequest, usings, request.AutoImport);
            var cached = FromCache(key);
            if (cached != null)
            {
                Interlocked.Increment(ref Stats.cacheHits);
                return new PreparedSnippet(request, null, cached, true, stopwatch.ElapsedMilliseconds);
            }

            var autoImported = new List<string>();
            var ambiguous = new List<CodeDiagnostic>();
            CompileResult compiled;
            for (var round = 0; ; round++)
            {
                compileRequest.Usings = usings;
                compileRequest.AssemblyName = SnippetAssemblyPrefix + Interlocked.Increment(ref _assemblyCounter) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                compiled = _compiler.Compile(compileRequest);
                if (compiled.Success || !request.AutoImport || compiled.UnresolvedNames.Count == 0 || round == MaxImportRounds)
                    break;

                var added = new List<string>();
                ambiguous.Clear();
                foreach (var pair in TypeIndex.FindNamespaces(compiled.UnresolvedNames))
                {
                    if (pair.Value.Count == 1)
                    {
                        if (!usings.Contains(pair.Value[0]))
                            added.Add(pair.Value[0]);
                    }
                    else
                    {
                        ambiguous.Add(new CodeDiagnostic
                        {
                            Id = "CE0001",
                            File = "snippet.cs",
                            Message = $"'{pair.Key}' exists in several namespaces: {string.Join(", ", pair.Value)}. Add the right using.",
                        });
                    }
                }
                if (added.Count == 0)
                    break;
                usings.AddRange(added);
                autoImported.AddRange(added);
            }

            if (!compiled.Success || compiled.Image == null)
            {
                var failure = ExecuteResult.Fail(ErrorKind.Compile, "Compilation failed");
                failure.Diagnostics = compiled.Diagnostics.Concat(ambiguous).ToList();
                failure.AutoImported = autoImported;
                failure.CompileMs = stopwatch.ElapsedMilliseconds;
                return new PreparedSnippet(request, failure, null, false, failure.CompileMs);
            }

            var entry = new CacheEntry(compiled.Image, compiled.Symbols, compiled.EntryType, compiled.EntryMethod, autoImported);
            ToCache(key, entry);
            return new PreparedSnippet(request, null, entry, false, stopwatch.ElapsedMilliseconds);
        }

        /// <summary>Invokes a prepared snippet synchronously. An unfinished asynchronous result is a <see cref="ErrorKind.Timeout"/>: use <see cref="RunAsync"/>.</summary>
        public ExecuteResult Run(PreparedSnippet prepared)
        {
            if (prepared.Failure != null)
                return prepared.Failure;
            var run = Start(prepared);
            if (run.Raw is Task task && !task.IsCompleted)
                run.Fail(ErrorKind.Timeout, "The snippet is asynchronous and did not finish synchronously; run it with RunAsync.");
            return run.Finish();
        }

        /// <summary>Invokes a prepared snippet; an asynchronous result is awaited (continuations stay on the caller's context).</summary>
        public async Task<ExecuteResult> RunAsync(PreparedSnippet prepared, CancellationToken cancellationToken = default)
        {
            if (prepared.Failure != null)
                return prepared.Failure;
            var run = Start(prepared);
            if (run.Raw is Task task && !task.IsCompleted)
            {
                var timeout = prepared.Request.TimeoutMs > 0 ? prepared.Request.TimeoutMs : Timeout.Infinite;
                var finished = await Task.WhenAny(task, Task.Delay(timeout, cancellationToken));
                if (finished != task)
                    run.Fail(ErrorKind.Timeout, cancellationToken.IsCancellationRequested
                        ? "Canceled while waiting for the snippet's result; the snippet may still be running."
                        : $"No result within {prepared.Request.TimeoutMs} ms; the snippet may still be running.");
            }
            return run.Finish();
        }

        Execution Start(PreparedSnippet prepared)
        {
            Interlocked.Increment(ref Stats.executions);
            var entry = prepared.Entry!;
            var run = new Execution(this, prepared);
            try
            {
                run.Loaded = entry.Loaded ?? _host.Load(entry.Image, entry.Symbols, entry.EntryType);
                if (!_host.SupportsUnload && entry.Loaded == null)
                {
                    entry.Loaded = run.Loaded; // Mono cannot unload: keep it so identical requests reuse it
                    Interlocked.Increment(ref Stats.loadedAssemblies);
                }

                var count = prepared.Request.Parameters.Count;
                var type = run.Loaded.Assembly.GetType(entry.EntryType, throwOnError: false);
                var method = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == entry.EntryMethod && m.GetParameters().Length == count);
                if (method == null)
                {
                    run.Fail(ErrorKind.EntryPointNotFound, $"Static method '{entry.EntryType}.{entry.EntryMethod}' with {count} parameter(s) not found.");
                    return run;
                }

                run.BeginCapture(_options.LogCaptures);
                run.Raw = method.Invoke(null, prepared.Request.Parameters.Select(p => p.Value).ToArray());
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                run.Fail(ErrorKind.Runtime, FormatException(e.InnerException));
            }
            catch (Exception e) when (e is ArgumentException || e is BadImageFormatException || e is FileLoadException || e is TypeLoadException)
            {
                run.Fail(ErrorKind.Runtime, FormatException(e));
            }
            return run;
        }

        internal static string FormatException(Exception exception)
        {
            while ((exception is TargetInvocationException || exception is AggregateException) && exception.InnerException != null)
                exception = exception.InnerException;
            var sb = new StringBuilder(exception.GetType().FullName).Append(": ").Append(exception.Message);
            foreach (var line in (exception.StackTrace ?? "").Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                // Frames below the snippet (reflection, the runner) are noise for the agent.
                if (trimmed.Contains("System.RuntimeMethodHandle") || trimmed.Contains("System.Reflection.") || trimmed.Contains("com.IvanMurzak.CodeExec.CodeRunner"))
                    break;
                if (trimmed.Trim().Length > 0)
                    sb.Append('\n').Append(trimmed);
            }
            return sb.ToString();
        }

        static string NormalizeUsing(string value)
        {
            var u = (value ?? "").Trim();
            if (u.StartsWith("using ", StringComparison.Ordinal))
                u = u.Substring(6).Trim();
            return u.TrimEnd(';').Trim();
        }

        static string CacheKey(CompileRequest request, IEnumerable<string> usings, bool autoImport)
        {
            var sb = new StringBuilder();
            void Add(string? s) => sb.Append(s ?? "").Append('\0');
            Add(request.Code);
            Add(request.EntryType);
            Add(request.EntryMethod);
            Add(autoImport ? "1" : "0");
            foreach (var u in usings) Add(u);
            Add("|");
            foreach (var p in request.Parameters) { Add(p.TypeName); Add(p.Name); }
            Add("|");
            foreach (var f in request.Includes) { Add(f.Path); Add(f.Text); }
            Add("|");
            foreach (var r in request.ReferencePaths) Add(r);
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        CacheEntry? FromCache(string key)
        {
            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(key, out var node))
                    return null;
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Value;
            }
        }

        void ToCache(string key, CacheEntry entry)
        {
            if (_options.CacheCapacity <= 0)
                return;
            lock (_cacheLock)
            {
                if (_cache.ContainsKey(key))
                    return;
                _cache[key] = _lru.AddFirst(new KeyValuePair<string, CacheEntry>(key, entry));
                while (_cache.Count > _options.CacheCapacity)
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _cache.Remove(last.Value.Key);
                }
            }
        }

        internal sealed class CacheEntry
        {
            public CacheEntry(byte[] image, byte[]? symbols, string entryType, string entryMethod, IReadOnlyList<string> autoImported)
            {
                Image = image;
                Symbols = symbols;
                EntryType = entryType;
                EntryMethod = entryMethod;
                AutoImported = autoImported;
            }

            public byte[] Image { get; }
            public byte[]? Symbols { get; }
            public string EntryType { get; }
            public string EntryMethod { get; }
            public IReadOnlyList<string> AutoImported { get; }

            /// <summary>Set only for hosts that cannot unload (Mono): the assembly is loaded once and reused.</summary>
            public ILoadedAssembly? Loaded { get; set; }
        }

        /// <summary>State of one invocation, from load to unload.</summary>
        sealed class Execution
        {
            readonly CodeRunner _runner;
            readonly PreparedSnippet _prepared;
            readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            readonly List<LogEntry> _logs = new List<LogEntry>();
            readonly List<IDisposable> _captures = new List<IDisposable>();
            ErrorKind _errorKind;
            string? _error;

            public Execution(CodeRunner runner, PreparedSnippet prepared)
            {
                _runner = runner;
                _prepared = prepared;
            }

            public ILoadedAssembly? Loaded { get; set; }
            public object? Raw { get; set; }

            public void Fail(ErrorKind kind, string error)
            {
                if (_errorKind != ErrorKind.None)
                    return;
                _errorKind = kind;
                _error = error;
            }

            public void BeginCapture(IList<ILogCapture> captures)
            {
                foreach (var capture in captures)
                    _captures.Add(capture.Begin(entry => { lock (_logs) _logs.Add(entry); }));
            }

            public ExecuteResult Finish()
            {
                object? value = null;
                try
                {
                    if (_errorKind == ErrorKind.None)
                        value = _runner._options.ValueConverter(Unwrap(Raw));
                }
                catch (Exception e)
                {
                    Fail(ErrorKind.Runtime, FormatException(e));
                }
                finally
                {
                    for (var i = _captures.Count - 1; i >= 0; i--)
                        _captures[i].Dispose();
                    if (_runner._host.SupportsUnload)
                        Loaded?.Dispose();
                    Raw = null;
                }

                List<LogEntry> logs;
                lock (_logs) logs = new List<LogEntry>(_logs);
                var entry = _prepared.Entry!;
                return new ExecuteResult
                {
                    Success = _errorKind == ErrorKind.None,
                    Value = value,
                    Logs = logs,
                    ErrorKind = _errorKind,
                    Error = _error,
                    AutoImported = entry.AutoImported,
                    CompileMs = _prepared.CompileMs,
                    ExecuteMs = _stopwatch.ElapsedMilliseconds,
                    CacheHit = _prepared.CacheHit,
                };
            }

            /// <summary>A finished Task → its result (or its exception); anything else as is.</summary>
            object? Unwrap(object? raw)
            {
                if (!(raw is Task task))
                    return raw;
                if (task.IsFaulted)
                    throw new TargetInvocationException(task.Exception!.InnerException ?? task.Exception);
                if (task.IsCanceled)
                    throw new OperationCanceledException("The snippet's task was canceled.");
                var type = task.GetType();
                if (!type.IsGenericType)
                    return null;
                var result = type.GetProperty("Result")!.GetValue(task);
                // async Task (no value) is surfaced by the runtime as Task<VoidTaskResult>.
                return result != null && result.GetType().FullName == "System.Threading.Tasks.VoidTaskResult" ? null : result;
            }
        }
    }
}
