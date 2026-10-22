using System.Text.Json;
namespace Loupedeck.MotorControlsPlugin;

// Bounded byte reads: a multi-gigabyte conversation must never be replayed
// merely to draw a status tile. Partial JSON lines remain for the next read.
internal sealed class SessionLogCursor
{
    internal const int MaxReadBytes = 256 * 1024, HeaderBytes = 16 * 1024;
    internal string Id { get; private set; }
    internal bool Root { get; private set; }
    internal string State { get; private set; } = "unknown";
    internal long Position { get; private set; }

    internal void Read(Stream stream)
    {
        var length = stream.Length;
        if (Position > length) { Position = 0; Id = null; Root = false; State = "unknown"; }
        if (Id == null)
        {
            stream.Position = 0;
            var header = new byte[(int)Math.Min(HeaderBytes, length)];
            var count = Fill(stream, header);
            var end = Array.IndexOf(header, (byte)'\n', 0, count);
            if (end >= 0) Parse(header.AsMemory(0, end), metadata: true);
        }
        var start = Math.Max(Position, length - MaxReadBytes);
        var skipPartial = start > Position;
        if (skipPartial)
        {
            State = "unknown"; // A skipped gap may contain a completion marker.
            stream.Position = start - 1;
            skipPartial = stream.ReadByte() != '\n';
        }
        stream.Position = start;
        var bytes = new byte[(int)Math.Min(MaxReadBytes, length - start)];
        var size = Fill(stream, bytes);
        var begin = 0;
        for (var i = 0; i < size; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            if (skipPartial) skipPartial = false;
            else Parse(bytes.AsMemory(begin, i - begin), metadata: false);
            begin = i + 1;
        }
        Position = start + begin;
    }

    private static int Fill(Stream stream, byte[] bytes)
    {
        var count = 0;
        while (count < bytes.Length) { var n = stream.Read(bytes, count, bytes.Length - count); if (n == 0) break; count += n; }
        return count;
    }

    private void Parse(ReadOnlyMemory<byte> line, bool metadata)
    {
        // Avoid decoding ordinary messages and tool output, including huge lines.
        if (!metadata && line.Span.IndexOf("\"task_started\""u8) < 0 && line.Span.IndexOf("\"turn_started\""u8) < 0
            && line.Span.IndexOf("\"task_complete\""u8) < 0 && line.Span.IndexOf("\"turn_complete\""u8) < 0
            && line.Span.IndexOf("\"turn_aborted\""u8) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var kind) || !root.TryGetProperty("payload", out var payload)) return;
            if (metadata && kind.GetString() == "session_meta")
            {
                Id = payload.GetProperty("id").GetString();
                Root = !payload.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("subagent", out _);
            }
            else if (!metadata && kind.GetString() == "event_msg" && payload.TryGetProperty("type", out var type))
            {
                switch (type.GetString())
                {
                    case "task_started": case "turn_started": State = "work"; break;
                    case "task_complete": case "turn_complete": case "turn_aborted": State = "idle"; break;
                }
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        catch (KeyNotFoundException) { }
    }
}
