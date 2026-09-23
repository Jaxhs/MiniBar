using System.IO;
using System.IO.Pipes;
using System.Threading;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Services;

/// <summary>
/// 单实例协调器：保证“同一时刻只有一个 MiniBar 在跑”。
///
/// 两个核心原语（都是 Windows 上最朴素、零依赖的进程间通信/互斥手段）：
///   · <see cref="Mutex"/>（命名互斥体）：一个跨进程的“锁”。第一个实例用 initiallyOwned:true 抢到它，createdNew=true；
///     后续实例抢不到（createdNew=false）就知道“已经有大哥在跑了”，于是把自己手里的文件路径通过管道转交出去后退出。
///     为什么不用“检查进程名/窗口标题”来判重：那种方式在进程卡死或同名其它程序时会误判；Mutex 由内核保证唯一。
///   · 命名管道 NamedPipe（NamedPipeServerStream / NamedPipeClientStream）：Windows 上最常用的【本机进程间通信】方式。
///     一个进程建服务端并起一个名字（PipeName），另一个进程用同名客户端连上来，双方通过字节流读写。
///     为什么不用：① 文件——要轮询、有竞态、要清理临时文件；② 剪贴板——会覆盖用户正在复制的东西，且容量/类型受限；
///     ③ HTTP/端口——要挑端口、怕被占用、起监听开销大。管道是内核级别的、按名连接、用完即弃，最轻最干净。
///
/// 流程：首个实例 new 出来时 IsFirstInstance=true → StartServer 起循环等待连接；后续实例 IsFirstInstance=false，
/// 直接 SendPaths 把命令行收到的路径写进管道，然后整个进程退出。首个实例从管道读到路径，用 Dispatcher 转回 UI 线程处理
/// （例如把拖进来的 DLL 加载成插件）。
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\MiniBar.SingleInstance.v1";
    private const string PipeName = "MiniBar.Instance.Pipe.v1";

    private readonly Mutex _mutex;
    private readonly Action<string[]> _onPathsReceived;
    private bool _disposed;

    /// <summary>
    /// 构造即判定“我是不是第一个实例”。
    /// 用 new Mutex(initiallyOwned:true, 名字, out createdNew)：若本进程成功抢到并拥有这个命名互斥体，createdNew=true（我是老大），
    /// 于是启动管道服务端（StartServer）等待后来者；若抢不到（互斥体已被第一个实例持有），createdNew=false，本实例不做服务端，
    /// 稍后直接把参数通过管道转发给对方再退出。MutexName 带 Local\ 前缀，限制在本机会话内，避免与其它用户/服务撞名。
    /// </summary>
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
                    // 每次循环新建一个服务端管道（NamedPipeServerStream）。PipeDirection.In=只收不发；
                    // 第 3 个参数 1=只允许 1 个客户端连入（一次转发完即弃）；Byte 模式=按字节流读写；
                    // Asynchronous=配合 await 非阻塞。注意：这里【不设超时】——服务端要一直在线等下一个实例连上来。
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    // 阻塞等待某个客户端（第二个实例）连上来；连上后 server 这个流就成了双方的通信通道。
                    await server.WaitForConnectionAsync().ConfigureAwait(false);

                    // 用 StreamReader 包一层读文本，编码默认 UTF-8。注意一定要 ReadToEnd 之后才关闭——
                    // 客户端会 Write + Flush，这里读到流尾才算收完，否则可能只读一半。
                    using var reader = new StreamReader(server);
                    var payload = await reader.ReadToEndAsync().ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        // 约定用 '\n' 分隔多个路径，去掉首尾空白，解析成字符串数组。
                        var paths = payload
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToArray();

                        if (paths.Length > 0)
                        {
                            // 路径交给 UI 去处理（加载 DLL 等），必须在 UI 线程做 —— 用 Dispatcher.BeginInvoke 切回去。
                            Application.Current?.Dispatcher.BeginInvoke(new Action(() => _onPathsReceived(paths)));
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 单个连接出错（比如客户端中途崩溃）不应让整个服务停摆，记日志 + 短暂延后继续下一轮等待。
                    AppLog.Debug($"单实例管道异常（忽略）：{ex.Message}");
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>
    /// 把命令行参数（文件/文件夹路径）转发给已在运行的实例，然后本进程即可退出。
    ///
    /// 分步骤：
    ///   1. 用 '\n' 把多个路径拼成一段文本 payload（与服务端 Split 约定一致）；空则直接返回；
    ///   2. new NamedPipeClientStream(".", PipeName, Out)：第一个参数 "." 表示“本机”，PipeName 必须与服务端同名才能连上；
    ///   3. client.Connect(2000)：带着【2 秒超时】去连。超时很重要——若首个实例没起服务端（极少见），客户端不会无限卡死；
    ///   4. 用 StreamWriter 写文本，必须 Write 后 Flush()：StreamWriter 默认带缓冲，不 Flush 服务端可能收不到完整数据；
    ///   5. using 结束自动关流，服务端 ReadToEnd 拿到完整内容。
    /// 任何失败（连不上/超时）都只记警告，绝不影响“打开第二个程序”本该做的最小动作。
    /// </summary>
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
