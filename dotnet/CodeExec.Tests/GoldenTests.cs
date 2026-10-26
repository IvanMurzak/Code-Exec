using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using com.IvanMurzak.CodeExec;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)] // Console capture is process-wide

namespace CodeExecTests
{
    /// <summary>Runs fixtures/golden/csharp.json — the contract shared with the Python runtime.</summary>
    public class GoldenTests
    {
        static readonly JsonElement Fixture = JsonDocument.Parse(File.ReadAllText(Fixtures.Path("fixtures/golden/csharp.json"))).RootElement;

        public static IEnumerable<object[]> Cases() =>
            Fixture.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()! });

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task Golden(string name)
        {
            var c = Fixture.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
            var request = new ExecuteRequest
            {
                Code = c.GetProperty("code").GetString()!,
                Usings = Strings(c, "usings"),
                Include = Strings(c, "include"),
            };
            if (c.TryGetProperty("maxValueChars", out var maxValue)) request.MaxValueChars = maxValue.GetInt32();
            if (c.TryGetProperty("maxLogChars", out var maxLogs)) request.MaxLogChars = maxLogs.GetInt32();
            if (c.TryGetProperty("maxLogLineChars", out var maxLine)) request.MaxLogLineChars = maxLine.GetInt32();
            if (c.TryGetProperty("maxErrorChars", out var maxError)) request.MaxErrorChars = maxError.GetInt32();
            if (c.TryGetProperty("entry", out var entry))
            {
                request.EntryType = entry.GetProperty("type").GetString();
                request.EntryMethod = entry.GetProperty("method").GetString();
            }

            var runner = Fixtures.Runner(Fixtures.Path(Fixture.GetProperty("includeRoot").GetString()!));
            var result = await runner.ExecuteAsync(request);
            var expect = c.GetProperty("expect");
            var shownValue = result.Value?.ToString();
            if (shownValue != null && shownValue.Length > 300)
                shownValue = shownValue.Substring(0, 300) + "...";
            var describe = $"{name}: success={result.Success} kind={result.ErrorKind} value={shownValue} error={result.Error} diagnostics={string.Join(" | ", result.Diagnostics)}";

            Assert.True(expect.GetProperty("success").GetBoolean() == result.Success, describe);
            if (expect.TryGetProperty("value", out var value))
            {
                if (value.ValueKind == JsonValueKind.Null)
                    Assert.True(result.Value == null, describe);
                else
                    Assert.True(value.GetString() == result.Value?.ToString(), describe);
            }
            if (expect.TryGetProperty("returnType", out var returnType))
                Assert.True(returnType.GetString() == result.ReturnType, describe + " returnType=" + result.ReturnType);
            if (expect.TryGetProperty("errorKind", out var kind))
                Assert.True(kind.GetString() == result.ErrorKind.ToString(), describe);
            if (expect.TryGetProperty("errorContains", out var contains))
                Assert.True(result.Error != null && result.Error.Contains(contains.GetString()!), describe);
            if (expect.TryGetProperty("diagnostic", out var diagnostic))
            {
                var file = diagnostic.GetProperty("file").GetString();
                var line = diagnostic.GetProperty("line").GetInt32();
                Assert.True(result.Diagnostics.Any(d => d.File == file && d.Line == line), describe);
            }
            if (expect.TryGetProperty("autoImported", out var imported))
                Assert.Equal(imported.EnumerateArray().Select(e => e.GetString()!).ToArray(), result.AutoImported.ToArray());
            if (expect.TryGetProperty("logsContain", out var logs))
            {
                foreach (var text in logs.EnumerateArray())
                    Assert.True(result.Logs.Any(l => l.Message.Contains(text.GetString()!)), describe + " logs=" + string.Join(" | ", result.Logs));
            }
            if (expect.TryGetProperty("logs", out var exactLogs))
                Assert.Equal(exactLogs.EnumerateArray().Select(e => e.GetString()!).ToArray(), result.Logs.Select(l => l.Message).ToArray());
            if (expect.TryGetProperty("truncated", out var truncated))
                Assert.Equal(truncated.EnumerateArray().Select(e => e.GetString()!).ToArray(), result.Truncated.ToArray());
            if (expect.TryGetProperty("valueEndsWith", out var endsWith))
                Assert.True(result.Value is string v && v.EndsWith(endsWith.GetString()!, StringComparison.Ordinal), describe);
            if (expect.TryGetProperty("valueLength", out var length))
                Assert.True(result.Value is string v && v.Length == length.GetInt32(), describe);
        }

        static string[] Strings(JsonElement c, string property) =>
            c.TryGetProperty(property, out var array) ? array.EnumerateArray().Select(e => e.GetString()!).ToArray() : Array.Empty<string>();
    }

    static class Fixtures
    {
        public static string Path(string relative) => System.IO.Path.Combine(AppContext.BaseDirectory, relative);

        public static CodeRunner Runner(string? includeRoot = null, Action<CodeRunnerOptions>? configure = null)
        {
            var options = new CodeRunnerOptions { IncludeRoot = includeRoot ?? AppContext.BaseDirectory };
            configure?.Invoke(options);
            return new CodeRunner(new com.IvanMurzak.CodeExec.Roslyn.RoslynCSharpCompiler(), options);
        }
    }
}
