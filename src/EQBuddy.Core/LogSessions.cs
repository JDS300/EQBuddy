using System.IO;
using System.Text;

namespace EQBuddy.Core;

/// <summary>One contiguous play session inside a log file, as a byte range
/// [StartOffset, EndOffset) plus its first and last timestamps.</summary>
public sealed record LogSessionInfo(long StartOffset, long EndOffset, DateTime Start, DateTime End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Splits a log file into sessions with the same &gt;= <see cref="SessionStats.SessionGap"/>
/// rule the live stats use — but by BYTE RANGE, so review mode (#74, Snagglefern round
/// two) can replay exactly one session out of a pre-splitter multi-session log instead
/// of always landing on the last one. Offsets are safe to hand to
/// <see cref="LogWatcher"/> because eqlog files are Latin1: one byte per char, always.
/// </summary>
public static class LogSessions
{
    public static List<LogSessionInfo> Scan(string path)
    {
        var sessions = new List<LogSessionInfo>();
        var bytes = File.ReadAllBytes(path);
        long lineStart = 0;
        long sessionStart = -1;
        DateTime firstTs = default, lastTs = default;

        for (long i = 0; i <= bytes.LongLength; i++)
        {
            if (i != bytes.LongLength && bytes[i] != (byte)'\n') continue;
            var len = (int)(i - lineStart);
            if (len > 0)
            {
                var line = Encoding.Latin1.GetString(bytes, (int)lineStart, len).TrimEnd('\r');
                if (LogParser.TrySplitLine(line, out var ts, out _))
                {
                    if (sessionStart < 0)
                    {
                        sessionStart = lineStart;
                        firstTs = ts;
                    }
                    else if (ts - lastTs >= SessionStats.SessionGap)
                    {
                        sessions.Add(new LogSessionInfo(sessionStart, lineStart, firstTs, lastTs));
                        sessionStart = lineStart;
                        firstTs = ts;
                    }
                    lastTs = ts;
                }
            }
            lineStart = i + 1;
        }
        if (sessionStart >= 0)
            sessions.Add(new LogSessionInfo(sessionStart, bytes.LongLength, firstTs, lastTs));
        return sessions;
    }
}
