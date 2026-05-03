namespace RedCompute.Core.Logging;

public class LogEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Tag { get; set; } = "";
    public string TagCategory { get; set; } = "debug";
    public string Message { get; set; } = "";
    public string FullMessage { get; set; } = "";
    public string TagColor { get; set; } = "#72767D";
    public bool IsMultiline { get; set; }
    public bool IsError { get; set; }
    public Guid? JobId { get; set; }

    public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");
    public string PreviewMessage => Message.Length > 300 ? Message[..300] + "..." : Message;
}
