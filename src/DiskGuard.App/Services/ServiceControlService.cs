using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskGuard.App.Services;

/// <summary>一个 Windows 服务的只读快照。</summary>
public readonly record struct ServiceProbe(
    string Name,
    bool Exists,
    bool Running,
    int StateCode,
    string Detail)
{
    public string StateText => StateCode switch
    {
        1 => "Stopped",
        2 => "StartPending",
        3 => "StopPending",
        4 => "Running",
        5 => "ContinuePending",
        6 => "PausePending",
        7 => "Paused",
        _ => Exists ? $"State {StateCode}" : "Unavailable"
    };
}

public readonly record struct ServiceControlResult(bool Ok, string Message, ServiceProbe State);

/// <summary>
/// 只暂停用户明确选择的后台服务，可立即启动恢复；不修改服务启动类型。
/// 操作后会轮询真实服务状态，避免把 sc.exe 返回 0 当成“已经停下”。
/// </summary>
public static class ServiceControlService
{
    private static readonly Regex StatePattern = new(@"STATE\s*:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static readonly IReadOnlyList<string> PausableServices = new[]
    {
        "SysMain",
        "WSearch",
        "DoSvc",
        "wuauserv",
        "BITS"
    };

    public static bool IsPausable(string serviceName) =>
        PausableServices.Contains(serviceName, StringComparer.OrdinalIgnoreCase);

    public static ServiceProbe Query(string serviceName)
    {
        if (!IsPausable(serviceName))
            return new ServiceProbe(serviceName, false, false, -1, "Unsupported service name");

        var (exitCode, output) = RunSc("query", serviceName);
        if (exitCode != 0)
            return new ServiceProbe(serviceName, false, false, -1, output.Trim());

        var match = StatePattern.Match(output);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stateCode))
            return new ServiceProbe(serviceName, true, false, -1, output.Trim());

        return new ServiceProbe(serviceName, true, stateCode == 4, stateCode, output.Trim());
    }

    public static IReadOnlyDictionary<string, ServiceProbe> QueryAll()
    {
        var result = new Dictionary<string, ServiceProbe>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in PausableServices)
            result[name] = Query(name);
        return result;
    }

    public static ServiceControlResult SetRunning(string serviceName, bool running)
    {
        if (!IsPausable(serviceName))
            return new ServiceControlResult(false, "Unsupported service name", Query(serviceName));

        var before = Query(serviceName);
        if (!before.Exists)
            return new ServiceControlResult(false, before.Detail, before);
        if (before.Running == running)
            return new ServiceControlResult(true, running ? "Already running" : "Already stopped", before);

        string action = running ? "start" : "stop";
        var (exitCode, output) = RunSc(action, serviceName);
        if (exitCode != 0)
        {
            var failed = Query(serviceName);
            return new ServiceControlResult(false, output.Trim(), failed);
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(12);
        ServiceProbe state = Query(serviceName);
        while (DateTime.UtcNow < deadline)
        {
            state = Query(serviceName);
            if (state.Exists && state.Running == running && state.StateCode is 4 or 1)
                return new ServiceControlResult(true, state.StateText, state);
            Thread.Sleep(250);
        }

        return new ServiceControlResult(false, $"Final state: {state.StateText}", state);
    }

    private static (int ExitCode, string Output) RunSc(params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("sc.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process == null) return (-1, "Unable to start sc.exe");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, "Service control timed out");
            }

            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
