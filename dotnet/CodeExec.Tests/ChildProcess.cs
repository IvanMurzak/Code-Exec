using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using com.IvanMurzak.CodeExec;

namespace CodeExecTests
{
    /// <summary>
    /// Runs one snippet in a child process (this test assembly, started as a program), so code that would end the process
    /// (a stack overflow the guards miss) fails its test instead of taking the whole test run down.
    /// </summary>
    static class ChildProcess
    {
        const string Flag = "--codeexec-run";

        public static int Main(string[] args)
        {
            if (args.Length != 4 || args[0] != Flag)
            {
                Console.Error.WriteLine("usage: CodeExec.Tests " + Flag + " <base64 snippet> <timeoutMs> <lingerMs>");
                return 2;
            }
            var code = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
            var runner = Fixtures.Runner();
            var result = runner.Execute(new ExecuteRequest { Code = code, TimeoutMs = int.Parse(args[2]) });
            // The process stays usable: another snippet (with a wait and a loop of its own) runs normally afterwards.
            var after = runner.Execute(new ExecuteRequest { Code = "System.Threading.Thread.Sleep(100);\nvar s = 0;\nfor (var i = 0; i < 3; i++) s += i;\ns", TimeoutMs = 10_000 });
            var afterText = after.Success ? Convert.ToString(after.Value) : after.ErrorKind + ": " + after.Error;
            Console.Out.Write(result.ErrorKind + "\n" + result.ExecuteMs + "\n" + afterText?.Replace("\n", " ") + "\n" + result.Error);
            Console.Out.Flush();
            System.Threading.Thread.Sleep(int.Parse(args[3])); // what the snippet left running gets the time to end the process
            return 0;
        }

        /// <summary>
        /// The child's <see cref="ErrorKind"/> and error text; fails when the child did not end normally, also when what the
        /// snippet left running ends it within <paramref name="lingerMs"/> or keeps it from exiting.
        /// </summary>
        public static (string ErrorKind, string Error) Execute(string code, int timeoutMs = 0, int lingerMs = 0)
        {
            var run = Run(code, timeoutMs, lingerMs);
            return (run.ErrorKind, run.Error);
        }

        /// <summary>
        /// Like <see cref="Execute"/>, plus how long the snippet ran (<see cref="ExecuteResult.ExecuteMs"/>) and what a
        /// snippet run after it in the same process returned (<c>3</c> when the process is still usable).
        /// </summary>
        public static (string ErrorKind, string Error, long ExecuteMs, string After) Run(string code, int timeoutMs = 0, int lingerMs = 0)
        {
            // The build output, not Assembly.Location: the .NET Framework test host runs a shadow copy without its dependencies.
            var assembly = Fixtures.Path(typeof(ChildProcess).Assembly.GetName().Name + ".dll");
            var start = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var snippet = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
#if NETFRAMEWORK
            start.FileName = Path.ChangeExtension(assembly, ".exe");
            start.Arguments = Flag + " " + snippet + " " + timeoutMs + " " + lingerMs;
#else
            start.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
            start.Arguments = "exec \"" + assembly + "\" " + Flag + " " + snippet + " " + timeoutMs + " " + lingerMs;
#endif
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(120_000))
            {
                process.Kill();
                throw new TimeoutException("the child process did not end within 120 s");
            }
            var stdout = output.Result;
            var lines = stdout.Split(new[] { '\n' }, 4);
            if (process.ExitCode != 0 || lines.Length < 4)
                throw new InvalidOperationException($"the child process ended with exit code {process.ExitCode}: {stdout} {stderr.Result}");
            return (lines[0], lines[3], long.Parse(lines[1]), lines[2]);
        }
    }
}
