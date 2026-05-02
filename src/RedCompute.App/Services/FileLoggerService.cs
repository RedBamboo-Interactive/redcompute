using System.IO;

namespace RedCompute.App.Services;

public class FileLoggerService : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly string _logPath;
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "Logs");

    public string LogFilePath => _logPath;

    public FileLoggerService()
    {
        Directory.CreateDirectory(LogDir);
        CleanupOldLogs(10);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _logPath = Path.Combine(LogDir, $"redcompute_{timestamp}.log");
        _writer = new StreamWriter(_logPath, append: false) { AutoFlush = true };
    }

    public void Write(string message)
    {
        try { _writer.WriteLine(message); }
        catch { }
    }

    private void CleanupOldLogs(int keepCount)
    {
        try
        {
            var files = Directory.GetFiles(LogDir, "redcompute_*.log")
                .OrderByDescending(f => f)
                .Skip(keepCount)
                .ToArray();
            foreach (var f in files)
                File.Delete(f);
        }
        catch { }
    }

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }
}
