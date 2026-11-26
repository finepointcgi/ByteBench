using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ByteBench.Data;

namespace ByteBench.Services.Tests;

/// <summary>One test of an automated report. <see cref="FullName"/> is what <see cref="TestCase.AutomatedName"/> is matched against.</summary>
public sealed record ParsedTest(string Name, string? ClassName, string? Suite, TestResultStatus Status, long? DurationMs, string? Message)
{
    public string FullName => string.IsNullOrEmpty(ClassName) || Name.StartsWith(ClassName + ".", StringComparison.Ordinal) ? Name : ClassName + "." + Name;
}

public sealed record ParsedTestReport(string Format, IReadOnlyList<ParsedTest> Tests, bool Truncated);

/// <summary>
/// Reads automated test reports: JUnit XML (Maven, Gradle, pytest, Jest, ...), TRX (<c>dotnet test --logger trx</c>), xUnit v2 XML and
/// CTRF JSON. Reports come from CI jobs and the API, so they are untrusted: a document with a DOCTYPE (XXE, entity expansion
/// "billion laughs") is refused outright, no external resource is ever resolved, the text is capped at <see cref="MaxBytes"/>,
/// the test count at <see cref="MaxTests"/> and every message at <see cref="MaxMessageChars"/>.
/// </summary>
public static class TestReportParser
{
    public const string JUnit = "junit", Trx = "trx", XUnit = "xunit", Ctrf = "ctrf";
    public static readonly IReadOnlyList<string> Formats = [JUnit, Trx, XUnit, Ctrf];
    public const int MaxBytes = 16 * 1024 * 1024;
    public const int MaxTests = 50_000;
    public const int MaxMessageChars = 4000;
    public const int MaxNameChars = 400;

    public static bool IsFormat(string? f) => f != null && Formats.Contains(f.ToLowerInvariant());

    /// <summary>Parses <paramref name="content"/> as <paramref name="format"/> (junit, trx, xunit, ctrf), or sniffs it when the format is null or "auto".</summary>
    /// <exception cref="InvalidDataException">Not a valid report (the message is safe to show).</exception>
    public static ParsedTestReport Parse(string content, string? format = null)
    {
        if (content.Length > MaxBytes) throw new InvalidDataException($"The report is larger than {MaxBytes / (1024 * 1024)} MB.");
        content = content.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (content.Length == 0) throw new InvalidDataException("The report is empty.");
        var f = string.IsNullOrWhiteSpace(format) || format.Equals("auto", StringComparison.OrdinalIgnoreCase) ? Sniff(content) : format.Trim().ToLowerInvariant();
        return f switch
        {
            Ctrf => ParseCtrf(content),
            JUnit or Trx or XUnit => ParseXml(content, f),
            _ => throw new InvalidDataException($"Unknown report format '{format}'. Use junit, trx, xunit or ctrf."),
        };
    }

    private static string Sniff(string content)
    {
        if (content[0] == '{') return Ctrf;
        if (content[0] != '<') throw new InvalidDataException("The report is neither XML nor JSON.");
        var head = content[..Math.Min(content.Length, 4096)];
        if (head.Contains("<TestRun", StringComparison.Ordinal)) return Trx;
        if (head.Contains("<assemblies", StringComparison.Ordinal) || head.Contains("<assembly ", StringComparison.Ordinal)) return XUnit;
        return JUnit;
    }

