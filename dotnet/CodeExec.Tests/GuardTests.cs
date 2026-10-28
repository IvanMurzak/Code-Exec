using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using com.IvanMurzak.CodeExec;
using Xunit;

namespace CodeExecTests
{
    /// <summary>Execution guards (spec §3.6): runaway recursion and endless loops end the run, never the host.</summary>
    public class GuardTests
    {
        // Without the stack guard each of these ends the test process with an uncatchable StackOverflowException.
        [Theory]
        [InlineData("int F(int n) => n == 0 ? 0 : 1 + F(n - 1);\nF(10_000_000)")]
        [InlineData("static int F(int n) { return n == 0 ? 0 : 1 + F(n - 1); }\nF(10_000_000)")]
        [InlineData("Func<int, int> f = null;\nf = n => { return n == 0 ? 0 : 1 + f(n - 1); };\nf(10_000_000)")]
        [InlineData("static int P => P;\nP")]
        [InlineData("class Node { public int Depth(int n) => 1 + Depth(n + 1); }\nnew Node().Depth(0)")]
        // Field and property initializers run before any constructor body.
        [InlineData("class Node { Node next = new Node(); public Node() { } }\nnew Node();\n1")]
        [InlineData("class Node { public Node Next { get; } = new Node(); }\nnew Node();\n1")]
        // A catch-all at every level must not swallow the stop and carry on recursing.
        [InlineData("int F(int n) { try { return 1 + F(n + 1); } catch (Exception) { return F(n + 1); } }\nF(0)")]
        public void DeepRecursionIsAnErrorNotACrash(string code)
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = code });
            Assert.Equal(ErrorKind.Runtime, result.ErrorKind);
            Assert.StartsWith("System.InsufficientExecutionStackException: Recursion too deep", result.Error);
            Assert.DoesNotContain(ExecutionGuard.TypeName, result.Error); // the guard's own frames are not the agent's
        }

        [Fact]
        public void DeepRecursionInFullSourceModeAndIncludes()
        {
            var full = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "public static class E { static int F(int n) => n == 0 ? 0 : 1 + F(n - 1); public static int Run() => F(10_000_000); }",
                EntryType = "E",
                EntryMethod = "Run",
            });
            Assert.Equal(ErrorKind.Runtime, full.ErrorKind);
            Assert.Contains("Recursion too deep", full.Error);

            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "Deep.cs"), "public static class Deep { public static int F(int n) => n == 0 ? 0 : 1 + F(n - 1); }");
            var include = Fixtures.Runner(dir.Path).Execute(new ExecuteRequest { Code = "Deep.F(10_000_000)", Include = new[] { "Deep.cs" } });
            Assert.Equal(ErrorKind.Runtime, include.ErrorKind);
            Assert.Contains("Recursion too deep", include.Error);
        }

        [Fact]
        public void HostStackAllowanceBoundsRecursion()
        {
            const string code = "int F(int n) => n == 0 ? 0 : 1 + F(n - 1);\nF(2000)";
            Assert.Equal("2000", Fixtures.Runner().Execute(new ExecuteRequest { Code = code }).Value);
            var bounded = Fixtures.Runner(configure: o => o.MaxStackBytes = 16 * 1024).Execute(new ExecuteRequest { Code = code });
            Assert.Equal(ErrorKind.Runtime, bounded.ErrorKind);
            Assert.Contains("Recursion too deep", bounded.Error);
        }

        [Fact]
        public void DeepRecursionOnASmallStackThread()
        {
            ExecuteResult? result = null;
            var thread = new System.Threading.Thread(() =>
                result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "int F(int n) => n == 0 ? 0 : 1 + F(n - 1);\nF(10_000_000)" }),
                1024 * 1024);
            thread.Start();
            thread.Join();
            Assert.Equal(ErrorKind.Runtime, result!.ErrorKind);
            Assert.Contains("Recursion too deep", result.Error);
        }

        // Without the time guard each of these never returns.
        [Theory]
        [InlineData("while (true) { }")]
        [InlineData("while (true);")]
        [InlineData("for (;;) { }")]
        [InlineData("do { } while (true);")]
        [InlineData("var i = 0;\nloop: i++;\ngoto loop;")]
        [InlineData("foreach (var x in Forever()) { }\nIEnumerable<int> Forever() { while (true) yield return 1; }")]
        [InlineData("long n = 0;\nforeach (var x in Enumerable.Repeat(1, int.MaxValue)) n += x;\nforeach (var x in Enumerable.Repeat(1, int.MaxValue)) n += x;\nn")]
        [InlineData("while (true) { try { throw new InvalidOperationException(); } catch { } }")]
        [InlineData("while (true) { try { System.Threading.Thread.Sleep(1); } catch (Exception e) when (e != null) { } }")]
        [InlineData("void Spin() { for (var i = 0; ; i++) { } }\nSpin();")]
        [InlineData("if (DateTime.Now.Year > 0) while (true) { }\n1")]
        public void EndlessLoopTimesOutWithinTheBudget(string code)
        {
            var watch = Stopwatch.StartNew();
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = code, TimeoutMs = 300 });
            Assert.True(ErrorKind.Timeout == result.ErrorKind, $"{result.ErrorKind}: {result.Error}");
            Assert.StartsWith("System.TimeoutException: The snippet ran longer than its time budget of 300 ms (timeoutMs)", result.Error);
            Assert.InRange(watch.ElapsedMilliseconds, 250, 5000);
        }

        [Fact]
        public void FullSourceStartingWithDefineStaysVerbatim()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "#define FOO\npublic static class E { public static int Run() {\n#if FOO\nreturn 1;\n#else\nreturn 0;\n#endif\n} }",
                EntryType = "E",
                EntryMethod = "Run",
                Usings = new[] { "System.Text" },
            });
            Assert.True(result.Success, result.Error + " " + string.Join(" | ", result.Diagnostics));
            Assert.Equal("1", result.Value);
        }

        [Fact]
        public void EndlessLoopInFullSourceModeTimesOut()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest
            {
                Code = "public static class E { public static void Run() { while (true) { } } }",
                EntryType = "E",
                EntryMethod = "Run",
                TimeoutMs = 200,
            });
            Assert.Equal(ErrorKind.Timeout, result.ErrorKind);
        }

        [Fact]
        public async Task AsynchronousEndlessLoopTimesOut()
        {
            var result = await Fixtures.Runner().ExecuteAsync(new ExecuteRequest { Code = "while (true) await Task.Yield();", TimeoutMs = 300 });
            Assert.Equal(ErrorKind.Timeout, result.ErrorKind);
        }

        [Fact]
        public void TimeoutErrorPointsAtTheLoop()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "var a = 1;\nwhile (a > 0) { a = 1; }", TimeoutMs = 200 });
            Assert.Equal(ErrorKind.Timeout, result.ErrorKind);
            Assert.Contains("snippet.cs:line 2", result.Error);
        }

        [Fact]
        public void HostDefaultBudgetAppliesWhenTheRequestHasNone()
        {
            var runner = Fixtures.Runner(configure: o => o.DefaultTimeoutMs = 300);
            var result = runner.Execute(new ExecuteRequest { Code = "while (true) { }" });
            Assert.Equal(ErrorKind.Timeout, result.ErrorKind);
            Assert.Contains("300 ms", result.Error);
            Assert.Equal(CodeRunnerOptions.DefaultTimeout, new CodeRunnerOptions().DefaultTimeoutMs);
            var negative = runner.Execute(new ExecuteRequest { Code = "1", TimeoutMs = -1 });
            Assert.Equal(ErrorKind.InvalidRequest, negative.ErrorKind);
        }

        [Fact]
        public void ReusedAssemblyIsReArmedOnEveryRunButATimedOutOneIsNotReused()
        {
            // Mono path: one loaded assembly per request, the guard re-armed by each run (it is no static state)...
            var runner = Fixtures.Runner(configure: o => o.Host = new DefaultAssemblyHost());
            for (var i = 0; i < 3; i++)
            {
                var result = runner.Execute(new ExecuteRequest { Code = "var s = 0;\nfor (var i = 0; i < 100; i++) s += i;\ns", TimeoutMs = 150 });
                Assert.True(result.Success, result.Error);
                Assert.Equal("4950", result.Value);
            }
            Assert.Equal(1, runner.Stats.LoadedAssemblies);
            // ...but a timed-out one keeps its stop for whatever of it still runs: the next identical request loads a fresh copy.
            for (var i = 0; i < 2; i++)
                Assert.Equal(ErrorKind.Timeout, runner.Execute(new ExecuteRequest { Code = "while (true) { }", TimeoutMs = 150 }).ErrorKind);
            Assert.Equal(3, runner.Stats.LoadedAssemblies);
        }

        [Fact]
        public void FinishedRunLiftsTheBudgetForCallbacksItLeftBehind()
        {
            var runner = Fixtures.Runner(configure: o => o.Host = new DefaultAssemblyHost());
            var result = runner.Execute(new ExecuteRequest
            {
                Code = "Func<int> f = () => { var s = 0; for (var i = 0; i < 10; i++) s += i; return s; };\nAppDomain.CurrentDomain.SetData(\"codeexec-callback\", f);\n1",
                TimeoutMs = 100,
            });
            Assert.True(result.Success, result.Error);
            System.Threading.Thread.Sleep(300); // past the run's budget
            var callback = (Func<int>)AppDomain.CurrentDomain.GetData("codeexec-callback")!;
            AppDomain.CurrentDomain.SetData("codeexec-callback", null);
            Assert.Equal(45, callback());
        }

        [Fact]
        public void GuardsCanBeTurnedOff()
        {
            const string code = "System.Reflection.Assembly.GetExecutingAssembly().GetType(\"__CodeExecGuard\") != null";
            Assert.Equal("True", Fixtures.Runner().Execute(new ExecuteRequest { Code = code }).Value);
            Assert.Equal("False", Fixtures.Runner(configure: o => o.ExecutionGuards = false).Execute(new ExecuteRequest { Code = code }).Value);
        }

        // The guard rewrites must not change what the agent's code means.
        [Theory]
        [InlineData("var sum = 0;\nfor (var i = 0; i < 100; i++) sum += i;\nsum", "4950")]
        [InlineData("var i = 0;\nagain: i++;\nif (i < 5) goto again;\ni", "5")]
        [InlineData("switch (2) { case 1: return \"one\"; case 2: goto case 1; }\nreturn \"none\";", "one")]
        [InlineData("try { throw new InvalidOperationException(\"x\"); } catch (InvalidOperationException e) when (e.Message == \"x\") { return 1; }\nreturn 0;", "1")]
        [InlineData("try { throw new InvalidOperationException(\"x\"); } catch { return 2; }", "2")]
        [InlineData("void Log() => Console.WriteLine(\"hi\");\nLog();\n3", "3")]
        [InlineData("async Task<int> G() => await Task.FromResult(3);\nawait G()", "3")]
        [InlineData("async Task H() => await Task.Yield();\nawait H();\n4", "4")]
        [InlineData("ref int First(int[] a) => ref a[0];\nvar arr = new[] { 1 };\nFirst(arr) = 6;\narr[0]", "6")]
        [InlineData("$\"{((Func<int>)(() => { var s = 0; for (var i = 0; i < 3; i++) s += i; return s; }))()}\"", "3")]
        [InlineData("System.Linq.Expressions.Expression<Func<int, int>> e = x => x + 1;\ne.Compile()(1)", "2")]
        [InlineData("class C { int v; public C(int v) => this.v = v; public static C operator +(C a, C b) => new C(a.v + b.v); public int this[int i] => v * i; public int V { get => v; set => v = value; } }\n(new C(2) + new C(3))[2]", "10")]
        [InlineData("Func<int, int> sq = delegate (int x) { return x * x; };\nsq(7)", "49")]
        [InlineData("int T() => throw new InvalidOperationException(\"t\");\ntry { return T(); } catch (InvalidOperationException e) { return e.Message; }", "t")]
        [InlineData("using MyTask = System.Threading.Tasks.Task;\nasync MyTask F() => await Task.Yield();\nawait F();\n1", "1")]
        [InlineData("class R { public int Y { get; set; } = 2; int z = 3; public int Sum => Y + z; }\nnew R().Sum", "5")]
        [InlineData("static class S { static readonly int[] Table = { 1, 2 }; public static int First => Table[0]; }\nS.First", "1")]
        public async Task GuardedCodeKeepsItsMeaning(string code, string expected)
        {
            var result = await Fixtures.Runner().ExecuteAsync(new ExecuteRequest { Code = code });
            Assert.True(result.Success, result.Error + " " + string.Join(" | ", result.Diagnostics));
            Assert.Equal(expected, result.Value);
        }

        // Inserted guard calls sit on lines of their own: the agent's lines AND columns stay.
        [Theory]
        [InlineData("while (false) { } int x = undefinedThing;", 1, 27)]
        [InlineData("for (;;) { var y = undefinedThing; }", 1, 20)]
        [InlineData("var a = 1;\nwhile (a > 0) a += undefinedThing;", 2, 20)]
        [InlineData("int F() => undefinedThing; int G() { return undefinedThing; }", 1, 12)]
        [InlineData("int F() { return 1; } int G() { return 2; } var z = undefinedThing;", 1, 53)]
        [InlineData("try { } catch (Exception e) when (e != null) { } var q = undefinedThing;", 1, 58)]
        public void DiagnosticsKeepTheAgentsCoordinatesAroundGuards(string code, int line, int column)
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = code, AutoImport = false });
            Assert.Equal(ErrorKind.Compile, result.ErrorKind);
            var d = Assert.Single(result.Diagnostics, x => x.Id == "CS0103" && x.Line == line && x.Column == column);
            Assert.Equal("snippet.cs", d.File);
        }

        [Fact]
        public void IncludeOutsideTheRootKeepsItsAbsolutePathThroughGuards()
        {
            using var dir = new TempDir();
            var path = Path.Combine(dir.Path, "Looping.cs");
            File.WriteAllText(path, "public static class Looping\n{\n    public static int Sum(int n) { var s = 0; for (var i = 0; i < n; i++) s += i; return s; }\n    public static int Bad() => undefinedThing;\n}\n");
            var runner = Fixtures.Runner(Fixtures.Path("fixtures"), o => o.AllowIncludeOutsideRoot = true);
            var result = runner.Execute(new ExecuteRequest { Code = "Looping.Sum(4)", Include = new[] { path } });
            Assert.Equal(ErrorKind.Compile, result.ErrorKind);
            var d = Assert.Single(result.Diagnostics, x => x.Id == "CS0103");
            Assert.Equal(path.Replace('\\', '/'), d.File.Replace('\\', '/'));
            Assert.Equal(4, d.Line);
            Assert.Equal(32, d.Column);
        }

        sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codeexec-tests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try { Directory.Delete(Path, true); }
                catch (IOException) { }
            }
        }
    }
}
