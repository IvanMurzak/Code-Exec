using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using com.IvanMurzak.CodeExec;
using com.IvanMurzak.CodeExec.Roslyn;
using Xunit;

namespace CodeExecTests.Game
{
    public static class GameScore { public static int Value = 77; }
}

namespace CodeExecTests.AmbA
{
    public class Twin { }
}

namespace CodeExecTests.AmbB
{
    public class Twin { }
}

namespace CodeExecTests
{
    public class RunnerTests
    {
        [Fact]
        public void AutoImportsATypeOfTheHostAssembly()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "GameScore.Value" });
            Assert.True(result.Success, result.Error);
            Assert.Equal("77", result.Value);
            Assert.Equal(new[] { "CodeExecTests.Game" }, result.AutoImported);
        }

        [Fact]
        public void AmbiguousNameIsNotGuessed()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "new Twin()" });
            Assert.Equal(ErrorKind.Compile, result.ErrorKind);
            var hint = Assert.Single(result.Diagnostics, d => d.Id == "CE0001");
            Assert.Contains("CodeExecTests.AmbA", hint.Message);
            Assert.Contains("CodeExecTests.AmbB", hint.Message);
            Assert.Empty(result.AutoImported);
        }

        [Fact]
        public void IdenticalRequestHitsTheCache()
        {
            var runner = Fixtures.Runner();
            var first = runner.Execute(new ExecuteRequest { Code = "6 * 7" });
            var second = runner.Execute(new ExecuteRequest { Code = "6 * 7" });
            Assert.False(first.CacheHit);
            Assert.True(second.CacheHit);
            Assert.Equal("42", second.Value);
            Assert.Equal(1, runner.Stats.CacheHits);
        }

        [Fact]
        public void NonCollectibleHostLoadsAnIdenticalSnippetOnce()
        {
            var runner = Fixtures.Runner(configure: o => o.Host = new DefaultAssemblyHost());
            for (var i = 0; i < 3; i++)
                Assert.Equal("3", runner.Execute(new ExecuteRequest { Code = "1 + 2" }).Value);
            Assert.Equal(1, runner.Stats.LoadedAssemblies);
        }

        [Fact]
        public void PassesParameters()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "a * names.Count",
                Parameters = new[]
                {
                    new SnippetParameter("a", typeof(int), 5),
                    new SnippetParameter("names", typeof(List<string>), new List<string> { "x", "y" }),
                },
            });
            Assert.True(result.Success, result.Error + string.Join(" | ", result.Diagnostics));
            Assert.Equal("10", result.Value);
        }

        [Fact]
        public void ValuesCanBeSuppliedAtRunTime()
        {
            // A host compiles off its main thread with parameter TYPES only and binds the values when it runs.
            var runner = Fixtures.Runner();
            var prepared = runner.Prepare(new ExecuteRequest { Code = "a * 2", Parameters = new[] { new SnippetParameter("a", typeof(int), null) } });
            Assert.Equal("42", runner.Run(prepared.WithValues(new object?[] { 21 })).Value);
            Assert.Equal("10", runner.Run(prepared.WithValues(new object?[] { 5 })).Value);
            Assert.Equal(ErrorKind.InvalidRequest, runner.Run(prepared.WithValues(new object?[] { 1, 2 })).ErrorKind);
            // Same conversion as Execute: a value that does not fit is a Runtime error, null becomes default(int).
            Assert.Equal(ErrorKind.Runtime, runner.Run(prepared.WithValues(new object?[] { "21" })).ErrorKind);
            Assert.Equal("0", runner.Run(prepared.WithValues(new object?[] { null })).Value);
        }

        [Fact]
        public void MostSpecificOverloadIsChosen()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "public static class Entry { public static string Run(object v) => \"object\"; public static string Run(string v) => \"string\"; public static string Run(int v) => \"int\"; }",
                EntryType = "Entry",
                EntryMethod = "Run",
                Parameters = new[] { new SnippetParameter("v", typeof(string), "x") },
            });
            Assert.Equal("string", result.Value);
        }

        [Theory]
        [InlineData("public static string Run(object a, string b) => \"1\"; public static string Run(string a, object b) => \"2\";")]
        [InlineData("public static string Run(int a, int b) => \"1\"; public static string Run(long a, bool b) => \"2\";")]
        public void OverloadsThatAreAmbiguousOrDoNotFitAreNotGuessed(string members)
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "public static class Entry { " + members + " }",
                EntryType = "Entry",
                EntryMethod = "Run",
                Parameters = new[] { new SnippetParameter("a", typeof(string), "x"), new SnippetParameter("b", typeof(string), "y") },
            });
            Assert.Equal(ErrorKind.EntryPointNotFound, result.ErrorKind);
        }

        [Fact]
        public void NonCollectibleEntryEvictedBeforeItsFirstRunIsPinned()
        {
            var runner = Fixtures.Runner(configure: o => { o.Host = new DefaultAssemblyHost(); o.CacheCapacity = 1; });
            var first = runner.Prepare(new ExecuteRequest { Code = "100 + 1" });
            runner.Prepare(new ExecuteRequest { Code = "100 + 2" }); // evicts the first entry before it ever ran
            Assert.Equal("101", runner.Run(first).Value);
            var again = runner.Prepare(new ExecuteRequest { Code = "100 + 1" });
            Assert.True(again.CacheHit); // reused, not compiled and loaded a second time
            Assert.Equal(1, runner.Stats.LoadedAssemblies);
        }

        [Fact]
        public void FullSourceModeReceivesParameters()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "public static class Entry { public static int Run(int v) => v + 1; }",
                EntryType = "Entry",
                EntryMethod = "Run",
                Parameters = new[] { new SnippetParameter("v", typeof(int), 41) },
            });
            Assert.Equal("42", result.Value);
        }

        [Fact]
        public async Task AsyncResultTimesOut()
        {
            var result = await Fixtures.Runner().ExecuteAsync(new ExecuteRequest { Code = "await Task.Delay(5000);\n1", TimeoutMs = 100 });
            Assert.Equal(ErrorKind.Timeout, result.ErrorKind);
        }

        [Fact]
        public void SynchronousRunRejectsAnUnfinishedTask()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "await Task.Delay(2000);\n1" });
            Assert.Equal(ErrorKind.Timeout, result.ErrorKind);
            Assert.Contains("RunAsync", result.Error);
        }

        [Fact]
        public async Task PrepareOnABackgroundThreadThenRun()
        {
            var runner = Fixtures.Runner();
            var prepared = await Task.Run(() => runner.Prepare(new ExecuteRequest { Code = "Environment.CurrentManagedThreadId" }));
            var result = runner.Run(prepared);
            Assert.Equal(Environment.CurrentManagedThreadId.ToString(), result.Value);
        }

        [Fact]
        public void ErrorLogsAreCaptured()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "Console.Error.WriteLine(\"bad\");\nConsole.Write(\"no newline\");" });
            Assert.Contains(result.Logs, l => l.Level == "error" && l.Message == "bad");
            Assert.Contains(result.Logs, l => l.Level == "info" && l.Message == "no newline");
        }

        [Fact]
        public void CustomValueConverterRunsBeforeUnload()
        {
            var result = Fixtures.Runner(configure: o => o.ValueConverter = v => v == null ? null : v.GetType().Name + ":" + v)
                .Execute(new ExecuteRequest { Code = "class Point { public override string ToString() => \"(1,2)\"; }\nnew Point()" });
            Assert.Equal("Point:(1,2)", result.Value);
        }

        [Fact]
        public void VoidTailIsRecognisedUnderAnyUiCulture()
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                // Roslyn's messages are localized; detection must not depend on their text.
                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
                var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "var list = new List<int>();\nlist.Add(1)" });
                Assert.True(result.Success, result.Error + string.Join(" | ", result.Diagnostics));
                Assert.Null(result.Value);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = culture;
            }
        }

        [Fact]
        public void UninvokableEntryIsARuntimeErrorAndRestoresTheConsole()
        {
            var output = Console.Out;
            var result = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "public static class E { public static T Run<T>() => default; }",
                EntryType = "E",
                EntryMethod = "Run",
            });
            Assert.Equal(ErrorKind.Runtime, result.ErrorKind);
            Assert.Same(output, Console.Out);
        }

        [Fact]
        public void FullSourceModeAutoImportsButGetsNoDefaultUsings()
        {
            // Not a default using: auto-import adds it (spec §3.2, §3.3).
            var imported = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "public static class E { public static object Run() => new StringBuilder(\"sb\"); }",
                EntryType = "E",
                EntryMethod = "Run",
            });
            Assert.True(imported.Success, imported.Error);
            Assert.Equal("sb", imported.Value);
            Assert.Equal(new[] { "System.Text" }, imported.AutoImported);

            // The request's usings apply; the host's defaults (System.Linq here) do not, and CE0003 says so.
            var withUsing = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "public static class E { public static object Run() => Enumerable.Range(0, 3).Count(); }",
                EntryType = "E",
                EntryMethod = "Run",
                Usings = new[] { "System.Linq" },
            });
            Assert.True(withUsing.Success, withUsing.Error);
            var unknown = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "public static class E { public static object Run() => NoSuchType.Value; }",
                EntryType = "E",
                EntryMethod = "Run",
            });
            Assert.Equal(ErrorKind.Compile, unknown.ErrorKind);
            Assert.Contains(unknown.Diagnostics, d => d.Id == "CE0003" && d.Message.Contains("System.Linq") && d.Message.Contains("'NoSuchType'"));
            Assert.Contains(unknown.Diagnostics, d => d.Id == "CS0103" && d.File == "snippet.cs" && d.Line == 1);
            var badUsing = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "public static class E { public static int Run() => 1; }",
                EntryType = "E",
                EntryMethod = "Run",
                Usings = new[] { "No.Such.Namespace" },
            });
            Assert.Contains(badUsing.Diagnostics, d => d.File == "usings" && d.Message.StartsWith("using 'No.Such.Namespace'"));
        }

        [Theory]
        [InlineData("var a = 1;\n    var b = undefinedThing;", 2, 13)]
        [InlineData("var a = 1;\na + undefinedThing", 2, 5)]
        [InlineData("[System.Diagnostics.DebuggerStepThrough] int F() => undefinedThing;\nF()", 1, 53)]
        public void DiagnosticsKeepTheAgentsColumns(string code, int line, int column)
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = code, AutoImport = false });
            Assert.Contains(result.Diagnostics, d => d.Id == "CS0103" && d.File == "snippet.cs" && d.Line == line && d.Column == column);
        }

        [Fact]
        public void ExternAliasGoesBeforeTheUsings()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "extern alias Other;\n1" });
            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CS0439");
        }

