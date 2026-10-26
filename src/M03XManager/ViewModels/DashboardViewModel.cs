using System.Collections.ObjectModel;
using System.Windows.Input;
using M03XManager.Controls;
using M03XManager.Core;
using M03XManager.Infrastructure;

namespace M03XManager.ViewModels;

public enum TileKind { Overview, Stats, SpeedChart, Status, Signal, DeviceInfo, SignalChart, Network }

/// <summary>概览页的一个磁贴：位置/尺寸/外观属于布局，数据来自共享的 DashboardViewModel。</summary>
public sealed class TileViewModel : ObservableObject
{
    public TileViewModel(DashboardViewModel owner, TileState state)
    {
        Owner = owner;
        Id = state.Id;
        ColumnSpan = state.ColumnSpan;
        RowSpan = state.RowSpan;
        Title = TileLayout.Find(state.Id)?.Title ?? state.Id;
        Kind = state.Id switch
        {
            "traffic-overview" => TileKind.Overview,
            "traffic-trend" => TileKind.Stats,
            "speed-chart" => TileKind.SpeedChart,
            "status-summary" => TileKind.Status,
            "signal" => TileKind.Signal,
            "device-info" => TileKind.DeviceInfo,
            "signal-trend" => TileKind.SignalChart,
            _ => TileKind.Network,
        };
    }

    public DashboardViewModel Owner { get; }
    public string Id { get; }
    public string Title { get; }
    public TileKind Kind { get; }

    public int ColumnSpan { get; private set; }
    public int RowSpan { get; private set; }

    private bool _showTitle = true;
    public bool ShowTitle { get => _showTitle; set => Set(ref _showTitle, value); }

    private bool _isDragging;
    public bool IsDragging { get => _isDragging; set => Set(ref _isDragging, value); }

    public TileDefinition? Definition => TileLayout.Find(Id);

    public void ApplySize(int columnSpan, int rowSpan)
    {
        var d = Definition;
        ColumnSpan = d is null ? columnSpan : Math.Clamp(columnSpan, d.MinColumnSpan, d.MaxColumnSpan);
        RowSpan = d is null ? rowSpan : Math.Clamp(rowSpan, d.MinRowSpan, d.MaxRowSpan);
        OnPropertyChanged(nameof(ColumnSpan));
        OnPropertyChanged(nameof(RowSpan));
    }
}

public sealed record TrafficDetailRow(string Name, string Sub, string Rx, string Tx, string Total);

/// <summary>
/// 概览页：磁贴布局 + 总体/当日流量卡片 + 三种统计视图（当日 / 近七天 / 本月）。
/// 所有数字都标注来源：设备累计值、设备口径周期值、或本机采样值。
/// </summary>
public sealed class DashboardViewModel : ModuleViewModel
{
    public DashboardViewModel(MainViewModel host) : base(host, "概览", "设备实时状态与流量统计")
    {
        SelectPeriodCommand = new RelayCommand(p =>
        {
            if (p is string s) SelectedPeriod = s;
        });
        ResetLayoutCommand = new RelayCommand(ResetLayout);
        ToggleEditModeCommand = new RelayCommand(() => IsEditing = !IsEditing);
        FinishEditingCommand = new RelayCommand(() => EndEditing());
        ToggleTileTitleCommand = new RelayCommand(p =>
        {
            if (p is TileViewModel t)
            {
                t.ShowTitle = !t.ShowTitle;
                PersistTiles();
                ShowTileNotice(t.ShowTitle ? $"已显示「{t.Title}」的标题。" : $"已隐藏「{t.Title}」的标题。");
            }
        });
        GrowTileCommand = new RelayCommand(p => Resize(p, 1));
        ShrinkTileCommand = new RelayCommand(p => Resize(p, -1));

        ReloadTiles();
        ApplyDefaultPeriod(Host.Settings.DefaultStatsPeriod);
    }

    // ---------------- 磁贴 ----------------

    public ObservableCollection<TileViewModel> Tiles { get; } = new();

