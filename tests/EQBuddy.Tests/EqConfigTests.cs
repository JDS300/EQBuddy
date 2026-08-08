using EQBuddy.Core;

namespace EQBuddy.Tests;

public class EqConfigTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eqbuddy-test-").FullName;
    private string LogsDir => Path.Combine(_root, "Logs");
    private string IniPath => Path.Combine(_root, "eqclient.ini");

    public EqConfigTests() => Directory.CreateDirectory(LogsDir);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void EnableLoggingFlipsLogAndPreservesEverythingElse()
    {
        File.WriteAllLines(IniPath,
        [
            "[Defaults]",
            "Log=0",
            "SomeSetting=5",
            "[Options]",
            "LogOutOverParcelLimitConfirm=1",
        ]);

        Assert.True(EqConfig.EnsureLoggingEnabled(LogsDir, ignoreGameCheck: true));

        var lines = File.ReadAllLines(IniPath);
        Assert.Contains("Log=1", lines);
        Assert.Contains("SomeSetting=5", lines);
        Assert.Contains("LogOutOverParcelLimitConfirm=1", lines);   // "Log"-prefixed key untouched
        Assert.DoesNotContain("Log=0", lines);
    }

    [Fact]
    public void EnableLoggingIsSectionAware()
    {
        // A "Log=" key in another section must NOT be touched; the [Defaults]
        // section gains Log=1.
        File.WriteAllLines(IniPath,
        [
            "[Video]",
            "Log=0",
            "[Defaults]",
            "SomeSetting=5",
        ]);

        Assert.True(EqConfig.EnsureLoggingEnabled(LogsDir, ignoreGameCheck: true));

        var lines = File.ReadAllLines(IniPath);
        Assert.Equal("[Video]", lines[0]);
        Assert.Equal("Log=0", lines[1]);          // untouched — wrong section
        Assert.Equal("[Defaults]", lines[2]);
        Assert.Equal("Log=1", lines[3]);          // inserted in the right section
        Assert.Contains("SomeSetting=5", lines);
    }

    [Fact]
    public void EnableLoggingIsIdempotent()
    {
        File.WriteAllLines(IniPath, ["[Defaults]", "Log=1"]);
        Assert.False(EqConfig.EnsureLoggingEnabled(LogsDir, ignoreGameCheck: true));
    }

    [Fact]
    public void TruncatesOnlyStaleLogs()
    {
        var stale = Path.Combine(LogsDir, "eqlog_Old_freeport.txt");
        var fresh = Path.Combine(LogsDir, "eqlog_New_qeynos.txt");
        File.WriteAllText(stale, new string('x', 5000));
        File.WriteAllText(fresh, "recent line");
        File.SetLastWriteTime(stale, DateTime.Now.AddHours(-2));

        var count = EqConfig.TruncateStaleLogs(LogsDir, TimeSpan.FromMinutes(60), ignoreGameCheck: true);

        Assert.Equal(1, count);
        Assert.Equal(0, new FileInfo(stale).Length);
        Assert.Equal("recent line", File.ReadAllText(fresh));
    }

    [Fact]
    public void ArchivesBeforeTruncatingWhenAsked()
    {
        var stale = Path.Combine(LogsDir, "eqlog_Dranak_legends.txt");
        File.WriteAllText(stale, "a whole session of lines");
        var ended = new DateTime(2026, 6, 7, 14, 32, 19);
        File.SetLastWriteTime(stale, ended);

        var count = EqConfig.TruncateStaleLogs(LogsDir, TimeSpan.FromMinutes(60),
            ignoreGameCheck: true, archive: true);

        Assert.Equal(1, count);
        Assert.Equal(0, new FileInfo(stale).Length);
        // joeymavity's exact naming (discussion #52): stamp is the session's END time,
        // and the copy lives in archive\ so the next sweep can never eat it.
        var archived = Path.Combine(LogsDir, "archive", "eqlog_Dranak_legends_20260607143219.txt");
        Assert.Equal("a whole session of lines", File.ReadAllText(archived));

        // A second session ending the same second keeps both copies.
        File.WriteAllText(stale, "second session");
        File.SetLastWriteTime(stale, ended);
        EqConfig.TruncateStaleLogs(LogsDir, TimeSpan.FromMinutes(60),
            ignoreGameCheck: true, archive: true);
        Assert.Equal("second session", File.ReadAllText(
            Path.Combine(LogsDir, "archive", "eqlog_Dranak_legends_20260607143219-2.txt")));
    }

    [Fact]
    public void ArchiveOffLeavesNoCopies()
    {
        var stale = Path.Combine(LogsDir, "eqlog_Dranak_legends.txt");
        File.WriteAllText(stale, "lines");
        File.SetLastWriteTime(stale, DateTime.Now.AddHours(-2));

        EqConfig.TruncateStaleLogs(LogsDir, TimeSpan.FromMinutes(60), ignoreGameCheck: true);

        Assert.False(Directory.Exists(Path.Combine(LogsDir, "archive")));
    }
}
