using System.Text;
namespace Loupedeck.MotorControlsPlugin;

internal static class SessionLogCursorTests
{
    private sealed class CountedStream : MemoryStream
    {
        internal long BytesRead;
        public override int Read(byte[] buffer, int offset, int count) { var n = base.Read(buffer, offset, count); BytesRead += n; return n; }
        public override int ReadByte() { var n = base.ReadByte(); if (n >= 0) BytesRead++; return n; }
        internal void Append(string text) { Position = Length; Write(Encoding.UTF8.GetBytes(text)); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run()
    {
        const string header = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"сесія\",\"source\":\"cli\"}}\n";
        const string start = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n";
        const string done = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}";
        using var stream = new CountedStream();
        stream.Append(header + new string('x', SessionLogCursor.MaxReadBytes * 8) + "\n" + start);
        var cursor = new SessionLogCursor(); cursor.Read(stream);
        Check(cursor.Id == "сесія" && cursor.Root && cursor.State == "work", "Bounded tail lost metadata or latest activity");
        Check(stream.BytesRead <= SessionLogCursor.MaxReadBytes + SessionLogCursor.HeaderBytes + 1, "Large journal was replayed");
        var boundary = cursor.Position;
        stream.Append(done); cursor.Read(stream);
        Check(cursor.Position == boundary && cursor.State == "work", "Incomplete JSON line was consumed");
        stream.Append("\n"); cursor.Read(stream);
        Check(cursor.Position == stream.Length && cursor.State == "idle", "Completed partial line was lost");
        stream.Append("{\"type\":\"response_item\",\"payload\":{\"type\":\"task_started\"}}\n"); cursor.Read(stream);
        Check(cursor.State == "idle", "Message content was mistaken for a lifecycle event");
        stream.Append(start); cursor.Read(stream);
        stream.Append(new string('x', SessionLogCursor.MaxReadBytes * 2) + "\n"); cursor.Read(stream);
        Check(cursor.State == "unknown", "Activity was fabricated after skipping an unread gap");
        stream.SetLength(0); stream.Append(header + done + "\n"); cursor.Read(stream);
        Check(cursor.State == "idle" && cursor.Id == "сесія", "Truncated journal retained old activity");
        using var child = new CountedStream();
        child.Append("{\"type\":\"session_meta\",\"payload\":{\"id\":\"child\",\"source\":{\"subagent\":{}}}}\n" + start);
        var nested = new SessionLogCursor(); nested.Read(child);
        Check(!nested.Root && nested.Id == "child", "Subagent became the owner's root session");
        Console.WriteLine("GPT journals: bounded startup and gap reads; UTF-8 metadata; partial lines; genuine lifecycle events; truncation; subagent isolation passed.");
    }
}