#if NET8_0_OR_GREATER
        [Theory]
        [InlineData("static async IAsyncEnumerable<int> G() { await Task.Yield(); yield return 2; }\nint s = 0;\nawait foreach (var x in G()) s += x;\ns", "2")]
        [InlineData("long n;\nawait using (var d = new System.IO.MemoryStream()) { n = d.Length; }\nn", "0")]
        public async Task AwaitForeachAndAwaitUsingMakeTheSnippetAsync(string code, string expected)
        {
            var result = await Fixtures.Runner().ExecuteAsync(new ExecuteRequest { Code = code });
            Assert.True(result.Success, result.Error + string.Join(" | ", result.Diagnostics));
            Assert.Equal(expected, result.Value);
        }
#endif

        [Fact]
        public void EntryMethodWithoutTypeIsInvalid()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "1", EntryMethod = "Run" });
            Assert.Equal(ErrorKind.InvalidRequest, result.ErrorKind);
        }

        [Fact]
        public void LocationResolverMakesInMemoryAssembliesVisible()
        {
            // Build a tiny library on disk, then load it from BYTES: its Location is empty, as with Godot's game assembly.
            var name = "InMemoryLib" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var dll = BuildLibrary(name, "InMemory.Answer", "namespace InMemory { public static class Answer { public static int Get() => 99; } }");
            var loaded = Assembly.Load(File.ReadAllBytes(dll));
            Assert.Equal("", loaded.Location);

            var blind = Fixtures.Runner().Execute(new ExecuteRequest { Code = "InMemory.Answer.Get()" });
            Assert.Equal(ErrorKind.Compile, blind.ErrorKind);

            var seeing = Fixtures.Runner(configure: o => o.References = new LoadedAssembliesReferenceProvider(a => a.GetName().Name == name ? dll : null))
                .Execute(new ExecuteRequest { Code = "InMemory.Answer.Get()" });
            Assert.True(seeing.Success, seeing.Error + string.Join(" | ", seeing.Diagnostics));
            Assert.Equal("99", seeing.Value);
        }

        /// <summary>Compiles <paramref name="code"/> to <c>name.dll</c> in a fresh temp folder and returns its path.</summary>
        static string BuildLibrary(string name, string type, string code)
        {
            var dir = Path.Combine(Path.GetTempPath(), "codeexec-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var build = new RoslynCSharpCompiler().Compile(new CompileRequest
            {
                Code = code,
                EntryType = type,
                AssemblyName = name,
                ReferencePaths = new LoadedAssembliesReferenceProvider().GetReferencePaths(),
            });
            Assert.True(build.Success, string.Join(" | ", build.Diagnostics));
            var dll = Path.Combine(dir, name + ".dll");
            File.WriteAllBytes(dll, build.Image!);
            return dll;
        }

        [Fact]
        public void AutoImportNeverGuessesARuntimeInternalNamespace()
        {
            // CoreLib (CoreCLR) has a public Internal.Console that writes past Console.SetOut: only System.Console counts.
            Assert.Equal(new[] { "System" }, TypeIndex.FindNamespaces(new[] { "Console" })["Console"]);

            var result = Fixtures.Runner(configure: o => o.DefaultUsings = Array.Empty<string>())
                .Execute(new ExecuteRequest { Code = "Console.WriteLine(\"captured\");\n1" });
            Assert.True(result.Success, result.Error + string.Join(" | ", result.Diagnostics));
            Assert.Equal(new[] { "System" }, result.AutoImported);
            Assert.Contains(result.Logs, l => l.Message == "captured");
        }

