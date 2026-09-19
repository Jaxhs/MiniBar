using System.IO;
using System.IO.Pipes;
using System.Threading;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Services;

/// <summary>
/// 单实例协调器。
/// 第二个实例（例如用户把文件拖到 exe / 快捷方式上）会把路径通过命名管道转发给第一个实例，
/// 然后自己退出 —— 于是“拖到程序上”的行为在两种入口下完全一致。
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\MiniBar.SingleInstance.v1";
    private const string PipeName = "MiniBar.Instance.Pipe.v1";

    private readonly Mutex _mutex;
    private readonly Action<string[]> _onPathsReceived;
    private bool _disposed;

    public SingleInstanceCoordinator(Action<string[]> onPathsReceived)
    {
        _onPathsReceived = onPathsReceived;

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;

        if (IsFirstInstance)
        {
            StartServer();
        }
    }

    public bool IsFirstInstance { get; }

    private void StartServer()
    {
        _ = Task.Run(async () =>
        {
            while (!_disposed)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync().ConfigureAwait(false);

                    using var reader = new StreamReader(server);
                    var payload = await reader.ReadToEndAsync().ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        var paths = payload
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToArray();

                        if (paths.Length > 0)
                        {
                            Application.Current?.Dispatcher.BeginInvoke(new Action(() => _onPathsReceived(paths)));
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"单实例管道异常（忽略）：{ex.Message}");
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>把命令行参数（文件/文件夹路径）转发给已在运行的实例。</summary>
    public void SendPaths(IEnumerable<string> paths)
    {
        var payload = string.Join('\n', paths);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.Write(payload);
            writer.Flush();
        }
        catch (Exception ex)
        {
            AppLog.Warn("转发命令行参数失败", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // 未持有则忽略
        }

        _mutex.Dispose();
    }
}
