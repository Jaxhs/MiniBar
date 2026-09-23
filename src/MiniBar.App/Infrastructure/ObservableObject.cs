using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MiniBar.App.Infrastructure;

/// <summary>
/// 极简 MVVM 通知基类（项目刻意不引入 CommunityToolkit.Mvvm / DI 容器，自己手写最省的版本）。
/// <para>
/// <b>INotifyPropertyChanged 是什么？</b> 这是 WPF 数据绑定的"心跳"：界面绑定了某个属性后，
/// 只要属性值变化并触发 <see cref="PropertyChanged"/> 事件，WPF 就会自动刷新对应控件，无需手动改 UI。
/// 本类把"触发通知"封装成 <see cref="OnPropertyChanged"/> 和 <see cref="SetProperty{T}"/> 两个helper。
/// </para>
/// <para>
/// <b>CallerMemberName 干嘛用？</b> 它是 C# 的"调用方信息"特性：方法被某个属性 setter 调用时，
/// 编译器会自动把"调用它的属性名"填进来。于是 <c>set =&gt; SetProperty(ref _x, value)</c> 不用手写
/// <c>SetProperty(ref _x, value, nameof(X))</c>，既省事又不会改属性名后漏改字符串。
/// </para>
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>属性值变化事件。WPF 的绑定系统订阅它来知道"该刷新界面了"。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 手动触发某属性变化通知。<c>propertyName</c> 默认由 <c>[CallerMemberName]</c> 自动取（见类说明）。
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// 设值并在"值真的变化时"自动通知界面。返回 true 表示值变了（调用方可借此决定是否做额外动作）。
    /// 先比较再赋值，避免"设成一样的值却白触发一次刷新"。
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
