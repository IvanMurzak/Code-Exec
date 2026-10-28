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
            if (args.Length != 2 || args[0] != Flag)
            {
                Console.Error.WriteLine("usage: CodeExec.Tests " + Flag + " <base64 snippet>");
                return 2;
            }
            var code = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
            var result = Fixtures.Runner().Execute(new ExecuteRequest { Code = code });
            Console.Out.Write(result.ErrorKind + "\n" + result.Error);
            return 0;
        }

        /// <summary>The child's <see cref="ErrorKind"/> and error text; fails when the child did not end normally.</summary>
        public static (string ErrorKind, string Error) Execute(string code)
        {
            // The build output, not Assembly.Location: the .NET Framework test host runs a shadow copy without its dependencies.
            var assembly = Path.Combine(AppContext.BaseDirectory, typeof(ChildProcess).Assembly.GetName().Name + ".dll");
            var start = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var snippet = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
#if NETFRAMEWORK
            start.FileName = Path.ChangeExtension(assembly, ".exe");
            start.Arguments = Flag + " " + snippet;
#else
            start.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
            start.Arguments = "exec \"" + assembly + "\" " + Flag + " " + snippet;
#endif
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                process.Kill();
                throw new TimeoutException("the child process did not end within 120 s");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"the child process ended with exit code {process.ExitCode}: {stdout} {stderr.Result}");
            var split = stdout.IndexOf('\n');
            return (stdout.Substring(0, split), stdout.Substring(split + 1));
        }
    }
}
