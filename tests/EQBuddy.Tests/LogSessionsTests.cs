using System.Text;
using EQBuddy.Core;

namespace EQBuddy.Tests;

public class LogSessionsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Directory.CreateTempSubdirectory("eqbuddy-sessions-").FullName, "eqlog_Dranak_legends.txt");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);

    private static string Line(int day, int hour, int min, string msg) =>
        $"[{new DateTime(2026, 8, day):ddd} Aug {day:00} {hour:00}:{min:00}:00 2026] {msg}";

    [Fact]
    public void SplitsOnTheSessionGapAndRangesRoundTrip()
    {
        // Three sessions: morning, evening (2h later), and a short one next day.
        var lines = new[]
        {
            Line(8, 9, 0, "You have entered Crushbone."),
            "garbage line without a timestamp",          // sticks to the session it's in
            Line(8, 9, 30, "You gain experience!!"),
            Line(8, 11, 0, "You have entered Greater Faydark."),   // 90 min gap -> new
            Line(8, 11, 5, "--You have looted a Dragoon Dirk from an orc's corpse.--"),
            Line(9, 8, 0, "You have entered Blackburrow."),        // next day -> new
        };
        File.WriteAllText(_path, string.Join("\r\n", lines) + "\r\n", Encoding.Latin1);

        var sessions = LogSessions.Scan(_path);

        Assert.Equal(3, sessions.Count);
        Assert.Equal(new DateTime(2026, 8, 8, 9, 0, 0), sessions[0].Start);
        Assert.Equal(new DateTime(2026, 8, 8, 9, 30, 0), sessions[0].End);
        Assert.Equal(new DateTime(2026, 8, 8, 11, 0, 0), sessions[1].Start);
        Assert.Equal(new DateTime(2026, 8, 9, 8, 0, 0), sessions[2].Start);

        // The ranges tile the file exactly: each starts where the previous ended,
        // the first at 0, the last at EOF — nothing lost between sessions.
        Assert.Equal(0, sessions[0].StartOffset);
        Assert.Equal(sessions[0].EndOffset, sessions[1].StartOffset);
        Assert.Equal(sessions[1].EndOffset, sessions[2].StartOffset);
        Assert.Equal(new FileInfo(_path).Length, sessions[2].EndOffset);

        // And a range really is its session's lines: session 2's bytes hold the
        // loot line, and neither neighbor's zone line.
        var bytes = File.ReadAllBytes(_path);
        var mid = Encoding.Latin1.GetString(bytes,
            (int)sessions[1].StartOffset, (int)(sessions[1].EndOffset - sessions[1].StartOffset));
        Assert.Contains("Dragoon Dirk", mid);
        Assert.DoesNotContain("Crushbone", mid);
        Assert.DoesNotContain("Blackburrow", mid);
    }

    [Fact]
    public void SingleSessionAndEmptyFilesStayBoring()
    {
        File.WriteAllText(_path, Line(8, 9, 0, "You gain experience!!") + "\r\n", Encoding.Latin1);
        var one = LogSessions.Scan(_path);
        Assert.Single(one);
        Assert.Equal(0, one[0].StartOffset);
        Assert.Equal(new FileInfo(_path).Length, one[0].EndOffset);

        File.WriteAllText(_path, "", Encoding.Latin1);
        Assert.Empty(LogSessions.Scan(_path));
    }
}
