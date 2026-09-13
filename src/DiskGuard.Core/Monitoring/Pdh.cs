using System.Runtime.InteropServices;
using System.Text;
using DiskGuard.Core.Interop;

namespace DiskGuard.Core.Monitoring;

/// <summary>PDH 查询封装，支持通配符实例（返回实例数组）。</summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly List<PdhCounter> _counters = new();
    private int _collectCount;

    public PdhQuery()
    {
        uint status = NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != 0)
            throw new InvalidOperationException($"PdhOpenQuery 失败: 0x{status:X8}");
    }

    public int CollectCount => _collectCount;

    public PdhCounter AddCounter(string englishPath)
    {
        var counter = PdhCounter.Add(_query, englishPath);
        _counters.Add(counter);
        return counter;
    }

    public void Collect()
    {
        uint status = NativeMethods.PdhCollectQueryData(_query);
        if (status != 0)
            throw new InvalidOperationException($"PdhCollectQueryData 失败: 0x{status:X8}");
        _collectCount++;
    }

    public void Dispose()
    {
        foreach (var counter in _counters) counter.Dispose();
        _counters.Clear();
        if (_query != IntPtr.Zero)
        {
            NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}

internal sealed class PdhCounter : IDisposable
{
    private IntPtr _counter;

    public string Path { get; }

    public int LastRawCount { get; private set; }
    public int LastValidCount { get; private set; }
    public uint LastStatus { get; private set; }

    private PdhCounter(IntPtr counter, string path)
    {
        _counter = counter;
        Path = path;
    }

    public static PdhCounter Add(IntPtr query, string englishPath)
    {
        uint status = NativeMethods.PdhAddCounterW(query, englishPath, IntPtr.Zero, out var handle);
        if (status != 0)
        {
            string localized = PdhPathLocalizer.ToLocalizedPath(englishPath);
            if (!string.Equals(localized, englishPath, StringComparison.Ordinal))
                status = NativeMethods.PdhAddCounterW(query, localized, IntPtr.Zero, out handle);

            if (status != 0)
                throw new InvalidOperationException($"PdhAddCounter 失败 [{englishPath}]: 0x{status:X8}");
        }

        return new PdhCounter(handle, englishPath);
    }

    /// <summary>读取通配符实例数组（无效样本自动跳过）。</summary>
    public List<(string Instance, double Value)> ReadArray()
    {
        var result = new List<(string, double)>(64);
        LastRawCount = 0;
        LastValidCount = 0;
        LastStatus = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint size = 0;
            uint count = 0;
            NativeMethods.PdhGetFormattedCounterArrayW(_counter, NativeMethods.PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
            if (size == 0 || count == 0) return result;

            // 预留富余空间，避免“两次调用之间进程数变化”导致的缓冲区不足
            uint capacity = size + 512;
            IntPtr ptr = Marshal.AllocHGlobal((int)capacity);
            try
            {
                uint status = NativeMethods.PdhGetFormattedCounterArrayW(_counter, NativeMethods.PDH_FMT_DOUBLE, ref capacity, out count, ptr);
                LastStatus = status;
                if (status == NativeMethods.PDH_MORE_DATA || status == NativeMethods.PDH_INSUFFICIENT_BUFFER)
                    continue;
                if (status != 0) return result;

                LastRawCount = (int)count;
                int itemSize = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM>(ptr + (i * itemSize));
                    uint cstatus = item.FmtValue.CStatus;
                    if (cstatus != NativeMethods.PDH_CSTATUS_VALID_DATA && cstatus != NativeMethods.PDH_CSTATUS_NEW_DATA)
                        continue;

                    string name = Marshal.PtrToStringUni(item.SzName) ?? string.Empty;
                    result.Add((name, item.FmtValue.DoubleValue));
                    LastValidCount++;
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        return result;
    }

    public void Dispose()
    {
        _counter = IntPtr.Zero;
    }
}

/// <summary>英文计数器路径 -&gt; 本地化（中文系统）路径映射，供 PdhAddCounter 回退使用。</summary>
internal static class PdhPathLocalizer
{
    private static readonly object Gate = new();
    private static Dictionary<string, uint>? _nameToIndex;
    private static bool _loaded;

    public static string ToLocalizedPath(string englishPath)
    {
        // 路径形如 \Object(instance)\Counter 或 \Object\Counter
        var parts = englishPath.Split('\\');
        if (parts.Length < 3) return englishPath;

        string objectPart = parts[1];
        string counterPart = parts[^1];

        int paren = objectPart.IndexOf('(');
        string objectName = paren >= 0 ? objectPart[..paren] : objectPart;
        string instanceSuffix = paren >= 0 ? objectPart[paren..] : string.Empty;

        string localizedObject = Localize(objectName);
        string localizedCounter = Localize(counterPart);
        return $"\\{localizedObject}{instanceSuffix}\\{localizedCounter}";
    }

    private static string Localize(string englishName)
    {
        try
        {
            var map = LoadMap();
            if (!map.TryGetValue(englishName, out uint index)) return englishName;

            var sb = new StringBuilder(256);
            uint size = (uint)sb.Capacity;
            if (NativeMethods.PdhLookupPerfNameByIndexW(null, index, sb, ref size) != 0) return englishName;
            return sb.ToString();
        }
        catch
        {
            return englishName;
        }
    }

    private static Dictionary<string, uint> LoadMap()
    {
        lock (Gate)
        {
            if (_loaded && _nameToIndex != null) return _nameToIndex;
            _loaded = true;
            var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

            using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\009");
            if (baseKey?.GetValue("Counter") is string[] entries)
            {
                for (int i = 0; i + 1 < entries.Length; i += 2)
                {
                    if (uint.TryParse(entries[i], out uint idx) && !string.IsNullOrWhiteSpace(entries[i + 1]))
                        map[entries[i + 1]] = idx;
                }
            }

            _nameToIndex = map;
            return map;
        }
    }
}
