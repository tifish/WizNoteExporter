using System;
using System.IO;
using System.Text;

namespace WizNoteExporter;

static class Log
{
    private static StreamWriter? _writer;
    private static readonly object _lock = new();
    public static string? LogFilePath { get; private set; }

    public static void Open(string outputDirectory)
    {
        Close();

        Directory.CreateDirectory(outputDirectory);
        LogFilePath = Path.Combine(
            outputDirectory,
            $"WizNoteExporter_{DateTime.Now:yyyyMMdd_HHmmss}.log"
        );

        _writer = new StreamWriter(LogFilePath, append: false, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public static void Close()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static void Info(string message) => Write(message, isError: false);

    public static void Error(string message) => Write(message, isError: true);

    private static void Write(string message, bool isError)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {(isError ? "ERROR " : "")}{message}";
        lock (_lock)
        {
            if (isError)
                Console.Error.WriteLine(line);
            else
                Console.WriteLine(line);

            _writer?.WriteLine(line);
        }
    }
}