    private static XDocument LoadXml(string content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, // no DOCTYPE at all: nothing to expand, nothing to fetch
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaxBytes + 1024,
            IgnoreComments = true, IgnoreProcessingInstructions = true,
            CloseInput = true,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex) when (ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The XML contains a DOCTYPE, which is not allowed.");
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The XML is not well-formed (line {ex.LineNumber}).");
        }
    }

    private static ParsedTestReport ParseXml(string content, string format)
    {
        var doc = LoadXml(content);
        var root = doc.Root ?? throw new InvalidDataException("The report has no root element.");
        var tests = new List<ParsedTest>();
        var truncated = false;
        switch (format)
        {
            case JUnit:
                if (root.Name.LocalName is not ("testsuites" or "testsuite")) throw new InvalidDataException("Not a JUnit report (expected <testsuites> or <testsuite>).");
                foreach (var tc in root.Descendants().Where(e => e.Name.LocalName == "testcase"))
                {
                    if (!Add(tests, JUnitCase(tc))) { truncated = true; break; }
                }
                break;
            case Trx:
                if (root.Name.LocalName != "TestRun") throw new InvalidDataException("Not a TRX report (expected <TestRun>).");
                var classes = root.Descendants().Where(e => e.Name.LocalName == "UnitTest")
                    .Select(u => (Id: (string?)u.Attribute("id"), Class: (string?)u.Descendants().FirstOrDefault(d => d.Name.LocalName == "TestMethod")?.Attribute("className")))
                    .Where(x => x.Id != null).GroupBy(x => x.Id!).ToDictionary(g => g.Key, g => g.First().Class);
                foreach (var r in root.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
                {
                    if (!Add(tests, TrxResult(r, classes))) { truncated = true; break; }
                }
                break;
            default:
                if (root.Name.LocalName is not ("assemblies" or "assembly")) throw new InvalidDataException("Not an xUnit report (expected <assemblies>).");
                foreach (var t in root.Descendants().Where(e => e.Name.LocalName == "test"))
                {
                    if (!Add(tests, XUnitTest(t))) { truncated = true; break; }
                }
                break;
        }
        return new ParsedTestReport(format, tests, truncated);
    }

    private static bool Add(List<ParsedTest> list, ParsedTest? t)
    {
        if (t == null) return true;
        if (list.Count >= MaxTests) return false;
        list.Add(t);
        return true;
    }

    private static string Cut(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];

    private static string? Message(params string?[] parts)
    {
        var text = string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return text.Length == 0 ? null : Cut(text, MaxMessageChars);
    }

    private static long? Seconds(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 && d < 1e9 ? (long)Math.Round(d * 1000) : null;

    private static ParsedTest? JUnitCase(XElement tc)
    {
        var name = Cut((string?)tc.Attribute("name"), MaxNameChars);
        if (name.Length == 0) return null;
        var status = TestResultStatus.Passed;
        string? message = null;
        foreach (var child in tc.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "failure" or "error":
                    status = TestResultStatus.Failed;
                    message = Message((string?)child.Attribute("message"), child.Value);
                    break;
                case "skipped" when status != TestResultStatus.Failed:
                    status = TestResultStatus.Skipped;
                    message = Message((string?)child.Attribute("message"));
                    break;
            }
        }
        if (status == TestResultStatus.Passed && (string?)tc.Attribute("status") is { } st && st.Equals("skipped", StringComparison.OrdinalIgnoreCase)) status = TestResultStatus.Skipped;
        var suite = (string?)tc.Ancestors().FirstOrDefault(a => a.Name.LocalName == "testsuite")?.Attribute("name");
        return new ParsedTest(name, Cut((string?)tc.Attribute("classname"), MaxNameChars) is { Length: > 0 } c ? c : null, suite == null ? null : Cut(suite, 200),
            status, Seconds((string?)tc.Attribute("time")), message);
    }

    private static ParsedTest? TrxResult(XElement r, Dictionary<string, string?> classes)
    {
        var name = Cut((string?)r.Attribute("testName"), MaxNameChars);
        if (name.Length == 0) return null;
        var outcome = ((string?)r.Attribute("outcome") ?? "").ToLowerInvariant();
        var status = outcome switch
        {
            "passed" or "passedbutrunabort" => TestResultStatus.Passed,
            "failed" or "error" or "timeout" or "aborted" => TestResultStatus.Failed,
            "notexecuted" or "inconclusive" or "pending" or "notrunnable" or "disconnected" => TestResultStatus.Skipped,
            "warning" => TestResultStatus.Passed,
            _ => TestResultStatus.Failed,
        };
        long? ms = TimeSpan.TryParse((string?)r.Attribute("duration"), CultureInfo.InvariantCulture, out var span) ? (long)span.TotalMilliseconds : null;
        var error = r.Descendants().FirstOrDefault(d => d.Name.LocalName == "ErrorInfo");
        var msg = Message(error?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value,
            error?.Elements().FirstOrDefault(e => e.Name.LocalName == "StackTrace")?.Value);
        var cls = (string?)r.Attribute("testId") is { } id && classes.TryGetValue(id, out var c) ? c : null;
        return new ParsedTest(name, cls == null ? null : Cut(cls, MaxNameChars), null, status, ms, msg);
    }

    private static ParsedTest? XUnitTest(XElement t)
    {
        var name = Cut((string?)t.Attribute("name"), MaxNameChars);
        if (name.Length == 0) return null;
        var status = ((string?)t.Attribute("result") ?? "").ToLowerInvariant() switch
        {
            "pass" => TestResultStatus.Passed,
            "skip" or "notrun" => TestResultStatus.Skipped,
            _ => TestResultStatus.Failed,
        };
        var failure = t.Elements().FirstOrDefault(e => e.Name.LocalName == "failure");
        var msg = Message(failure?.Elements().FirstOrDefault(e => e.Name.LocalName == "message")?.Value,
            failure?.Elements().FirstOrDefault(e => e.Name.LocalName == "stack-trace")?.Value,
            t.Elements().FirstOrDefault(e => e.Name.LocalName == "reason")?.Value);
        var type = (string?)t.Attribute("type");
        var method = (string?)t.Attribute("method");
        // xUnit names tests "Namespace.Class.Method"; with type + method present use the compact form so it matches TRX and JUnit.
        if (type != null && method != null) { name = Cut(method, MaxNameChars); return new ParsedTest(name, Cut(type, MaxNameChars), null, status, Seconds((string?)t.Attribute("time")), msg); }
        return new ParsedTest(name, null, null, status, Seconds((string?)t.Attribute("time")), msg);
    }

    private static ParsedTestReport ParseCtrf(string content)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { throw new InvalidDataException("The CTRF report is not valid JSON."); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Object || !results.TryGetProperty("tests", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Not a CTRF report (expected results.tests).");
            var tests = new List<ParsedTest>();
            var truncated = false;
            foreach (var t in arr.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                var name = Cut(Str(t, "name"), MaxNameChars);
                if (name.Length == 0) continue;
                if (tests.Count >= MaxTests) { truncated = true; break; }
                var status = (Str(t, "status") ?? "").ToLowerInvariant() switch
                {
                    "passed" => TestResultStatus.Passed,
                    "skipped" or "pending" or "other" => TestResultStatus.Skipped,
                    _ => TestResultStatus.Failed,
                };
                long? ms = t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var dv) && dv >= 0 && dv < 1e12 ? (long)dv : null;
                var suite = t.TryGetProperty("suite", out var s) ? s.ValueKind == JsonValueKind.Array ? string.Join("/", s.EnumerateArray().Select(x => x.ToString())) : s.ToString() : null;
                tests.Add(new ParsedTest(name, null, suite is { Length: > 0 } ? Cut(suite, 200) : null, status, ms, Message(Str(t, "message"), Str(t, "trace"))));
            }
            return new ParsedTestReport(Ctrf, tests, truncated);
        }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
