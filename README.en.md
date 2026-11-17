# DiskGuard

[简体中文](README.md) · English

A Windows disk activity monitor with process I/O priority and recovery controls. It samples the selected physical disk separately, identifies active process I/O, and can lower eligible process priorities to reduce contention. It cannot guarantee a disk will stay below a particular utilization level.

## Download and use

1. Open the [latest release](https://github.com/XiaoRui114514/DiskGuard/releases/latest) and download `DiskGuard.exe` from Assets.
2. Run the standalone executable. The release includes the .NET runtime. Administrator access enables ETW attribution; without it, attribution is approximate.
3. Select a physical disk, inspect the activity, and review the thresholds and protection list before leaving monitoring active.

The interface supports Simplified Chinese, Traditional Chinese, English, Japanese, Korean and Arabic, including right-to-left layout. Change it in Settings.

## Behavior and limits

- Ordinary throttling protects the foreground application. At 98% activity on the selected disk, protection may also throttle an active foreground workload; input and desktop critical processes remain protected.
- Priority reduction is the first stage; it is a scheduling hint, not a guaranteed throughput cap.
- The level-2 bandwidth/IOPS path uses the legacy [`SetIoRateControlInformationJobObject` API](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-setioratecontrolinformationjobobject). Microsoft marks this API unsupported on Windows 10 version 1607 and newer. Do not choose DiskGuard expecting guaranteed per-process MB/s or IOPS caps on current Windows 10/11. The source retains the legacy request path; failed requests are logged and do not establish an active cap. Sandbox/job restrictions can also reject assignment.
- [ProcessThrottler](src/DiskGuard.Core/Throttling/ProcessThrottler.cs) applies the native request; [GuardEngine](src/DiskGuard.Core/Engine/GuardEngine.cs) handles failure. Native test success for priority/restoration is not proof of rate enforcement or an actual disk-performance benchmark.
- Strong mode is off by default and intermittently suspends a target process; it can make that application temporarily unresponsive.
- Kernel I/O, protected processes, driver activity and drive-internal maintenance cannot all be controlled by a user-space tool.
- System optimization controls are separate, explicit actions. Stopping search/indexing or update services affects those features; disabling Defender real-time protection reduces security. These actions are not required to monitor a disk.

Settings, logs and recovery state are stored in `%LOCALAPPDATA%\DiskGuard\`. Restore throttles and any service/Defender changes you made before removing the app. Turn off automatic startup in Settings, then exit and remove the executable. Keep the configuration directory if you need its diagnostic or recovery information.

## Build and verify

Use Windows and the .NET 8 SDK:

```powershell
dotnet build DiskGuard.sln -c Release
dotnet publish src/DiskGuard.App/DiskGuard.App.csproj -c Release -o dist
```

The [Windows workflow](.github/workflows/build.yml) also checks translations, rolling diagnostics, per-disk policies, API failures, recovery and native sampling. Reproduce the relevant checks when changing engine behavior. Real disk contention must be assessed on Windows; compilation alone does not establish responsiveness or safe recovery.

The [Chinese guide](README.md) contains the full threshold and recovery rules. Report problems through [Issues](https://github.com/XiaoRui114514/DiskGuard/issues) with Windows version, selected disk, mode and reproduction steps. Redact process paths and other personal details from diagnostics.

MIT licensed; see [LICENSE](LICENSE). Author: Hison.
