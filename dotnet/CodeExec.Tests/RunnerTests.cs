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

#if NET8_0_OR_GREATER
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

        [Fact]
        public void RuntimeStackTracePointsAtSnippetLines()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "var a = 1;\nthrow new InvalidOperationException(\"x\" + a);" });
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
