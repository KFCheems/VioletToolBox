using System;
using System.Collections.ObjectModel;
using VioletToolBox.Core;

namespace VioletToolBox.Linux.ViewModels;

/// <summary>关于页：版本、许可、环境检查明细。</summary>
public sealed class AboutPageViewModel : ViewModelBase
{
    public AboutPageViewModel()
    {
        Tools = new ObservableCollection<ToolStatus>(
            ToolLocator.Describe().Select(t => new ToolStatus(t.Name, t.Path is null ? "未找到" : "就绪", t.Path)));
    }

    public ObservableCollection<ToolStatus> Tools { get; }

    public string AppVersion => "紫罗兰工具箱 · Linux 移植版 1.1（Avalonia 12 / .NET 10）";
    public string Upstream => "基于 Smart-Paocai/VioletToolBox v3.7（GPL-3.0）";
    public string PortNote => "EDL（9008）由 SharpEDL 纯托管实现；线刷/修补/模块等引擎复用上游纯 C# 代码；" +
                              "外部工具在 Linux 下自动解析（tools/ 目录或系统 PATH）。";
    public string License => "本分支遵循上游 GPL-3.0 许可证开源。刷机有风险，操作前请自行备份。";
}
