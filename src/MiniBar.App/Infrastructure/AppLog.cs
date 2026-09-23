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
/// <para>
/// <b>为什么要"后台批量落盘"？</b> 日志若每次都直接写文件，界面线程会被磁盘 I/O 卡住（尤其高频 Debug 日志）。
/// 这里用 <see cref="System.Timers.Timer"/>（<c>FlushTimer</c>，每 2 秒 <c>Elapsed</c> 一次）把所有待写行攒成一批，
/// 统一 <c>AppendAllText</c> 一次写完。<b>注意用的是 System.Timers.Timer 而非 DispatcherTimer</b>：
/// 落盘是纯后台 I/O、完全不碰 WPF 的 UI 对象，不需要回到 UI 线程，用线程池触发的 System.Timers.Timer 反而更省。
/// </para>
/// <para>
/// 两个队列：<c>Recent</c> 存最近 300 条给界面看；<c>Pending</c> 存"还没刷到盘"的行，刷盘后清空。
/// 两者都用 <c>lock (Gate)</c> 保护，因为写日志的线程和 2 秒刷盘的线程可能同时访问。
/// 关闭 VerboseLogging 时 Debug 级别直接丢弃，代价接近于零。
/// </para>
/// </summary>
public static class AppLog
{
    private const int MaxMemoryEntries = 300;

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static readonly Queue<string> Pending = new();
    // AutoReset = true：每 2 秒自动再来一次，持续刷盘；首次启动即 Start()。
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
