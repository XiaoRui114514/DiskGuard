using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskGuard.App.Localization;
using DiskGuard.Core.Localization;
using DiskGuard.Core.Logging;

namespace DiskGuard.App;

public partial class DeveloperLogWindow : Window
{
    private readonly DeveloperRecorder _recorder;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<DeveloperLogEntry> _rows = new();
    private long _selectedSequence = -1;
    private bool _closed;
    private bool _refreshing;

    public DeveloperLogWindow(DeveloperRecorder recorder)
    {
        InitializeComponent();
        _recorder = recorder;
        RecordGrid.ItemsSource = _rows;
        UiLanguage.Apply(this, Loc.Current);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshRecords();
        Closed += (_, _) => { _closed = true; _timer.Stop(); };
        RefreshRecords();
        _timer.Start();
    }

    private void RefreshRecords()
    {
        var selected = RecordGrid.SelectedItem as DeveloperLogEntry;
        var records = _recorder.GetRecent(); // 每次按当前时间裁剪，选中的过期记录也必须消失。
        StatusText.Text = !_recorder.Enabled ? Loc.T(LK.DeveloperRecordingOff)
            : records.Count == 0 ? Loc.T(LK.DeveloperEmpty)
            : Loc.F(LK.DeveloperRangeFormat, records.Count, records[^1].TimeText, records[0].TimeText);
        if (_recorder.LastError is { } error)
            StatusText.Text += "\n" + Loc.F(LK.DeveloperWriteFailedFormat, error);

        _refreshing = true;
        var validSequences = records.Select(r => r.Sequence).ToHashSet();
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (!validSequences.Contains(_rows[i].Sequence)) _rows.RemoveAt(i);
        long newest = _rows.Count == 0 ? -1 : _rows[0].Sequence;
        foreach (var entry in records.TakeWhile(r => r.Sequence > newest).Reverse()) _rows.Insert(0, entry);
        RecordGrid.SelectedItem = selected == null ? null : _rows.FirstOrDefault(r => r.Sequence == selected.Sequence);
        _refreshing = false;
        if (RecordGrid.SelectedItem == null)
        {
            _selectedSequence = -1;
            DetailText.Clear();
        }
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        if (RecordGrid.SelectedItem is not DeveloperLogEntry entry)
        {
            _selectedSequence = -1;
            DetailText.Clear();
            return;
        }
        _selectedSequence = entry.Sequence;
        string json = await Task.Run(() => entry.ToJson(indented: true));
        if (!_closed && _selectedSequence == entry.Sequence) DetailText.Text = json;
    }
}