    /// <summary>供磁贴面板等控件访问应用级设置（动画开关等）。</summary>
    public MainViewModel App => Host;

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set { if (Set(ref _isEditing, value)) OnPropertyChanged(nameof(IsNotEditing)); }
    }

    public bool IsNotEditing => !_isEditing;

    private string _saveStatus = "";
    /// <summary>与通知系统完全无关的直接反馈：用户点「完成」后一定能看到结果。</summary>
    public string SaveStatusText { get => _saveStatus; private set => Set(ref _saveStatus, value); }

    public ICommand SelectPeriodCommand { get; }
    public ICommand ResetLayoutCommand { get; }
    public ICommand ToggleEditModeCommand { get; }
    public ICommand FinishEditingCommand { get; }
    public ICommand ToggleTileTitleCommand { get; }
    public ICommand GrowTileCommand { get; }
    public ICommand ShrinkTileCommand { get; }

    public string LayoutHint =>
        "拖动磁贴可调整顺序；编辑模式下可调整尺寸与标题显示。布局、磁贴外观与数据设置分别保存在配置文件中，互不覆盖。";

    public void ReloadTiles()
    {
        Host.Settings.Tiles = TileLayout.Merge(Host.Settings.Tiles);
        Tiles.Clear();
        foreach (var s in Host.Settings.Tiles.OrderBy(t => t.Order))
        {
            var tile = new TileViewModel(this, s) { ShowTitle = s.ShowTitle };
            Tiles.Add(tile);
        }
    }

    private void ResetLayout()
    {
        Host.Settings.Tiles = TileLayout.CreateDefault();
        Host.PersistSettings();
        ReloadTiles();
        ShowTileNotice("已恢复默认磁贴布局。");
    }

    /// <summary>磁贴相关提示统一走"折叠型"通知：5 秒后收起为紧凑状态而不是直接消失。</summary>
    private void ShowTileNotice(string message) => Host.Notices.RaiseTileEdit("概览磁贴", message);

    private void Resize(object? parameter, int delta)
    {
        if (parameter is not TileViewModel t) return;
        var d = t.Definition;
        if (d is null) return;
        // 尺寸按「列跨度 + 行跨度」成对放大或缩小，避免出现极端扁平的磁贴
        var col = t.ColumnSpan + delta;
        var row = t.RowSpan + (delta > 0 && t.ColumnSpan >= d.MaxColumnSpan ? 1 : 0);

        // 已到边界时必须给出明确反馈，不能让用户点了没反应
        if (delta > 0 && (col > d.MaxColumnSpan || row > d.MaxRowSpan))
        {
            ShowTileNotice($"「{t.Title}」已经是可用范围的最大尺寸（{d.MaxColumnSpan} 列 × {d.MaxRowSpan} 行）。");
            return;
        }
        if (delta < 0 && t.ColumnSpan <= d.MinColumnSpan && t.RowSpan <= d.MinRowSpan)
        {
            ShowTileNotice($"「{t.Title}」已经是最小尺寸（{d.MinColumnSpan} 列 × {d.MinRowSpan} 行）。");
            return;
        }

        t.ApplySize(col, row);
        PersistTiles();
        ShowTileNotice($"「{t.Title}」尺寸已调整为 {t.ColumnSpan} 列 × {t.RowSpan} 行。");
    }

    /// <summary>供拖拽排序调用。</summary>
    public void MoveTile(int from, int to)
    {
        if (from < 0 || from >= Tiles.Count || to < 0 || to >= Tiles.Count || from == to) return;
        Tiles.Move(from, to);
        PersistTiles();
    }

    /// <summary>把当前磁贴布局写入配置。返回是否写入成功；任何内部异常都不会向外抛。</summary>
    public bool PersistTiles()
    {
        var list = new List<TileState>();
        for (var i = 0; i < Tiles.Count; i++)
            list.Add(new TileState
            {
                Id = Tiles[i].Id,
                Order = i,
                ColumnSpan = Tiles[i].ColumnSpan,
                RowSpan = Tiles[i].RowSpan,
                ShowTitle = Tiles[i].ShowTitle,
                Visible = true,
            });
        Host.Settings.Tiles = list;

        try
        {
            return Host.PersistSettings();
        }
        catch (Exception ex)
        {
            Host.Settings.ReportSaveError(ex);
            Host.ReportFailure("保存磁贴布局", ex);
            return false;
        }
    }

    /// <summary>
    /// 结束编辑：无论当前是长按进入还是按钮进入，都要落盘并以最终布局为准。
    /// 这里不再依赖 TileGrid 的内部状态，避免"面板状态与视图模型不同步时收尾逻辑被跳过"。
    /// </summary>
    public void EndEditing()
    {
        Host.Trace("EndEditing 开始");
        // 1) 先纯落盘：不碰界面、不碰通知，绝不抛异常
        var ok = PersistTiles();
        Host.Trace($"PersistTiles 返回 {ok}（{Host.Settings.LastSaveError ?? "无错误"}）");

        // 2) 退出编辑态
        try { IsEditing = false; }
        catch (Exception ex) { Host.ReportFailure("退出编辑状态", ex); }

        // 3) 直接反馈，与通知系统无关
        SaveStatusText = ok
            ? $"布局已保存 {DateTime.Now:HH:mm:ss}"
            : $"保存失败：{Host.Settings.LastSaveError}";
        Host.Trace($"SaveStatusText = {SaveStatusText}");

        // 4) 通知放最后，异常一律吞掉——通知绝不能影响保存结果与界面状态
        try { if (ok) ShowTileNotice("布局已保存。"); }
        catch (Exception ex) { Host.ReportFailure("弹出保存通知", ex); }
    }

    public void ApplyDefaultPeriod(string period)
    {
        SelectedPeriod = period switch { "week" => "week", "month" => "month", _ => "day" };
    }

    // ---------------- 统计视图 ----------------

    private string _selectedPeriod = "day";
    public string SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            if (!Set(ref _selectedPeriod, value)) return;
            OnPropertyChanged(nameof(IsDayPeriod));
            OnPropertyChanged(nameof(IsWeekPeriod));
            OnPropertyChanged(nameof(IsMonthPeriod));
            RefreshTrafficViews();
        }
    }

    public bool IsDayPeriod { get => _selectedPeriod == "day"; set { if (value) SelectedPeriod = "day"; } }
    public bool IsWeekPeriod { get => _selectedPeriod == "week"; set { if (value) SelectedPeriod = "week"; } }
    public bool IsMonthPeriod { get => _selectedPeriod == "month"; set { if (value) SelectedPeriod = "month"; } }

    public ObservableCollection<BarItem> Bars { get; } = new();
    public ObservableCollection<TrafficDetailRow> DetailRows { get; } = new();

    private string _detailTitle = "点击图表中的时间点查看该时段各终端消耗";
    public string DetailTitle { get => _detailTitle; private set => Set(ref _detailTitle, value); }

    private string _periodSummary = "—";
    public string PeriodSummary { get => _periodSummary; private set => Set(ref _periodSummary, value); }

    private string _periodScope = "—";
    public string PeriodScope { get => _periodScope; private set => Set(ref _periodScope, value); }

    // ---------------- 总体流量 ----------------

    public string CumulativeRxText { get; private set; } = "—";
    public string CumulativeTxText { get; private set; } = "—";
    public string CumulativeTotalText { get; private set; } = "—";
    public string TodayRxText { get; private set; } = "—";
    public string TodayTxText { get; private set; } = "—";
    public string TodayTotalText { get; private set; } = "—";
    public string TodayDeviceLevelText { get; private set; } = "—";
    public string MonthDeviceLevelText { get; private set; } = "—";
    public string Month3DayDeviceText { get; private set; } = "—";
    public string DataUpdatedText { get; private set; } = "—";
    public string CoverageText { get; private set; } = "—";
    public string UncapturedText { get; private set; } = "—";
    public string DataSourceNote =>
        "口径说明：累计值来自设备自身的计数器（包含软件未运行期间的流量）；" +
        "当日与近七天为本机采样（由设备累计计数差分得到，只覆盖软件运行时段）；" +
        "本月的「设备口径」直接读取设备的本月计数器，「本地口径」为本地按日累加。";

    // ---------------- 设备与网络信息 ----------------

    private StatusInfo? _status;
    public StatusInfo? Status { get => _status; private set => Set(ref _status, value); }

    public TrafficSampler Sampler { get; } = new();
    public ObservableCollection<SamplePoint> Samples { get; } = new();

    public string DeviceName { get; private set; } = "—";
    public string HardwareVersion { get; private set; } = "—";
    public string FirmwareVersion { get; private set; } = "—";
    public string FirmwareDate { get; private set; } = "—";
    public string SerialNumber { get; private set; } = "—";
    public string Operator { get; private set; } = "—";
    public string Imei { get; private set; } = "—";
    public string Imsi { get; private set; } = "—";
    public string Iccid { get; private set; } = "—";
    public string WanIp { get; private set; } = "—";
    public string LanIp { get; private set; } = "—";
    public string Ssid { get; private set; } = "—";
    public string MacAddress { get; private set; } = "—";
    public string NetworkMode { get; private set; } = "—";
    public string ConnectionState { get; private set; } = "—";
    public string SignalLevelText { get; private set; } = "—";
    public string SignalDetail { get; private set; } = "—";
    public string RunTimeText { get; private set; } = "—";
    public string BatteryText { get; private set; } = "—";
    public string TrafficText { get; private set; } = "—";
    public string SpeedText { get; private set; } = "—";
    public string ClientsText { get; private set; } = "—";
    public string WifiStateText { get; private set; } = "—";
    public string PdpSummary { get; private set; } = "—";
    public string LteCellSummary { get; private set; } = "—";
    public int? SignalBars { get; private set; }
    public string SignalBarsText => SignalBars is { } bars ? bars.ToString() : "—";
    public string SampleDownText { get; private set; } = "—";
    public string SampleUpText { get; private set; } = "—";
    public string DeviceSpeedText { get; private set; } = "—";

    public ObservableCollection<string> Alerts { get; } = new();

    // ---------------- 数据加载 ----------------

    protected override async Task LoadAsync()
    {
        var api = Host.Api!;
        Status = await api.GetStatusAsync(CancellationToken.None);
        ApplyStatus(Status);
        await ApplyDeviceInfoAsync(api);

        var eng = await api.GetEngineeringAsync(CancellationToken.None);
        ApplyEngineering(eng);

        var pdp = await api.GetPdpContextAsync(CancellationToken.None);
        var ctx = pdp.PdpContextList?.FirstOrDefault();
        PdpSummary = ctx is null
            ? "无已激活的 PDP 上下文"
            : $"{pdp.NetworkName} · IPv4 {ctx.Ipv4} · 已连接 {J.Duration(J.Long(ctx.CurConnTime))}";
        OnPropertyChanged(nameof(PdpSummary));
        RefreshTrafficViews();
    }

    /// <summary>由全局轮询驱动，不触发忙碌状态，避免界面闪烁。</summary>
    public void ApplyStatus(StatusInfo status)
    {
        Status = status;

        var point = Sampler.Add(status);
        Samples.Add(point);
        while (Samples.Count > 600) Samples.RemoveAt(0);

        SampleDownText = J.Rate((long)point.DownloadBytesPerSec);
        SampleUpText = J.Rate((long)point.UploadBytesPerSec);

        SignalBars = J.Int(status.SignalQuality);
        SignalLevelText = SignalBars switch
        {
            0 => "无信号", 1 => "很弱", 2 => "较弱", 3 => "一般", 4 => "良好", 5 => "优秀", null => "—", _ => "—",
        };
        SignalDetail = J.Int(status.Rssi) is { } rssi && rssi is >= -140 and <= -20
            ? $"RSSI {rssi} dBm（设备上报值）"
            : "—（设备未返回有效 RSSI）";
        RunTimeText = J.Duration(J.Long(status.RunSeconds));
        var batteryState = BatteryState.Of(J.Int(status.BatteryCharge), J.Bool(status.BatteryCharging));
        BatteryText = J.Int(status.BatteryPercent) is { } bp
            ? $"{bp}%" + (batteryState == BatteryChargeState.Unknown ? "" : $"（{BatteryState.Text(batteryState)}）")
            : "无电池信息";
        TrafficText = $"累计接收 {J.Bytes(J.Long(status.RxByteAll))} · 累计发送 {J.Bytes(J.Long(status.TxByteAll))}";
        SpeedText = $"↓ {J.Rate(J.Long(status.RxSpeed))} · ↑ {J.Rate(J.Long(status.TxSpeed))}";
        DeviceSpeedText = "设备上报：" + SpeedText;
        ClientsText = J.Int(status.WifiClientsNum) is { } n ? $"{n} 台" : "—";
        NetworkMode = SysModeText(status.SysMode);

        RefreshAlerts(status);
        RaiseStatusFields();
    }

    public async Task ApplyDeviceInfoAsync(RouterApi api)
    {
        var home = await api.GetHomepageAsync(CancellationToken.None);
        DeviceName = string.IsNullOrWhiteSpace(home.DeviceName) ? "—" : home.DeviceName;
        HardwareVersion = home.HwVersion ?? "—";
        FirmwareVersion = home.SwVersion ?? "—";
        SerialNumber = home.SerialNumber ?? "—";
        Operator = string.IsNullOrWhiteSpace(home.NetworkName) ? "未注册" : home.NetworkName;
        Imei = home.Imei ?? "—";
        Imsi = string.IsNullOrWhiteSpace(home.Imsi) ? "—" : home.Imsi;
        Iccid = string.IsNullOrWhiteSpace(home.Iccid) ? "—" : home.Iccid;
        WanIp = string.IsNullOrWhiteSpace(home.WanIp) ? "—" : home.WanIp;
        LanIp = home.LanIp ?? "—";
        Ssid = string.IsNullOrWhiteSpace(home.Ssid) ? "—" : home.Ssid;
        MacAddress = home.Mac ?? "—";
        WifiStateText = J.Bool(home.WlanEnable) switch { true => "已开启", false => "已关闭", null => "—" };
        ConnectionState = home.ConnectDisconnect == "cellular" ? "已拨号连接" :
                          home.ConnectDisconnect == "disabled" ? "已断开" : home.ConnectDisconnect ?? "—";

        var fw = await api.GetFirmwareAsync(CancellationToken.None);
        FirmwareVersion = fw.VersionNum ?? FirmwareVersion;
        FirmwareDate = fw.VersionDate ?? "—";
        HardwareVersion = fw.HardwareVersion ?? HardwareVersion;

        // 本月 / 近 3 日的设备口径（各终端计数器求和）
        try
        {
            var devices = await api.GetDevicesAsync(CancellationToken.None);
            long mRx = 0, mTx = 0, d3Rx = 0, d3Tx = 0;
            var excluded = 0;
            foreach (var c in devices.ClientList ?? new List<ClientEntry>())
            {
                var counters = TrafficSanity.Inspect(c, Status);
                if (!counters.Reliable) { excluded++; continue; }
                mRx += counters.MonthRx ?? 0;
                mTx += counters.MonthTx ?? 0;
                d3Rx += counters.Day3Rx ?? 0;
                d3Tx += counters.Day3Tx ?? 0;
            }
            var excludedNote = excluded == 0 ? "" : $"（已排除 {excluded} 台计数器异常的终端）";
            MonthDeviceLevelText =
                $"设备口径本月（各终端合计）：↓ {J.Bytes(mRx)} · ↑ {J.Bytes(mTx)} · 合计 {J.Bytes(mRx + mTx)}{excludedNote}";
            Month3DayDeviceText =
                $"设备口径近 3 日（各终端合计）：↓ {J.Bytes(d3Rx)} · ↑ {J.Bytes(d3Tx)} · 合计 {J.Bytes(d3Rx + d3Tx)}{excludedNote}";
        }
        catch (RouterException)
        {
            MonthDeviceLevelText = "设备口径本月：读取失败";
            Month3DayDeviceText = "设备口径近 3 日：读取失败";
        }

        RaiseStatusFields();
        RefreshTrafficViews();
    }

    public void ApplyEngineering(EngineeringInfo eng)
    {
        var lte = eng.Lte;
        LteCellSummary = lte is null || string.IsNullOrEmpty(lte.Band)
            ? "设备未上报 LTE 小区信息"
            : $"Band {lte.Band} · PCI {lte.PhyCellId} · EARFCN {lte.DlEuArfcn} · 原始 RSRP {lte.Rsrp} / RSRQ {lte.Rsrq} / SINR {lte.Sinr} / CQI {lte.Cqi}";
        OnPropertyChanged(nameof(LteCellSummary));
    }

    // ---------------- 流量视图 ----------------

    /// <summary>重新计算总体流量卡片与当前统计视图（采样后调用）。</summary>
    public void RefreshTrafficViews()
    {
        var h = Host.History;

        var rx = J.Long(Status?.RxByteAll);
        var tx = J.Long(Status?.TxByteAll);
        CumulativeRxText = J.Bytes(rx);
        CumulativeTxText = J.Bytes(tx);
        CumulativeTotalText = rx is null || tx is null ? "—" : J.Bytes(rx + tx);

        var today = h.TodayDeviceLevel();
        TodayRxText = J.Bytes(today.Rx);
        TodayTxText = J.Bytes(today.Tx);
        TodayTotalText = J.Bytes(today.Total);
        TodayDeviceLevelText = h.HasLocalHistory
            ? "由本机采样得到（软件运行期间）"
            : "尚无本地采样数据";

        DataUpdatedText = h.LastSampleUtc is null
            ? $"最后更新：—（本轮界面刷新 {DateTime.Now:HH:mm:ss}）"
            : $"最后采样：{h.LastSampleUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        CoverageText = h.CoverageText;
        UncapturedText = h.UncapturedBytes > 0
            ? $"软件未运行期间未采集到：{J.Bytes(h.UncapturedBytes)}（不摊派到任何一天）"
            : "未采集量：无（采样连续）";

        Bars.Clear();
        DetailRows.Clear();
        DetailTitle = "点击图表中的时间点查看该时段各终端消耗";

        switch (SelectedPeriod)
        {
            case "week":
                foreach (var p in h.LastDays(7, true))
                    Bars.Add(new BarItem(p.Key, p.Label, p.Rx, p.Tx));
                PeriodSummary = SumText("近七天（本地采样）", Bars);
                PeriodScope = $"统计范围：{DateTime.Today.AddDays(-6):yyyy-MM-dd} ~ {DateTime.Today:yyyy-MM-dd}（本地采样，仅覆盖软件运行时段）";
                break;

            case "month":
                foreach (var p in h.MonthDaily(true))
                    Bars.Add(new BarItem(p.Key, p.Label, p.Rx, p.Tx));
                PeriodSummary = SumText($"本月 {DateTime.Today:yyyy 年 M 月}（本地口径）", Bars);
                PeriodScope = $"统计月份：{DateTime.Today:yyyy 年 M 月}，截至 {DateTime.Now:yyyy-MM-dd HH:mm}。" + MonthDeviceLevelText;
                break;

            default:
                foreach (var p in h.TodayHourly(true))
                    Bars.Add(new BarItem(p.Key, p.Label, p.Rx, p.Tx));
                PeriodSummary = SumText("当日（本地采样）", Bars);
                PeriodScope = $"统计日期：{DateTime.Today:yyyy-MM-dd}（本地采样，仅覆盖软件运行时段）";
                break;
        }

        OnPropertyChanged(nameof(PeriodScope));
        RaiseTrafficFields();
    }

    private static string SumText(string title, IEnumerable<BarItem> bars)
    {
        var list = bars.ToList();
        var rx = list.Sum(b => b.Rx);
        var tx = list.Sum(b => b.Tx);
        return $"{title}：↓ {J.Bytes(rx)} · ↑ {J.Bytes(tx)} · 合计 {J.Bytes(rx + tx)}";
    }

    /// <summary>图表点击：展示该时间段的各终端明细。</summary>
    public void ShowDetail(BarItem item)
    {
        DetailRows.Clear();
        var isHour = SelectedPeriod == "day";
        var rows = isHour
            ? Host.History.HourByDevice(item.Key)
            : Host.History.DayByDevice(DateTime.Parse(item.Key));

        DetailTitle = $"{item.Label} 明细：共 {J.Bytes(item.Total)}（↓ {J.Bytes(item.Rx)} · ↑ {J.Bytes(item.Tx)}）";

        foreach (var r in rows)
        {
            var name = Host.DisplayName(r.Mac, r.Hostname);
            DetailRows.Add(new TrafficDetailRow(name, r.Mac,
                J.Bytes(r.Rx), J.Bytes(r.Tx), J.Bytes(r.Rx + r.Tx)));
        }

        if (DetailRows.Count == 0)
            DetailRows.Add(new TrafficDetailRow("该时段没有本地采样记录", "可能软件当时未运行，或设备未上报该终端", "—", "—", "—"));
    }

    // ---------------- 提示 ----------------

    private void RefreshAlerts(StatusInfo status)
    {
        Alerts.Clear();
        var quality = J.Int(status.SignalQuality);
        if (quality is { } q && q <= Host.Settings.WeakSignalThresholdLevel)
            Alerts.Add($"设备原始信号档位 {q}/5，低于或等于阈值 {Host.Settings.WeakSignalThresholdLevel}/5。" +
                       "（阈值仅使用 signal_quality，不换算基带原始编码）");
        if (J.Bool(status.LoginStatus) == false) Alerts.Add("设备登录状态异常。");
        if (J.Int(status.NewSmsNum) is > 0) Alerts.Add($"设备报告有 {status.NewSmsNum} 条未读短信（本固件未提供短信管理接口）。");
        var batteryState = BatteryState.Of(J.Int(status.BatteryCharge), J.Bool(status.BatteryCharging));
        if (J.Int(status.BatteryPercent) is { } bp && bp <= 20 &&
            batteryState is not (BatteryChargeState.Charging or BatteryChargeState.Full))
            Alerts.Add($"电量偏低（{bp}%），请及时充电。");
        if (J.Bool(status.Roaming) == true) Alerts.Add("当前处于漫游状态。");
        if (Alerts.Count == 0) Alerts.Add("未发现异常。");
    }

    public static string SysModeText(string? sysMode) => sysMode switch
    {
        "17" => "LTE (4G)",
        "5" or "15" => "UMTS (3G)",
        "3" => "GSM (2G)",
        null or "" => "—",
        _ => $"未知制式（原始值 {sysMode}）",
    };

    private void RaiseStatusFields()
    {
        foreach (var name in new[]
                 {
                     nameof(DeviceName), nameof(HardwareVersion), nameof(FirmwareVersion), nameof(FirmwareDate),
                     nameof(SerialNumber), nameof(Operator), nameof(Imei), nameof(Imsi), nameof(Iccid),
                     nameof(WanIp), nameof(LanIp), nameof(Ssid), nameof(MacAddress), nameof(NetworkMode),
                     nameof(ConnectionState), nameof(SignalLevelText), nameof(SignalDetail), nameof(RunTimeText),
                     nameof(BatteryText), nameof(TrafficText), nameof(SpeedText), nameof(ClientsText),
                     nameof(WifiStateText), nameof(SignalBars), nameof(SignalBarsText),
                     nameof(SampleDownText), nameof(SampleUpText),
                     nameof(DeviceSpeedText), nameof(MonthDeviceLevelText), nameof(Month3DayDeviceText),
                 })
            OnPropertyChanged(name);

        Host.Summary = Host.IsConnected
            ? string.Join(" · ", new[] { Operator, NetworkMode, $"信号 {SignalLevelText}", $"在线 {ClientsText}" }
                .Where(s => !string.IsNullOrEmpty(s) && !s.Contains('—')))
            : "尚未连接设备";
    }

    private void RaiseTrafficFields()
    {
        foreach (var name in new[]
                 {
                     nameof(CumulativeRxText), nameof(CumulativeTxText), nameof(CumulativeTotalText),
                     nameof(TodayRxText), nameof(TodayTxText), nameof(TodayTotalText), nameof(TodayDeviceLevelText),
                     nameof(DataUpdatedText), nameof(CoverageText), nameof(UncapturedText), nameof(PeriodSummary),
                     nameof(DetailTitle),
                 })
            OnPropertyChanged(name);
    }
}