#if NET8_0_OR_GREATER
        [Fact]
        public void ReferencesIncludeTheFrameworkFacades()
        {
            var files = new LoadedAssembliesReferenceProvider().GetReferencePaths().Select(Path.GetFileName).ToList();
            foreach (var facade in new[] { "System.Runtime.dll", "System.Console.dll", "System.Collections.dll", "System.Linq.dll" })
                Assert.Contains(facade, files, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Godot's situation: the game assembly lives in its own collectible context, loaded from memory, while a host's
        /// default-context resolver answers a miss with a fresh copy from disk. The snippet must see the LIVE assembly.
        /// </summary>
        [Fact]
        public void CollectibleHostBindsToTheLiveAssemblyNotADiskCopy()
        {
            var name = "LiveGame" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var dll = BuildLibrary(name, "LiveGame.Score", "namespace LiveGame { public static class Score { public static int Value = 1; } }");
            var gameContext = new System.Runtime.Loader.AssemblyLoadContext("TestGame", isCollectible: true);
            Func<System.Runtime.Loader.AssemblyLoadContext, AssemblyName, Assembly?> diskCopy = (context, requested) =>
                requested.Name == name ? context.LoadFromAssemblyPath(dll) : null;
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += diskCopy;
            try
            {
                var live = gameContext.LoadFromStream(new MemoryStream(File.ReadAllBytes(dll))); // like Godot: no Location
                live.GetType("LiveGame.Score")!.GetField("Value")!.SetValue(null, 5);

                var result = Fixtures.Runner(configure: o => o.References = new LoadedAssembliesReferenceProvider(a => a == live ? dll : null))
                    .Execute(new ExecuteRequest { Code = "LiveGame.Score.Value" });
                Assert.True(result.Success, result.Error + string.Join(" | ", result.Diagnostics));
                Assert.Equal("5", result.Value);
                Assert.Single(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == name);
            }
            finally
            {
                System.Runtime.Loader.AssemblyLoadContext.Default.Resolving -= diskCopy;
                gameContext.Unload();
            }
        }

        [Fact]
        public void CollectibleHostBindsToTheReferencedIdentityFirst()
        {
            // Two loaded versions of one name; the snippet compiles against 2.0, which loaded FIRST.
            var name = "Versioned" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string Version(string v) =>
                $"[assembly: System.Reflection.AssemblyVersion(\"{v}.0.0.0\")] namespace Versioned {{ public static class Tag {{ public static string Value = \"v{v}\"; }} }}";
            var v2 = BuildLibrary(name, "Versioned.Tag", Version("2"));
            var v1 = BuildLibrary(name, "Versioned.Tag", Version("1"));
            var first = new System.Runtime.Loader.AssemblyLoadContext("TestV2", isCollectible: true);
            var second = new System.Runtime.Loader.AssemblyLoadContext("TestV1", isCollectible: true);
            try
            {
                var loadedV2 = first.LoadFromStream(new MemoryStream(File.ReadAllBytes(v2)));
                second.LoadFromStream(new MemoryStream(File.ReadAllBytes(v1)));

                var result = Fixtures.Runner(configure: o => o.References = new LoadedAssembliesReferenceProvider(a => a == loadedV2 ? v2 : null))
                    .Execute(new ExecuteRequest { Code = "Versioned.Tag.Value" });
                Assert.True(result.Success, result.Error + string.Join(" | ", result.Diagnostics));
                Assert.Equal("v2", result.Value);
            }
            finally
            {
                first.Unload();
                second.Unload();
            }
        }

        static string VersionedTag(string version, string value, string ns = "Versioned") =>
            $"[assembly: System.Reflection.AssemblyVersion(\"{version}.0.0.0\")] namespace {ns} {{ public static class Tag {{ public static string Value = \"{value}\"; }} }}";

        static ExecuteResult RunTag(Func<Assembly, string?> locate, string ns = "Versioned") =>
            Fixtures.Runner(configure: o => o.References = new LoadedAssembliesReferenceProvider(locate))
                .Execute(new ExecuteRequest { Code = ns + ".Tag.Value" });

        [Fact]
        public void CollectibleHostNeverBindsAnOlderVersionByName()
        {
            // Only 1.0 is loaded, the snippet compiles against 2.0: the name fallback must not bind 1.0. The 2.0 the
            // default context then loads stays for the process: its own namespace keeps it out of the other tests' way.
            var name = "OlderPick" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var v2 = BuildLibrary(name, "OlderPick.Tag", VersionedTag("2", "v2", "OlderPick"));
            var v1 = BuildLibrary(name, "OlderPick.Tag", VersionedTag("1", "v1", "OlderPick"));
            var context = new System.Runtime.Loader.AssemblyLoadContext("TestOld", isCollectible: true);
            Func<System.Runtime.Loader.AssemblyLoadContext, AssemblyName, Assembly?> compatible = (ctx, requested) =>
                requested.Name == name ? ctx.LoadFromAssemblyPath(v2) : null;
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += compatible;
            try
            {
                var old = context.LoadFromStream(new MemoryStream(File.ReadAllBytes(v1)));
                var result = RunTag(a => a == old ? v2 : null, "OlderPick");
                Assert.True(result.Success, result.Error + string.Join(" | ", result.Diagnostics));
                Assert.Equal("v2", result.Value);
            }
            finally
            {
                System.Runtime.Loader.AssemblyLoadContext.Default.Resolving -= compatible;
                context.Unload();
            }
        }

        [Fact]
        public void CollectibleHostSkipsAnUnloadingCopy()
        {
            // A host reload: the old copy's context is unloading but still listed (we hold it), and it loaded LAST.
            var name = "Versioned" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var dll = BuildLibrary(name, "Versioned.Tag", VersionedTag("1", "init"));
            var current = new System.Runtime.Loader.AssemblyLoadContext("TestCurrent", isCollectible: true);
            var leaving = new System.Runtime.Loader.AssemblyLoadContext("TestLeaving", isCollectible: true);
            try
            {
                var live = current.LoadFromStream(new MemoryStream(File.ReadAllBytes(dll)));
                var stale = leaving.LoadFromStream(new MemoryStream(File.ReadAllBytes(dll)));
                live.GetType("Versioned.Tag")!.GetField("Value")!.SetValue(null, "live");
                stale.GetType("Versioned.Tag")!.GetField("Value")!.SetValue(null, "stale");
                leaving.Unload();

                var result = RunTag(a => a == live || a == stale ? dll : null);
                Assert.True(result.Success, result.Error + string.Join(" | ", result.Diagnostics));
                Assert.Equal("live", result.Value);
                GC.KeepAlive(stale);
            }
            finally
            {
                current.Unload();
            }
        }

        [Fact]
        public void CollectibleHostPrefersTheDefaultContextAmongEqualIdentities()
        {
            // The Default copy stays loaded for the process: its own namespace keeps it out of the other tests' way.
            var name = "DefaultPick" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var dll = BuildLibrary(name, "DefaultPick.Tag", VersionedTag("1", "init", "DefaultPick"));
            var other = new System.Runtime.Loader.AssemblyLoadContext("TestOther", isCollectible: true);
            try
            {
                var inDefault = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(dll); // unique name: harmless
                var later = other.LoadFromStream(new MemoryStream(File.ReadAllBytes(dll)));
                inDefault.GetType("DefaultPick.Tag")!.GetField("Value")!.SetValue(null, "default");
                later.GetType("DefaultPick.Tag")!.GetField("Value")!.SetValue(null, "other");

                var result = RunTag(a => a == later ? dll : null, "DefaultPick");
                Assert.True(result.Success, result.Error + string.Join(" | ", result.Diagnostics));
                Assert.Equal("default", result.Value);
            }
            finally
            {
                other.Unload();
            }
        }

        [Fact]
        public void CollectibleContextsAreUnloaded()
        {
            var runner = Fixtures.Runner();
            Assert.True(runner.Host.SupportsUnload);
            for (var i = 0; i < 30; i++)
                Assert.Equal((i * 2).ToString(), runner.Execute(new ExecuteRequest { Code = $"var k = {i};\nk * 2" }).Value);
            for (var i = 0; i < 10 && Alive() > 0; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.Equal(0, Alive());

            static int Alive() => System.Runtime.Loader.AssemblyLoadContext.All.Count(c => c.Name != null && c.Name.StartsWith("CodeExec:"));
        }

        [Theory]
        [InlineData("var a = 1;\nthrow new InvalidOperationException(\"x\" + a);")]
        [InlineData("var a = 0;\n10 / a")]
        public void RuntimeStackTracePointsAtSnippetLines(string code)
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = code });
            Assert.Equal(ErrorKind.Runtime, result.ErrorKind);
            Assert.Contains("snippet.cs:line 2", result.Error);
        }
