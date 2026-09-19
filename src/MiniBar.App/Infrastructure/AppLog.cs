using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MiniBar.App.Infrastructure;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// 轻量日志：内存里只保留最近若干条（供界面展示），文件写入按 2 秒批量刷盘。
/// 关闭 VerboseLogging 时 Debug 级别直接丢弃，代价接近于零。
/// </summary>
public static class AppLog
{
    private const int MaxMemoryEntries = 300;

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static readonly Queue<string> Pending = new();
    private static readonly System.Timers.Timer FlushTimer = new(2000) { AutoReset = true };

    static AppLog()
    {
        FlushTimer.Elapsed += (_, _) => Flush();
        FlushTimer.Start();
    }

    public static bool Verbose { get; set; }

    public static event EventHandler<string>? EntryWritten;

    public static IReadOnlyList<string> GetRecent()
    {
        lock (Gate)
        {
            return Recent.ToArray();
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);

    public static void Info(string message) => Write(LogLevel.Info, message, null);

    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warn, message, ex);

    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    private static void Write(LogLevel level, string message, Exception? ex)
    {
        if (level == LogLevel.Debug && !Verbose)
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] {message}";
        if (ex is not null)
        {
            line += $" :: {ex.GetType().Name}: {ex.Message}";

            // 出错时把调用栈也带上，省得下次还要复现
            if (level >= LogLevel.Error && ex.StackTrace is { Length: > 0 } stack)
            {
                line += Environment.NewLine + "        " +
                        stack.Replace(Environment.NewLine, Environment.NewLine + "        ");
            }
        }

        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > MaxMemoryEntries)
            {
                Recent.Dequeue();
            }

            Pending.Enqueue(line);
            while (Pending.Count > 2000)
            {
                Pending.Dequeue();
            }
        }

        if (level >= LogLevel.Warn)
        {
            System.Diagnostics.Debug.WriteLine(line);
        }

        EntryWritten?.Invoke(null, line);
    }

    private static void Flush()
    {
        string[] batch;
        lock (Gate)
        {
            if (Pending.Count == 0)
            {
                return;
            }

            batch = Pending.ToArray();
            Pending.Clear();
        }

        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var sb = new StringBuilder();
            foreach (var line in batch)
            {
                sb.AppendLine(line);
            }

            File.AppendAllText(AppPaths.LogFile, sb.ToString());
        }
        catch
        {
            // 日志失败绝不能影响程序运行
        }
    }

    public static void Shutdown()
    {
        FlushTimer.Stop();
        Flush();
    }
}
