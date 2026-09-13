using System.ComponentModel;
using DiskGuard.Core.Util;

namespace DiskGuard.App.ViewModels;

public sealed class ProcessRowVm : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private double _read;
    private double _write;
    private string _tag = string.Empty;
    private double _occupancy;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Pid { get; init; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnChanged(nameof(Name));
        }
    }

    public double ReadBytesPerSec
    {
        get => _read;
        set
        {
            if (_read.Equals(value)) return;
            _read = value;
            OnChanged(nameof(ReadBytesPerSec));
            OnChanged(nameof(ReadText));
            OnChanged(nameof(RateText));
        }
    }

    public double WriteBytesPerSec
    {
        get => _write;
        set
        {
            if (_write.Equals(value)) return;
            _write = value;
            OnChanged(nameof(WriteBytesPerSec));
            OnChanged(nameof(WriteText));
            OnChanged(nameof(RateText));
        }
    }

    public string Tag
    {
        get => _tag;
        set
        {
            if (_tag == value) return;
            _tag = value;
            OnChanged(nameof(Tag));
        }
    }

    /// <summary>磁盘占用百分比（该进程让磁盘忙碌的时间占比）。</summary>
    public double OccupancyPercent
    {
        get => _occupancy;
        set
        {
            if (_occupancy.Equals(value)) return;
            _occupancy = value;
            OnChanged(nameof(OccupancyPercent));
            OnChanged(nameof(OccupancyText));
        }
    }

    public string OccupancyText => _occupancy <= 0 ? "-" : $"{_occupancy:0.#}%";

    public string RateText => ProcessUtil.FormatRate(_read + _write);
    public string ReadText => ProcessUtil.FormatRate(_read);
    public string WriteText => ProcessUtil.FormatRate(_write);

    private void OnChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class LogRowVm
{
    public string Time { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