#endif

        [Theory]
        [InlineData(typeof(int), "global::System.Int32")]
        [InlineData(typeof(List<string>), "global::System.Collections.Generic.List<global::System.String>")]
        [InlineData(typeof(Dictionary<string, List<int>>), "global::System.Collections.Generic.Dictionary<global::System.String, global::System.Collections.Generic.List<global::System.Int32>>")]
        [InlineData(typeof(int[,]), "global::System.Int32[,]")]
        [InlineData(typeof(Outer.Inner), "global::CodeExecTests.RunnerTests.Outer.Inner")]
        public void FormatsTypeNames(Type type, string expected) => Assert.Equal(expected, TypeNames.ToCSharp(type));

        public static class Outer { public sealed class Inner { } }

        // A host that cannot unload reuses a snippet's assembly only when the snippet has no static state of its own:
        // every run of a stateful one starts from fresh statics, as on a host that unloads.
        [Theory]
        [InlineData("public static class Ctr { public static int N; }\n++Ctr.N", "1", true)]
        [InlineData("static int counter = 3;\n++counter", "4", true)]
        [InlineData("public static class K { public static readonly List<int> L = new List<int>(); }\nK.L.Add(1);\nK.L.Count", "1", true)]
        [InlineData("public static class P { public static int N { get; set; } }\n++P.N", "1", true)]
        [InlineData("public static class Cache { public static Func<int> F; public static int Calls; }\nCache.F ??= () => ++Cache.Calls;\nCache.F()", "1", true)]
        [InlineData("Func<int, int> twice = x => x * 2;\nnew[] { 1, 2 }.Select(twice).Sum()", "6", false)] // lambda caches only
        [InlineData("var seen = new List<int>();\nFunc<int, int> twice = x => x * 2;\nseen.Add(twice(1));\nnew[] { 1, 2 }.Sum() + seen.Count", "4", false)]
        [InlineData("const int K = 2;\nnew[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }.Length * K", "20", false)] // array data only
        // readonly + immutable type: a lookup table, not state (spec §3.5)
        [InlineData("static readonly string greeting = \"hi\";\ngreeting.Length", "2", false)]
        [InlineData("public enum Color { Red, Green }\npublic static class D { public static readonly Color Default = Color.Green; public static readonly int? Max = 5; public static readonly TimeSpan T = TimeSpan.FromSeconds(1); }\n(int)D.Default + D.Max.Value + (int)D.T.TotalSeconds", "7", false)]
        [InlineData("public static class G { public static int N { get; } = 3; }\nG.N", "3", false)]
        [InlineData("public static class M { public static readonly IReadOnlyDictionary<string, int> Map = new Dictionary<string, int> { [\"a\"] = 1, [\"b\"] = 2 }; }\nM.Map[\"b\"]", "2", false)]
        [InlineData("public static class I { public static readonly System.Collections.Immutable.ImmutableArray<string> A = System.Collections.Immutable.ImmutableArray.Create(\"x\", \"y\"); }\nI.A.Length", "2", false)]
        // readonly but mutable: arrays, mutable elements, or a mutable field next to an immutable one
        [InlineData("public static class A { public static readonly int[] C = new int[1]; }\n++A.C[0]", "1", true)]
        [InlineData("public static class R { public static readonly IReadOnlyList<List<int>> L = new[] { new List<int>() }; }\nR.L[0].Add(1);\nR.L[0].Count", "1", true)]
        [InlineData("public static class X { public static readonly string S = \"s\"; public static int N; }\n++X.N", "1", true)]
        [InlineData("public static class GL { public static List<int> L { get; } = new List<int>(); }\nGL.L.Add(1);\nGL.L.Count", "1", true)]
        [InlineData("public static class P { public static readonly IntPtr Mem = System.Runtime.InteropServices.Marshal.AllocHGlobal(4); static P() { System.Runtime.InteropServices.Marshal.WriteInt32(Mem, 0); } }\nvar n = System.Runtime.InteropServices.Marshal.ReadInt32(P.Mem) + 1;\nSystem.Runtime.InteropServices.Marshal.WriteInt32(P.Mem, n);\nn", "1", true)]
        // a view the snippet implements itself can hide state in its instance
        [InlineData("public sealed class Ctr : IReadOnlyList<int> { int n; public int this[int i] => ++n; public int Count => 1; public IEnumerator<int> GetEnumerator() { yield return this[0]; } System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); }\npublic static class S { public static readonly IReadOnlyList<int> L = new Ctr(); }\nS.L[0]", "1", true)]
        public void StatefulSnippetsNeverSeeAnotherRunsStatics(string code, string expected, bool stateful)
        {
            var runner = Fixtures.Runner(configure: o => o.Host = new DefaultAssemblyHost());
            for (var i = 0; i < 3; i++)
            {
                var result = runner.Execute(new ExecuteRequest { Code = code });
                Assert.True(result.Success, result.Error);
                Assert.Equal(expected, result.Value);
            }
            // A stateful snippet loads on every run. A stateless one is reused (the first run may load twice: assemblies
            // the process loads during it change the reference set, hence the cache key).
            if (stateful)
                Assert.Equal(3, runner.Stats.LoadedAssemblies);
            else
                Assert.True(runner.Stats.LoadedAssemblies < 3, "loads: " + runner.Stats.LoadedAssemblies);
        }

        [Fact]
        public void FailingStaticInitializerOnReuseIsARuntimeError()
        {
            var runner = Fixtures.Runner(configure: o => o.Host = new DefaultAssemblyHost());
            const string code = "public static class Boom { public static int Runs; static Boom() { if (System.Environment.GetEnvironmentVariable(\"CODEEXEC_TEST_BOOM\") == \"1\") throw new InvalidOperationException(\"init\"); } }\n++Boom.Runs";
            Assert.Equal("1", runner.Execute(new ExecuteRequest { Code = code }).Value);
            Environment.SetEnvironmentVariable("CODEEXEC_TEST_BOOM", "1");
            try
            {
                var result = runner.Execute(new ExecuteRequest { Code = code });
                Assert.Equal(ErrorKind.Runtime, result.ErrorKind);
                Assert.Contains("init", result.Error);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEEXEC_TEST_BOOM", null);
            }
        }

        [Fact]
        public void BadUsingIsReportedAsTheUsing()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "1", Usings = new[] { "No.Such.Namespace" } });
            Assert.Equal(ErrorKind.Compile, result.ErrorKind);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("usings", diagnostic.File);
            Assert.Equal(0, diagnostic.Line);
            Assert.StartsWith("using 'No.Such.Namespace': ", diagnostic.Message);
        }

        [Fact]
        public void SnippetErrorsKeepTheirCoordinatesNextToUsings()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "var a = 1;\nundefinedThing", Usings = new[] { "System.Text" } });
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("snippet.cs", diagnostic.File);
            Assert.Equal(2, diagnostic.Line);
        }

        [Fact]
        public void DiagnosticsAreCapped()
        {
            var code = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"int x{i} = \"s\";"));
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = code });
            Assert.Equal(ErrorKind.Compile, result.ErrorKind);
            Assert.Equal(OutputLimits.MaxDiagnostics + 1, result.Diagnostics.Count);
            Assert.Equal("CE0002", result.Diagnostics.Last().Id);
            Assert.StartsWith("10 more diagnostic(s) left out", result.Diagnostics.Last().Message);
        }

        [Fact]
        public void IncludeOutsideTheRootNeedsTheHostsConsent()
        {
            var root = Fixtures.Path("fixtures/include/csharp/geo");
            var outside = Fixtures.Path("fixtures/include/csharp/Broken.cs");
            foreach (var entry in new[] { "../Broken.cs", outside })
            {
                var refused = Fixtures.Runner(root).Execute(new ExecuteRequest { Code = "1", Include = new[] { entry } });
                Assert.Equal(ErrorKind.InvalidRequest, refused.ErrorKind);
                Assert.Contains("outside the include root", refused.Error);
            }
            var allowed = Fixtures.Runner(root, o => o.AllowIncludeOutsideRoot = true).Execute(new ExecuteRequest { Code = "1", Include = new[] { "../Broken.cs" } });
            Assert.Equal(ErrorKind.Compile, allowed.ErrorKind); // Broken.cs is read and compiled now
            // An absolute path inside the root is fine, and is reported relative to it.
            var inside = Fixtures.Runner(root).Execute(new ExecuteRequest { Code = "1", Include = new[] { root } });
            Assert.True(inside.Success, inside.Error);
        }

        [Fact]
        public void InnerExceptionsAreReportedAndBounded()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "Exception e = new ArgumentException(\"cause 0\");\nfor (var i = 1; i < 9; i++) e = new InvalidOperationException(\"cause \" + i, e);\nthrow e;",
            });
            Assert.Equal(ErrorKind.Runtime, result.ErrorKind);
            Assert.StartsWith("System.InvalidOperationException: cause 8", result.Error);
            Assert.Contains(" ---> System.InvalidOperationException: cause 3", result.Error);
            Assert.DoesNotContain("cause 2", result.Error);
            Assert.Contains(" ---> ... more inner exception(s) left out", result.Error);
        }

        [Fact]
        public void RunawayConsoleOutputKeepsFirstAndLastLines()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "for (var i = 0; i < 20000; i++) Console.WriteLine(\"line \" + i);" });
            Assert.True(result.Success, result.Error);
            Assert.Equal("line 0", result.Logs.First().Message);
            Assert.Equal("line 19999", result.Logs.Last().Message);
            Assert.Single(result.Logs, l => l.Level == "warning" && l.Message.Contains("log line(s) left out"));
            Assert.True(result.Logs.Where(l => l.Level != "warning").Sum(l => l.Message.Length + OutputLimits.LogEntryChars) <= OutputLimits.DefaultLogChars);
            Assert.Equal(new[] { "logs" }, result.Truncated);
        }

        [Fact]
        public void NonTextValueIsLeftToTheHost()
        {
            var big = Enumerable.Range(0, 200000).ToArray();
            var result = Fixtures.Runner(configure: o => o.ValueConverter = v => v is int[] a ? a.Length : v).Execute(new ExecuteRequest { Code = "Enumerable.Range(0, 200000).ToArray()" });
            Assert.Equal(big.Length, result.Value);
            Assert.Empty(result.Truncated);
        }

        [Fact]
        public void ClipNeverSplitsASurrogatePair()
        {
            var clipped = OutputLimits.Clip("ab\U0001F600cd", 3, "hint");
            Assert.Equal("ab... [truncated: 2 of 6 characters shown; hint]", clipped);
            Assert.Equal("abc", OutputLimits.Clip("abc", 3, "hint"));
            Assert.Equal("abcdef", OutputLimits.Clip("abcdef", 0, "hint"));
        }

        [Fact]
        public void EmptyIncludeRootIsFineWithoutIncludes()
        {
            var result = Fixtures.Runner(configure: o => o.IncludeRoot = "").Execute(new ExecuteRequest { Code = "1" });
            Assert.True(result.Success, result.Error);
        }

        sealed class NullMessageCapture : ILogCapture
        {
            public IDisposable Begin(Action<LogEntry> sink)
            {
                sink(new LogEntry("info", null!));
                return new System.IO.StringWriter(); // anything disposable
            }
        }

        [Fact]
        public void HostLogEntryWithoutAMessageIsKept()
        {
            var result = Fixtures.Runner(configure: o => o.LogCaptures.Add(new NullMessageCapture())).Execute(new ExecuteRequest { Code = "1" });
            Assert.True(result.Success, result.Error);
            Assert.Contains(result.Logs, l => l.Message == "");
        }

        [Fact]
        public void EarlyErrorsAreClippedToo()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "1", Include = new[] { new string('d', 300) + ".cs" }, MaxErrorChars = 30 });
            Assert.Equal(ErrorKind.InvalidRequest, result.ErrorKind);
            Assert.EndsWith("characters shown; raise a shorter message]", result.Error);
            Assert.Equal(new[] { "error" }, result.Truncated);
        }

        [Fact]
        public void NegativeLimitIsAnInvalidRequest()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "1", MaxErrorChars = -5 });
            Assert.Equal(ErrorKind.InvalidRequest, result.ErrorKind);
        }
    }
}
