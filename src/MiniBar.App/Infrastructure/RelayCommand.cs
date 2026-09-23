using System.Windows.Input;

namespace MiniBar.App.Infrastructure;

/// <summary>
/// 极简 <see cref="ICommand"/> 实现（同样不依赖任何第三方库，手写最省）。
/// <para>
/// <b>ICommand 是什么？</b> WPF 的按钮/菜单等控件绑定 <c>Command</c> 后，点击会自动调用
/// <see cref="Execute"/>；<see cref="CanExecute"/> 返回 false 时按钮自动变灰禁用。这就把"界面交互"
/// 和"业务逻辑"解耦——XAML 只管绑定，逻辑全在委托里。
/// </para>
/// <para>
/// <b>EventHandler 是什么？</b> .NET 里"发生了一件事"的标准通知方式：<c>event EventHandler 某事件</c>，
/// 别人用 <c>+=</c> 订阅，发事件时用 <c>某事件?.Invoke(发送者, 参数)</c>。这里 <see cref="CanExecuteChanged"/>
/// 通知 WPF"能不能执行的状态变了，请重新问一下 CanExecute"。
/// </para>
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>无参版本的便捷构造：把 <c>Action</c> 包成带 <c>object?</c> 参数的执行体。</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    /// <summary>完整构造：传入"点击时做什么"和可选的"当前能否点击"。</summary>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>订阅方（WPF 绑定）靠它知道"按钮要不要变灰"。</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>当前是否允许执行；没提供判定函数时默认永远允许。</summary>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <summary>真正执行命令逻辑（点击时由 WPF 自动调用）。</summary>
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>外界状态变化导致"能否执行"可能变了时，手动触发 <see cref="CanExecuteChanged"/> 让 WPF 重新评估。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
