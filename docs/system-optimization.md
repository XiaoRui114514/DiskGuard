# 系统优化项目与数据口径

1.10.0 的服务检查覆盖 24 项，其中 17 项提供单独的暂停/恢复开关。全部开关位于状态表上方；除用户已有选择外，没有默认批量关闭操作。

新增可选项为 DiagTrack、PcaSvc、MapsBroker、TrkWks、Spooler、Fax、WMPNetworkSvc、CDPSvc、PhoneSvc、XblAuthManager、XblGameSave 和 XboxNetApiSvc。打印、共享、兼容性和游戏服务只适合在不用对应功能时暂停。每个开关旁均写明功能影响，Xbox 存档项提示先同步存档。不存在的服务显示未安装且不提供操作。

Defender、安全中心、错误报告、通知、流量统计和字体缓存只展示通用服务状态。Defender 仍通过原有偏好设置控制扫描负载。关闭选择沿用既有保存方式，管理员启动时重新应用；不改变服务的启动类型，不停止依赖项，不删除文件。

## 资源与性能

资源数据仅在首次打开优化页、手动刷新或操作服务后读取。相同 PID 只采样一次；内存为宿主进程工作集，I/O 为短窗口的进程读写速率，包含网络和设备 I/O。共享宿主的数值不能逐行相加，也不能视为关闭一个服务即可释放的内存。无访问权限或进程退出时显示未知。

最小化和托盘隐藏时保留监控、限速、恢复与诊断记录，停止界面绘制；日志保留在原有有界记录中，打开日志页时补齐。普通界面读数每两秒绘制，限速、暂停、磁盘及数据源变化立即刷新，监控与保护仍每秒采样。没有新增持续的服务扫描任务。

## 研究依据

- [微软服务配置说明](https://learn.microsoft.com/en-us/windows/iot/iot-enterprise/optimize/services)：用于核对服务职责和依赖。该文面向 IoT，未将其整套停用建议直接用于普通桌面。
- [Windows Search 性能说明](https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/windows-search-performance-issues)：索引与搜索的取舍。
- [进程工作集](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.workingset64)：工作集包含共享和私有页。
- [GetProcessIoCounters](https://learn.microsoft.com/en-us/windows/desktop/api/winbase/nf-winbase-getprocessiocounters)：进程级 I/O 的统计范围。
- [WinUtil 源项目](https://github.com/ChrisTitusTech/winutil)：用于比较常见优化项目；未运行或引入该项目的脚本。
- [YouTube 公开视频说明](https://www.youtube.com/watch?v=AD3RRUVLXpo)：仅作为服务候选线索；未将视频中的整套注册表操作加入软件。X 的搜索结果有限，TikTok 正文受到访问限制。
