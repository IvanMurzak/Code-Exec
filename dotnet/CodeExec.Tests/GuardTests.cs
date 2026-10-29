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

        // Recursion that runs only through expression-bodied lambdas (and anonymous methods). In a child process: an
        // unguarded one ends the process with a stack overflow, which then fails this test instead of the whole run.
        [Theory]
        [InlineData("Func<int, int> f = null;\nf = n => n == 0 ? 0 : 1 + f(n - 1);\nf(10_000_000)")]
        [InlineData("Func<int, int> f = null;\nf = (int n) => n == 0 ? 0 : 1 + f(n - 1);\nf(10_000_000)")]
        [InlineData("Action<int> a = null;\na = n => a(n + 1);\na(0);")]
        [InlineData("Func<int, Task<int>> g = null;\ng = async n => n == 0 ? 0 : 1 + await g(n - 1);\nawait g(10_000_000)")]
        [InlineData("Func<int, int> f = null;\nf = delegate (int n) { return n == 0 ? 0 : 1 + f(n - 1); };\nf(10_000_000)")]
        [InlineData("class C { public static Func<int, int> F = n => n == 0 ? 0 : 1 + F(n - 1); }\nC.F(10_000_000)")]
        // A lambda whose target is Delegate / object has a natural delegate type (C# 10, Roslyn 4+): still guarded.
        [InlineData("Delegate d = null;\nd = (int n) => n == 0 ? 0 : 1 + ((Func<int, int>)d)(n - 1);\n((Func<int, int>)d)(10_000_000)", 4)]
        public void DeepRecursionThroughLambdasIsAnErrorNotACrash(string code, int minRoslyn = 0)
        {
            if (typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.GetName().Version!.Major < minRoslyn)
                return;
            var (kind, error) = ChildProcess.Execute(code);
            Assert.True(kind == nameof(ErrorKind.Runtime), $"{kind}: {error}");
            Assert.StartsWith("System.InsufficientExecutionStackException: Recursion too deep", error);
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
        public void HostCapsTheBudget()
        {
            var runner = Fixtures.Runner(configure: o => o.MaxTimeoutMs = 300);
            foreach (var timeoutMs in new[] { 0, 60_000 }) // the host default (120 s) and a longer request both get the cap
            {
                var watch = Stopwatch.StartNew();
                var result = runner.Execute(new ExecuteRequest { Code = "while (true) { }", TimeoutMs = timeoutMs });
                Assert.Equal(ErrorKind.Timeout, result.ErrorKind);
                Assert.Contains("300 ms", result.Error);
                Assert.InRange(watch.ElapsedMilliseconds, 250, 5000);
            }
            Assert.Contains("100 ms", runner.Execute(new ExecuteRequest { Code = "while (true) { }", TimeoutMs = 100 }).Error); // a shorter one stays
            var unbounded = Fixtures.Runner(configure: o => { o.DefaultTimeoutMs = 0; o.MaxTimeoutMs = 200; });
            Assert.Contains("200 ms", unbounded.Execute(new ExecuteRequest { Code = "while (true) { }" }).Error);
            Assert.Equal(0, new CodeRunnerOptions().MaxTimeoutMs);
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

        // Work a snippet starts on another thread belongs to its run: once the run has ended, it stops at its next check.
        // Without that, each of these spins forever after the run (the test then times out waiting for it).
        [Theory]
        [InlineData("var t = new System.Threading.Thread(() => { while (true) { } }) { IsBackground = true };\nt.Start();\nreturn (Func<bool>)(() => t.Join(10_000));")]
        [InlineData("void Spin() { var n = 0L; while (n >= 0) n++; }\nvar t = new System.Threading.Thread(Spin) { IsBackground = true };\nt.Start();\nreturn (Func<bool>)(() => t.Join(10_000));")]
        [InlineData("var t = new System.Threading.Thread(o => { while (o != null) { } }) { IsBackground = true };\nt.Start(1);\nreturn (Func<bool>)(() => t.Join(10_000));")]
        // Waits until the work runs inside its try: work that starts only after the run has ended stops at its first check.
        [InlineData("var started = new System.Threading.ManualResetEvent(false);\nvar done = new System.Threading.ManualResetEvent(false);\nSystem.Threading.ThreadPool.QueueUserWorkItem(_ => { try { started.Set(); while (true) { } } finally { done.Set(); } });\nstarted.WaitOne();\nreturn (Func<bool>)(() => done.WaitOne(10_000));")]
        [InlineData("var t = Task.Run(() => { while (true) { } });\nreturn (Func<bool>)(() => ((IAsyncResult)t).AsyncWaitHandle.WaitOne(10_000));")]
        [InlineData("var t = Task.Run(() => { var n = 0; while (true) n++; return n; });\nreturn (Func<bool>)(() => ((IAsyncResult)t).AsyncWaitHandle.WaitOne(10_000) && t.Exception!.InnerException is TimeoutException);")]
        [InlineData("var t = Task.Run(async () => { while (true) await Task.Yield(); });\nreturn (Func<bool>)(() => ((IAsyncResult)t).AsyncWaitHandle.WaitOne(10_000));")]
        [InlineData("var t = Task.Factory.StartNew(() => { while (true) { } }, TaskCreationOptions.LongRunning);\nreturn (Func<bool>)(() => ((IAsyncResult)t).AsyncWaitHandle.WaitOne(10_000));")]
        [InlineData("var t = new Task(() => { while (true) { } });\nt.Start();\nreturn (Func<bool>)(() => ((IAsyncResult)t).AsyncWaitHandle.WaitOne(10_000));")]
        // Asynchronous task work belongs to the run until its task ends, also when its first await returned before the run ended.
        [InlineData("var t = Task.Run(async () => { await Task.Delay(200); while (true) { } });\nSystem.Threading.Thread.Sleep(50);\nreturn (Func<bool>)(() => ((IAsyncResult)t).AsyncWaitHandle.WaitOne(10_000));")]
        // Code that asynchronous task work starts without a wrapper (a fire-and-forget async method) carries the run on.
        [InlineData("class K { public static System.Threading.ManualResetEvent Done = new System.Threading.ManualResetEvent(false); public static async Task Spin() { try { await Task.Delay(300); while (true) { } } finally { Done.Set(); } } }\nTask.Run(async () => { _ = K.Spin(); await Task.Yield(); }).Wait();\nreturn (Func<bool>)(() => K.Done.WaitOne(10_000));")]
        // Started by work the run started: it belongs to the same run.
        [InlineData("var inner = new TaskCompletionSource<Task>();\nTask.Run(() => inner.SetResult(Task.Run(() => { while (true) { } })));\nvar t = inner.Task.Result;\nreturn (Func<bool>)(() => ((IAsyncResult)t).AsyncWaitHandle.WaitOne(10_000));")]
        // Caught: the stop passes the agent's catch-all.
        [InlineData("var t = new System.Threading.Thread(() => { while (true) { try { System.Threading.Thread.Sleep(1); } catch (Exception) { } } }) { IsBackground = true };\nt.Start();\nreturn (Func<bool>)(() => t.Join(10_000));")]
        public void WorkTheSnippetStartedStopsWhenItsRunEnds(string code)
        {
            // The test takes the snippet's own delegate out (a host must not): it waits for the work after the run.
            var result = Fixtures.Runner(configure: o => o.ValueConverter = value => value).Execute(new ExecuteRequest { Code = code });
            Assert.True(result.Success, result.Error + " " + string.Join(" | ", result.Diagnostics));
            Assert.True(((Func<bool>)result.Value!)(), "the work is still running after its run ended");
        }

        [Theory]
        [InlineData("var t = new System.Threading.Thread(() => { while (true) { } });\nt.Start();\nt.Join();\n1")]
        [InlineData("Task.Run(() => { while (true) { } }).ContinueWith(_ => { }).Wait();\n1")]
        // A void task entry faults with the stop (only thread entries swallow it): the snippet does not carry on after Wait.
        [InlineData("var stop = false;\nTask.Run(() => { while (!stop) { } }).Wait();\nConsole.WriteLine(\"after\");")]
        public void BudgetSpentInWorkTheSnippetWaitsForIsATimeout(string code)
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = code, TimeoutMs = 300 });
            Assert.True(ErrorKind.Timeout == result.ErrorKind, $"{result.ErrorKind}: {result.Error}");
            Assert.Contains("The snippet ran longer than its time budget of 300 ms (timeoutMs)", result.Error);
            Assert.DoesNotContain(result.Logs, l => l.Message == "after");
        }

        [Fact]
        public void RecursionStoppedInAThreadTheSnippetWaitsForIsAnError()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "int R(int n) => R(n + 1) + 1;\nvar t = new System.Threading.Thread(() => { R(0); });\nt.Start();\nt.Join();\nreturn 5;" });
            Assert.Equal(ErrorKind.Runtime, result.ErrorKind);
            Assert.StartsWith("System.InsufficientExecutionStackException: Recursion too deep", result.Error);
        }

        [Fact]
        public void WorkerFramesAreNotTheAgents()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "Task.Run(() => { throw new InvalidOperationException(\"w\"); }).Wait();" });
            Assert.Equal(ErrorKind.Runtime, result.ErrorKind);
            Assert.Contains("InvalidOperationException: w", result.Error);
            Assert.Contains("snippet.cs:line 1", result.Error);
            Assert.DoesNotContain(ExecutionGuard.TypeName, result.Error);
        }

        [Fact]
        public void UnfinishedWorkIsReportedAsAWarning()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "new System.Threading.Thread(() => { while (true) { } }) { IsBackground = true }.Start();\n1" });
            Assert.True(result.Success, result.Error);
            Assert.Contains(result.Logs, l => l.Level == "warning" && l.Message.StartsWith("1 thread(s) or task(s) the snippet started had not finished"));
            var joined = Fixtures.Runner().Execute(new ExecuteRequest { Code = "var t = new System.Threading.Thread(() => { });\nt.Start();\nt.Join();\n1" });
            Assert.Empty(joined.Logs);
        }

        // A stop on a thread the snippet started would end the process if it went unhandled there. In a child process.
        [Theory]
        [InlineData("new System.Threading.Thread(() => { while (true) { } }).Start();\nwhile (true) { }", "Timeout")]
        [InlineData("System.Threading.ThreadPool.QueueUserWorkItem(_ => { while (true) { } });\nwhile (true) { }", "Timeout")]
        // Nothing but the work itself carries the run: an async void continuation or a timer the work created is not
        // stopped (an exception unhandled there would end the process).
        [InlineData("new System.Threading.Thread(() => System.Threading.Thread.Sleep(3000)) { IsBackground = true }.Start();\nnew System.Threading.Thread(async () => { await Task.Delay(100); for (var i = 0; i < 1000; i++) { } }) { IsBackground = true }.Start();\n1", "None")]
        [InlineData("new System.Threading.Thread(() => System.Threading.Thread.Sleep(3000)) { IsBackground = true }.Start();\nSystem.Threading.ThreadPool.QueueUserWorkItem(async _ => { await Task.Delay(100); for (var i = 0; i < 1000; i++) { } });\n1", "None")]
        [InlineData("class Keep { public static object T; }\nnew System.Threading.Thread(() => System.Threading.Thread.Sleep(3000)) { IsBackground = true }.Start();\nTask.Run(() => { Keep.T = new System.Threading.Timer(_ => { for (int i = 0; i < 10; i++) { } }, null, 300, 100); }).Wait();\n1", "None")]
        // A foreground thread left spinning would also keep the process from exiting.
        [InlineData("new System.Threading.Thread(() => { while (true) { } }).Start();\n1", "None")]
        public void StoppedThreadsNeverEndTheProcess(string code, string kind)
        {
            var (errorKind, error) = ChildProcess.Execute(code, timeoutMs: 300, lingerMs: 1000);
            Assert.True(errorKind == kind, errorKind + ": " + error);
        }

        [Fact]
        public void WorkACallbackStartsAfterTheRunIsItsOwn()
        {
            var runner = Fixtures.Runner(configure: o => o.ValueConverter = value => value);
            var result = runner.Execute(new ExecuteRequest
            {
                Code = "return (Func<int>)(() => Task.Run(() => { var watch = System.Diagnostics.Stopwatch.StartNew(); var n = 0; while (watch.ElapsedMilliseconds < 300) n++; return 1; }).Result);",
                TimeoutMs = 100,
            });
            Assert.True(result.Success, result.Error);
            Assert.Equal(1, ((Func<int>)result.Value!)());
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
        // Expression-bodied lambdas become blocks only when they are delegates: the value, the overload and the tree stay.
        [InlineData("var list = new List<int>();\nAction<List<int>> add = l => l.Add(1);\nadd(list);\nlist.Count", "1")]
        [InlineData("var d = new Dictionary<int, int> { [1] = 1 };\nAction<Dictionary<int, int>> drop = x => x.Remove(1);\ndrop(d);\nd.Count", "0")]
        [InlineData("Func<int, bool> even = x => x % 2 == 0;\neven(4)", "True")]
        [InlineData("Func<int, Func<int, int>> add = a => b => a + b;\nadd(2)(3)", "5")]
        [InlineData("Func<Task<int>> t = async () => await Task.FromResult(5);\nawait t()", "5")]
        [InlineData("Func<Task> t = async () => await Task.Yield();\nawait t();\n6", "6")]
        [InlineData("System.Linq.Expressions.Expression<Func<Func<int>>> e = () => () => 7;\ne.Compile()()()", "7")]
        [InlineData("new[] { 3, 1, 2 }.OrderBy(x => x).Select(x => x * 2).First()", "2")]
        [InlineData("Task.Run(() => 8).Result", "8")]
        [InlineData("Task.Run(() => Console.WriteLine()).Wait();\n9", "9")]
        [InlineData("Func<int> t = () => throw new InvalidOperationException(\"t\");\ntry { return t(); } catch (InvalidOperationException e) { return e.Message; }", "t")]
        [InlineData("Func<int, int> s = static x => x + 1;\ns(1)", "2")]
        [InlineData("using TI = System.Threading.Tasks.Task<int>;\nclass C { public async TI F() => await Task.FromResult(1); }\nawait new C().F()", "1")]
        [InlineData("int[] arr = { 1 };\nFunc<int> one = () => arr[0];\n$\"{one()}{((Func<int>)(() => 2))()}\"", "12")]
        // Work handed to a thread or task start is wrapped, cast to the delegate type it had: the same overload, the same value.
        [InlineData("var x = 0;\nvar t = new System.Threading.Thread(() => x = 5);\nt.Start();\nt.Join();\nx", "5")]
        [InlineData("var x = 0;\nSystem.Threading.Thread t = new(() => { x = 6; });\nt.Start();\nt.Join();\nx", "6")]
        [InlineData("var done = new System.Threading.ManualResetEvent(false);\nvar x = 0;\nSystem.Threading.ThreadPool.QueueUserWorkItem(o => { x = (int)o!; done.Set(); }, 7);\ndone.WaitOne();\nx", "7")]
        [InlineData("Task.Run(async () => { await Task.Delay(1); return 8; }).Result", "8")]
        [InlineData("await Task.Run(async () => await Task.FromResult(9))", "9")]
        [InlineData("Task.Factory.StartNew(o => (int)o! * 2, 5).Result", "10")]
        [InlineData("Task.Run(() => new { A = 11 }).Result.A", "11")]
        [InlineData("int Twelve() => 12;\nTask.Run(Twelve).Result", "12")]
        [InlineData("Func<int> f = null;\ntry { Task.Run(f); } catch (ArgumentNullException) { return 13; }\nreturn 0;", "13")]
        [InlineData("try { Task.Run(() => { throw new InvalidOperationException(\"w\"); }).Wait(); } catch (AggregateException e) { return e.InnerException!.Message; }\nreturn \"\";", "w")]
        [InlineData("System.Linq.Expressions.Expression<Func<Task<int>>> e = () => Task.Run(() => 1);\ne.Body.ToString().Contains(\"Worker\")", "False")]
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
        [InlineData("Func<int, int> f = x => x + 1; var z = undefinedThing;", 1, 40)]
        [InlineData("Func<int, int> f = x =>\n    x + 1;\nvar z = undefinedThing;", 3, 9)]
        public void DiagnosticsKeepTheAgentsCoordinatesAroundGuards(string code, int line, int column)
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = code, AutoImport = false });
            Assert.Equal(ErrorKind.Compile, result.ErrorKind);
            var d = Assert.Single(result.Diagnostics, x => x.Id == "CS0103" && x.Line == line && x.Column == column);
            Assert.Equal("snippet.cs", d.File);
        }

        [Fact]
        public void StackFrameInAGuardedLambdaKeepsTheAgentsLine()
        {
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = "var zero = 0;\nFunc<int, int> f = x =>\n    x / zero;\nf(1)" });
            Assert.Equal(ErrorKind.Runtime, result.ErrorKind);
            Assert.StartsWith("System.DivideByZeroException", result.Error);
            Assert.Contains("snippet.cs:line 3", result.Error);
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
