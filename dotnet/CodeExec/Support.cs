using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace com.IvanMurzak.CodeExec
{
    /// <summary>Simple type name → namespaces that define a public type with that name (spec §3.3).</summary>
    public static class TypeIndex
    {
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> FindNamespaces(IEnumerable<string> simpleNames)
        {
            var wanted = new HashSet<string>(simpleNames, StringComparer.Ordinal);
            var found = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || (assembly.GetName().Name ?? "").StartsWith(CodeRunner.SnippetAssemblyPrefix, StringComparison.Ordinal))
                    continue;
                Type[] types;
                try { types = assembly.GetExportedTypes(); }
                catch (Exception) { continue; } // assemblies whose dependencies are missing cannot list their types
                foreach (var type in types)
                {
                    if (type.IsNested || string.IsNullOrEmpty(type.Namespace))
                        continue;
                    var name = type.Name;
                    var tick = name.IndexOf('`');
                    if (tick > 0)
                        name = name.Substring(0, tick);
                    if (!wanted.Contains(name))
                        continue;
                    if (!found.TryGetValue(name, out var namespaces))
                        found[name] = namespaces = new SortedSet<string>(StringComparer.Ordinal);
                    namespaces.Add(type.Namespace!);
                }
            }
            return found.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList(), StringComparer.Ordinal);
        }
    }

    static class IncludeResolver
    {
        public static List<SourceFile> Read(IReadOnlyList<string> include, string root)
        {
            var files = new List<SourceFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in include)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    throw new ArgumentException("include entry is empty");
                var full = Path.GetFullPath(Path.IsPathRooted(entry) ? entry : Path.Combine(root, entry));
                IEnumerable<string> paths;
                if (Directory.Exists(full))
                    paths = Directory.GetFiles(full, "*.cs", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal);
                else if (File.Exists(full))
                    paths = new[] { full };
                else
                    throw new FileNotFoundException("include not found: " + entry);
                foreach (var path in paths)
                {
                    if (seen.Add(path))
                        files.Add(new SourceFile(Relative(root, path), File.ReadAllText(path)));
                }
            }
            return files;
        }

        static string Relative(string root, string path)
        {
            var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var relative = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : path;
            return relative.Replace('\\', '/');
        }
    }

    /// <summary>Formats a runtime <see cref="Type"/> as C# source: <c>global::System.Collections.Generic.List&lt;global::System.Int32&gt;</c>.</summary>
    public static class TypeNames
    {
        public static string ToCSharp(Type type)
        {
            if (type.IsByRef || type.IsPointer || type.IsGenericParameter)
                throw new ArgumentException("unsupported parameter type: " + type);
            if (type.IsArray)
                return ToCSharp(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";

            var arguments = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
            var chain = new List<Type>();
            for (var t = type; t != null; t = t.DeclaringType)
                chain.Insert(0, t);

            var sb = new StringBuilder("global::");
            if (!string.IsNullOrEmpty(chain[0].Namespace))
                sb.Append(chain[0].Namespace).Append('.');
            var used = 0;
            for (var i = 0; i < chain.Count; i++)
            {
                if (i > 0)
                    sb.Append('.');
                var name = chain[i].Name;
                var tick = name.IndexOf('`');
                var arity = 0;
                if (tick > 0)
                {
                    arity = int.Parse(name.Substring(tick + 1));
                    name = name.Substring(0, tick);
                }
                sb.Append(name);
                if (arity > 0)
                {
                    sb.Append('<').Append(string.Join(", ", arguments.Skip(used).Take(arity).Select(ToCSharp))).Append('>');
                    used += arity;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Collects what a snippet writes while it runs. Hosts add their own (Unity log, Godot print, ...).</summary>
    public interface ILogCapture
    {
        IDisposable Begin(Action<LogEntry> sink);
    }

    /// <summary>Redirects <see cref="Console.Out"/> / <see cref="Console.Error"/> for the duration of a run.</summary>
    public sealed class ConsoleLogCapture : ILogCapture
    {
        public IDisposable Begin(Action<LogEntry> sink)
        {
            var output = Console.Out;
            var error = Console.Error;
            var outWriter = new LineWriter(line => sink(new LogEntry("info", line)));
            var errWriter = new LineWriter(line => sink(new LogEntry("error", line)));
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            return new Restore(() =>
            {
                outWriter.Flush();
                errWriter.Flush();
                Console.SetOut(output);
                Console.SetError(error);
            });
        }

        sealed class LineWriter : TextWriter
        {
            readonly Action<string> _line;
            readonly StringBuilder _buffer = new StringBuilder();
            public LineWriter(Action<string> line) { _line = line; }
            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
            {
                if (value == '\n')
                {
                    EmitLine();
                    return;
                }
                if (value != '\r')
                    _buffer.Append(value);
            }

            public override void Flush()
            {
                if (_buffer.Length > 0)
                    EmitLine();
            }

            void EmitLine()
            {
                _line(_buffer.ToString());
                _buffer.Clear();
            }
        }
    }

    sealed class Restore : IDisposable
    {
        Action? _action;
        public Restore(Action action) { _action = action; }

        public void Dispose()
        {
            _action?.Invoke();
            _action = null;
        }
    }
}
