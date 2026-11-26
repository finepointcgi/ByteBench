using System.Text;
using ByteBench.Data;
using ByteBench.Services.Tests;

namespace ByteBench.Tests;

/// <summary>Parsing of JUnit, TRX, xUnit and CTRF reports, and what happens with hostile ones. No database, no HTTP.</summary>
public class TestReportParserTests
{
    private const string JUnit = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites>
          <testsuite name="Checkout" tests="4">
            <testcase classname="shop.CartTests" name="adds_item" time="0.125"/>
            <testcase classname="shop.CartTests" name="removes_item" time="1.5"><failure message="expected 0 got 1">stack here</failure></testcase>
            <testcase classname="shop.PayTests" name="timeout" time="2"><error message="boom"/></testcase>
            <testcase classname="shop.PayTests" name="later"><skipped message="not yet"/></testcase>
          </testsuite>
        </testsuites>
        """;

    [Fact]
    public void JUnit_maps_pass_fail_error_and_skip_with_durations_and_messages()
    {
        var r = TestReportParser.Parse(JUnit);
        Assert.Equal("junit", r.Format);
        Assert.Equal(4, r.Tests.Count);
        Assert.Equal(TestResultStatus.Passed, r.Tests[0].Status);
        Assert.Equal(125, r.Tests[0].DurationMs);
        Assert.Equal("shop.CartTests.adds_item", r.Tests[0].FullName);
        Assert.Equal(TestResultStatus.Failed, r.Tests[1].Status);
        Assert.Contains("expected 0 got 1", r.Tests[1].Message);
        Assert.Contains("stack here", r.Tests[1].Message);
        Assert.Equal(TestResultStatus.Failed, r.Tests[2].Status); // <error> counts as a failure
        Assert.Equal(TestResultStatus.Skipped, r.Tests[3].Status);
        Assert.Equal("Checkout", r.Tests[0].Suite);
    }

    [Fact]
    public void A_single_testsuite_root_works_and_format_is_detected()
    {
        var r = TestReportParser.Parse("""<testsuite name="s"><testcase name="a"/></testsuite>""", "auto");
        Assert.Single(r.Tests);
        Assert.Equal("junit", r.Format);
    }

    [Fact]
    public void Trx_reads_outcomes_and_class_names()
    {
        const string trx = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="Adds" outcome="Passed" duration="00:00:00.0420000"/>
                <UnitTestResult testId="2" testName="Fails" outcome="Failed" duration="00:00:01">
                  <Output><ErrorInfo><Message>Assert.Equal failed</Message><StackTrace>at X.Y()</StackTrace></ErrorInfo></Output>
                </UnitTestResult>
                <UnitTestResult testId="3" testName="Ignored" outcome="NotExecuted"/>
              </Results>
              <TestDefinitions>
                <UnitTest id="1"><TestMethod className="App.Tests.MathTests" name="Adds"/></UnitTest>
                <UnitTest id="2"><TestMethod className="App.Tests.MathTests" name="Fails"/></UnitTest>
              </TestDefinitions>
            </TestRun>
            """;
        var r = TestReportParser.Parse(trx);
        Assert.Equal("trx", r.Format);
        Assert.Equal(3, r.Tests.Count);
        Assert.Equal("App.Tests.MathTests.Adds", r.Tests[0].FullName);
        Assert.Equal(42, r.Tests[0].DurationMs);
        Assert.Equal(TestResultStatus.Failed, r.Tests[1].Status);
        Assert.Contains("Assert.Equal failed", r.Tests[1].Message);
        Assert.Equal(TestResultStatus.Skipped, r.Tests[2].Status);
    }

    [Fact]
    public void XUnit_and_CTRF_are_read()
    {
        var x = TestReportParser.Parse("""
            <assemblies><assembly name="a"><collection><test name="N.C.M" type="N.C" method="M" result="Fail" time="0.5"><failure><message>bad</message></failure></test>
            <test name="N.C.Skipped" type="N.C" method="Skipped" result="Skip"><reason>no</reason></test></collection></assembly></assemblies>
            """);
        Assert.Equal("xunit", x.Format);
        Assert.Equal("N.C.M", x.Tests[0].FullName);
        Assert.Equal(TestResultStatus.Failed, x.Tests[0].Status);
        Assert.Equal(TestResultStatus.Skipped, x.Tests[1].Status);

        var c = TestReportParser.Parse("""{"results":{"tests":[{"name":"a","status":"passed","duration":12},{"name":"b","status":"failed","message":"nope"},{"name":"c","status":"pending"}]}}""");
        Assert.Equal("ctrf", c.Format);
        Assert.Equal(new[] { TestResultStatus.Passed, TestResultStatus.Failed, TestResultStatus.Skipped }, c.Tests.Select(t => t.Status));
        Assert.Equal(12, c.Tests[0].DurationMs);
    }

    [Fact]
    public void Doctype_with_external_entity_is_refused_and_nothing_is_read()
    {
        var secret = Path.Combine(Path.GetTempPath(), "bytebench-xxe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secret, "TOP-SECRET-CONTENT");
        try
        {
            var xxe = $"""<?xml version="1.0"?><!DOCTYPE r [<!ENTITY x SYSTEM "file://{secret}">]><testsuite><testcase name="&x;"/></testsuite>""";
            var ex = Assert.Throws<InvalidDataException>(() => TestReportParser.Parse(xxe, "junit"));
            Assert.DoesNotContain("TOP-SECRET", ex.Message);
            Assert.Contains("DOCTYPE", ex.Message);
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public void Entity_expansion_bombs_are_refused_quickly()
    {
        var bomb = new StringBuilder("""<?xml version="1.0"?><!DOCTYPE lolz [<!ENTITY lol "lol">""");
        var prev = "lol";
        for (var i = 1; i <= 9; i++)
        {
            bomb.Append($"<!ENTITY lol{i} \"{string.Concat(Enumerable.Repeat($"&{prev};", 10))}\">");
            prev = "lol" + i;
        }
        bomb.Append($"]><testsuite><testcase name=\"&{prev};\"/></testsuite>");
        var started = DateTime.UtcNow;
        Assert.Throws<InvalidDataException>(() => TestReportParser.Parse(bomb.ToString(), "junit"));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Oversized_reports_and_test_floods_are_capped()
    {
        var big = new string('a', TestReportParser.MaxBytes + 10);
        Assert.Throws<InvalidDataException>(() => TestReportParser.Parse("<testsuite>" + big + "</testsuite>", "junit"));

        var sb = new StringBuilder("<testsuite>");
        for (var i = 0; i < TestReportParser.MaxTests + 5; i++) sb.Append("<testcase name=\"t").Append(i).Append("\"/>");
        sb.Append("</testsuite>");
        var r = TestReportParser.Parse(sb.ToString(), "junit");
        Assert.Equal(TestReportParser.MaxTests, r.Tests.Count);
        Assert.True(r.Truncated);

        var longMsg = "<testsuite><testcase name=\"a\"><failure message=\"" + new string('x', 100_000) + "\"/></testcase></testsuite>";
        Assert.True(TestReportParser.Parse(longMsg, "junit").Tests[0].Message!.Length <= TestReportParser.MaxMessageChars);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a report")]
    [InlineData("<html><body/></html>")]
    [InlineData("<testsuite><testcase")]
    [InlineData("{\"nope\":1}")]
    public void Garbage_is_refused_with_a_clean_error(string content)
    {
        Assert.Throws<InvalidDataException>(() => TestReportParser.Parse(content));
    }

    [Fact]
    public void Unknown_format_names_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => TestReportParser.Parse("<testsuite/>", "tap"));
    }
}
