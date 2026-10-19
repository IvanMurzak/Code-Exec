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
        public void FullSourceModeDoesNotClaimAutoImports()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "public static class E { public static object Run() => new StringBuilder(); }",
                EntryType = "E",
                EntryMethod = "Run",
            });
            Assert.Equal(ErrorKind.Compile, result.ErrorKind);
            Assert.Empty(result.AutoImported);
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
            var dir = Path.Combine(Path.GetTempPath(), "codeexec-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var name = "InMemoryLib" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var dll = Path.Combine(dir, name + ".dll");
            var build = new RoslynCSharpCompiler().Compile(new CompileRequest
            {
                Code = "namespace InMemory { public static class Answer { public static int Get() => 99; } }",
                EntryType = "InMemory.Answer",
                AssemblyName = name,
                ReferencePaths = new LoadedAssembliesReferenceProvider().GetReferencePaths(),
            });
            Assert.True(build.Success, string.Join(" | ", build.Diagnostics));
            File.WriteAllBytes(dll, build.Image!);
            var loaded = Assembly.Load(File.ReadAllBytes(dll));
            Assert.Equal("", loaded.Location);

            var blind = Fixtures.Runner().Execute(new ExecuteRequest { Code = "InMemory.Answer.Get()" });
            Assert.Equal(ErrorKind.Compile, blind.ErrorKind);

            var seeing = Fixtures.Runner(configure: o => o.References = new LoadedAssembliesReferenceProvider(a => a.GetName().Name == name ? dll : null))
                .Execute(new ExecuteRequest { Code = "InMemory.Answer.Get()" });
            Assert.True(seeing.Success, seeing.Error + string.Join(" | ", seeing.Diagnostics));
            Assert.Equal("99", seeing.Value);
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
        public void ReferencesIncludeTheFrameworkFacadesEvenBeforeTheyLoad()
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
            var dll = BuildLibrary(name, "1.0.0.0", "namespace LiveGame { public static class Score { public static int Value = 1; } }");
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
            var v2 = BuildLibrary(name, "2.0.0.0", "namespace Versioned { public static class Tag { public static string Value = \"v2\"; } }");
            var v1 = BuildLibrary(name, "1.0.0.0", "namespace Versioned { public static class Tag { public static string Value = \"v1\"; } }");
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

        static string BuildLibrary(string name, string version, string code)
        {
            var dir = Path.Combine(Path.GetTempPath(), "codeexec-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var build = new RoslynCSharpCompiler().Compile(new CompileRequest
            {
                Code = $"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]\n{code}\nnamespace Entry {{ public static class Main {{ public static void Run() {{ }} }} }}",
                EntryType = "Entry.Main",
                AssemblyName = name,
                ReferencePaths = new LoadedAssembliesReferenceProvider().GetReferencePaths(),
            });
            Assert.True(build.Success, string.Join(" | ", build.Diagnostics));
            var dll = Path.Combine(dir, name + ".dll");
            File.WriteAllBytes(dll, build.Image!);
            return dll;
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
    }
}
