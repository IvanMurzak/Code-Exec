using System;
using com.IvanMurzak.CodeExec;
using com.IvanMurzak.CodeExec.Roslyn;

var runner = new CodeRunner(new RoslynCSharpCompiler());
var result = runner.Execute(new ExecuteRequest { Code = "var x = 20;\nx + 22" });
Console.WriteLine($"roslyn={runner.CompilerVersion} success={result.Success} value={result.Value}");
return result.Success && (string)result.Value == "42" ? 0 : 1;
