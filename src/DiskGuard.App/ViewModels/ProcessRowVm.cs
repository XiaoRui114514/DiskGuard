using System.ComponentModel;
using DiskGuard.Core.Localization;
using DiskGuard.Core.Util;

namespace DiskGuard.App.ViewModels;

public sealed class ProcessRowVm : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private double _read;
    private double _write;
    private string _tag = string.Empty;
    private double _occupancy;
    private string _rateText = Loc.Value(ProcessUtil.FormatRate(0));
    private string _readText = Loc.Value(ProcessUtil.FormatRate(0));
    private string _writeText = Loc.Value(ProcessUtil.FormatRate(0));
    private string _occupancyText = "-";

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
            RefreshReadText();
            RefreshTotalRateText();
        }
    }

    public double WriteBytesPerSec
    {
        get => _write;
        set
        {
            if (_write.Equals(value)) return;
            _write = value;
            RefreshWriteText();
            RefreshTotalRateText();
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
            RefreshOccupancyText();
        }
    }

    public string OccupancyText => _occupancyText;

    public string RateText => _rateText;
    public string ReadText => _readText;
    public string WriteText => _writeText;

    /// <summary>语言切换后重建缓存的显示文本（阿拉伯语需添加从左到右标记）。</summary>
    public void RefreshDisplayTexts()
    {
        RefreshReadText();
        RefreshWriteText();
        RefreshTotalRateText();
        RefreshOccupancyText();
    }

    private void RefreshReadText()
    {
        string text = Loc.Value(ProcessUtil.FormatRate(_read));
        if (_readText != text)
        {
            _readText = text;
            OnChanged(nameof(ReadText));
        }
    }

    private void RefreshWriteText()
    {
        string text = Loc.Value(ProcessUtil.FormatRate(_write));
        if (_writeText != text)
        {
            _writeText = text;
            OnChanged(nameof(WriteText));
        }
    }

    private void RefreshTotalRateText()
    {
        string text = Loc.Value(ProcessUtil.FormatRate(_read + _write));
        if (_rateText != text)
        {
            _rateText = text;
            OnChanged(nameof(RateText));
        }
    }

    private void RefreshOccupancyText()
    {
        string text = _occupancy <= 0 ? "-" : Loc.Value($"{_occupancy:0.#}%");
        if (_occupancyText == text) return;
        _occupancyText = text;
        OnChanged(nameof(OccupancyText));
    }

    private void OnChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class LogRowVm
{
    public string Time { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
