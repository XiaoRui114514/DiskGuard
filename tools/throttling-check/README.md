# 限速回归检查

```sh
dotnet run --project tools/throttling-check/ThrottlingCheck.csproj -c Release
dotnet run --project tools/engine-check/EngineCheck.csproj -c Release
```

`throttling-check` 检查实际程序集的 P/Invoke 返回类型，并将原始限速代码链接到可控的
Win32 替身，覆盖作业 IO 上限的成功、直接返回的错误码、进程加入失败和解除限速。
`SetIoRateControlInformationJobObject` 返回 `DWORD`：`0` 表示成功，不能按 `BOOL` 判断，
错误时应使用返回码而非 `GetLastError`。

`engine-check` 调用真实引擎状态机，检查一秒配置下连续两次安全采样解除硬限速，
以及 ETW 连续重建失败后的降级和重试计数重置。

两组检查可在 Windows 或 Linux .NET 8 SDK 上运行，不创建系统 ETW 会话，不改变真实
进程的优先级或限速。Windows 驱动/权限兼容性及实际磁盘限速效果仍需在 Windows 上验证。
