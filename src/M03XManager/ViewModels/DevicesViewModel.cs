using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using M03XManager.Controls;
using M03XManager.Core;
using M03XManager.Infrastructure;

namespace M03XManager.ViewModels;

public sealed record ClientRow(
    string Index, string Name, string Hostname, string Mac, string Ip, string ConnType, string Status,
    string TotalRx, string TotalTx, string MonthRx, string MonthTx, string Day3Rx, string Day3Tx,
    string AccessTime, bool IsBlocked, bool CustomNamed, bool Online, bool CounterSuspect, string CounterWarning);

/// <summary>
/// 设备与流量：在线终端清单、自定义名称、单设备流量详情（当日 / 近七天 / 本月）、拉黑与解除。
/// 统计口径与概览页完全一致（都取自同一份本地采样历史）。
/// </summary>
public sealed class DevicesViewModel : ModuleViewModel
{
    public DevicesViewModel(MainViewModel host) : base(host, "设备与流量", "在线终端、自定义名称与单设备流量")
    {
        ResetStatsCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            var r = MessageBox.Show(
                "将重置设备侧的设备列表与流量统计（action=4）。此操作不会断开当前上网，但设备上已累计的统计会清零。是否继续？",
                "重置设备统计", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            await Host.Api!.DeviceActionAsync("4", null, CancellationToken.None);
        }, "已下发重置统计命令"), () => Host.IsConnected);

        BlockCommand = new AsyncRelayCommand(p => ConfirmAndRunAsync(p, block: true), p => Host.IsConnected);
        UnblockCommand = new AsyncRelayCommand(p => ConfirmAndRunAsync(p, block: false), p => Host.IsConnected);
        SelectDeviceCommand = new RelayCommand(p => { if (p is ClientRow row) OpenDetail(row); });
        CloseDetailCommand = new RelayCommand(() => { Selected = null; DetailVisible = false; });

        SaveAliasCommand = new RelayCommand(_ =>
        {
            if (Selected is null) return;
            Host.Aliases.Set(Selected.Mac, AliasInput);
            var mac = Selected.Mac;
            var idx = Clients.ToList().FindIndex(c => c.Mac == mac);
            ApplyAliasToRows();
            FinishAliasChange(string.IsNullOrWhiteSpace(AliasInput)
                ? $"已清除 {mac} 的自定义名称，恢复显示设备原始主机名。"
                : $"已把 {mac} 命名为「{AliasInput}」，并已同步到列表与统计明细。");
            _ = idx;
        });

        ClearAliasCommand = new RelayCommand(_ =>
        {
            if (Selected is null) return;
            AliasInput = "";
            Host.Aliases.Set(Selected.Mac, null);
            ApplyAliasToRows();
            FinishAliasChange("已恢复为设备原始主机名。");
        });
    }

    private void FinishAliasChange(string successMessage)
    {
        var error = Host.Aliases.LastSaveError;
        if (string.IsNullOrWhiteSpace(error))
        {
            IsError = false;
            Message = successMessage;
            return;
        }

        IsError = true;
        var message = "设备名称已在当前会话生效，但保存到本地失败：" + error;
        Host.Log.Error(LogCategory.Persistence, "设备名称", message);
        Host.Notices.Raise(NoticeLevel.Error, Title, message,
            logCategory: LogCategory.Persistence, logSource: "设备名称", logEvent: false);
    }

    private async Task ConfirmAndRunAsync(object? parameter, bool block)
    {
        if (parameter is not ClientRow row) return;
        var text = block
            ? $"禁止「{row.Name}」（{row.Mac}）上网？\n设备侧标记为黑名单（action=2），该终端会立即失去网络访问。"
            : $"解除对「{row.Name}」（{row.Mac}）的限制？\n设备侧移除黑名单（action=3）。";
        if (MessageBox.Show(text, block ? "禁止上网" : "解除限制", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunAsync(async () =>
        {
            await Host.Api!.DeviceActionAsync(block ? "2" : "3", row.Mac, CancellationToken.None);
        }, block ? $"已禁止 {row.Name} 上网" : $"已解除 {row.Name} 的限制");
    }

    // ---------------- 列表 ----------------

    public ObservableCollection<ClientRow> Clients { get; } = new();

    public string KnownDeviceCount { get; private set; } = "—";
    public string OnlineCount { get; private set; } = "—";
    public string TotalBanner { get; private set; } = "—";
    public string CounterWarning { get; private set; } = "";
    public bool HasCounterWarning => CounterWarning.Length > 0;

    public string DataSourceNote =>
        "流量数据来自设备接口（client_list）：累计 / 本月 / 近 3 日都是设备侧统计口径。" +
        "固件对部分离线/历史终端会返回失真的计数器（例如单机几十 TB，远超整机累计），" +
        "这类值已标为「计数器异常」并隐藏，请以本地采样为准。" +
        "详情页中的「当日」「近七天」为本地采样（由累计计数差分得到，只覆盖软件运行时段），两者口径不同，界面已分别标注。";

    public string RoutingNote =>
        "流量去向（域名 / 服务类别）无法实现：已核查设备提供的全部 30 个接口，没有任何 DNS 查询记录、连接日志或流量分类接口，" +
        "因此不存在可真实获取的数据源。" +
        "本软件不会为此抓包或注入代理——那需要常驻后台进程、会记录你的全部上网行为，且与「不默认记录用户上网活动」的要求冲突。" +
        "在设备固件不提供日志的前提下，任何「域名排行」都只能是编造的数据。";

    // ---------------- 详情 ----------------

    public ObservableCollection<BarItem> DeviceBars { get; } = new();

    private ClientRow? _selected;
    public ClientRow? Selected
    {
        get => _selected;
        private set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(DetailTitle));
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selected is not null;

    private bool _detailVisible;
    public bool DetailVisible { get => _detailVisible; private set => Set(ref _detailVisible, value); }

    public string DetailTitle => _selected is null ? "未选择设备" : $"设备详情 · {_selected.Name}";
    public string DetailHostname { get; private set; } = "—";
    public string DetailMac { get; private set; } = "—";
    public string DetailIp { get; private set; } = "—";
    public string DetailConnType { get; private set; } = "—";
    public string DetailOnline { get; private set; } = "—";
    public string DetailToday { get; private set; } = "—";
    public string DetailWeek { get; private set; } = "—";
    public string DetailMonthLocal { get; private set; } = "—";
    public string DetailMonthDevice { get; private set; } = "—";
    public string DetailIdentityNote { get; private set; } = "";

    private string _aliasInput = "";
    public string AliasInput { get => _aliasInput; set => Set(ref _aliasInput, value); }

    public ICommand ResetStatsCommand { get; }
    public ICommand BlockCommand { get; }
    public ICommand UnblockCommand { get; }
    public ICommand SelectDeviceCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand SaveAliasCommand { get; }
    public ICommand ClearAliasCommand { get; }

    protected override async Task LoadAsync()
    {
        var api = Host.Api!;
        var info = await api.GetDevicesAsync(CancellationToken.None);
        var status = await api.GetStatusAsync(CancellationToken.None);

        Clients.Clear();
        var suspect = 0;
        foreach (var c in info.ClientList ?? new List<ClientEntry>())
        {
            var blocked = c.Status == "2";
            var hasIp = !string.IsNullOrWhiteSpace(c.Ip) &&
                        !string.Equals(c.Ip, "NA", StringComparison.OrdinalIgnoreCase);
            var online = c.Status != "0" && hasIp;
            var statusText = blocked ? "已禁止上网" : online ? "正常" : "离线（设备历史记录）";
            var hostname = string.IsNullOrWhiteSpace(c.Name) ? "未知设备" : c.Name;
            var counters = TrafficSanity.Inspect(c, status);
            if (!counters.Reliable) suspect++;
            Clients.Add(new ClientRow(
                c.Index ?? "", Host.DisplayName(c.Mac, hostname), hostname, c.Mac ?? "", c.Ip ?? "",
                c.ConnType == "WIFI" ? "无线" : c.ConnType ?? "—",
                statusText,
                counters.Reliable ? J.Bytes(counters.Rx) : "—",
                counters.Reliable ? J.Bytes(counters.Tx) : "—",
                counters.Reliable ? J.Bytes(counters.MonthRx) : "—",
                counters.Reliable ? J.Bytes(counters.MonthTx) : "—",
                counters.Reliable ? J.Bytes(counters.Day3Rx) : "—",
                counters.Reliable ? J.Bytes(counters.Day3Tx) : "—",
                c.AccessTime ?? "—", blocked, Host.Aliases.Has(c.Mac),
                online, !counters.Reliable, counters.Reason ?? ""));
        }

        foreach (var k in info.KnownDevices ?? new List<KnownDevice>())
        {
            if (Clients.Any(c => string.Equals(c.Mac, k.Mac, StringComparison.OrdinalIgnoreCase))) continue;
            var hostname = string.IsNullOrWhiteSpace(k.Name) ? "未知设备" : k.Name;
            Clients.Add(new ClientRow(k.Index ?? "", Host.DisplayName(k.Mac, hostname), hostname, k.Mac ?? "",
                k.IpAddress ?? "", k.ConnType == "WLAN" ? "无线" : k.ConnType ?? "—",
                "离线（历史设备）", "—", "—", "—", "—", "—", "—", k.ConnTime ?? "—", false,
                Host.Aliases.Has(k.Mac), false, false, ""));
        }

        KnownDeviceCount = (info.KnownDevices?.Count ?? 0).ToString();
        OnlineCount = Clients.Count(c => c.Online).ToString();
        CounterWarning = suspect == 0
            ? ""
            : $"已隐藏 {suspect} 台终端的设备侧计数器：固件上报值与整机累计明显矛盾，不能当作真实流量。";

        TotalBanner = $"设备累计：接收 {J.Bytes(J.Long(status.RxByteAll))} · 发送 {J.Bytes(J.Long(status.TxByteAll))}";

        foreach (var n in new[]
                 {
                     nameof(KnownDeviceCount), nameof(OnlineCount), nameof(TotalBanner),
                     nameof(CounterWarning), nameof(HasCounterWarning),
                 })
            OnPropertyChanged(n);

        // 详情面板选中项在新数据里可能已消失（设备断开），此时关闭详情避免展示过期数据
        if (Selected is not null)
        {
            var again = Clients.FirstOrDefault(c => c.Mac == Selected.Mac);
            if (again is null) { Selected = null; DetailVisible = false; }
            else OpenDetail(again, silent: true);
        }
    }

    private void ApplyAliasToRows()
    {
        var list = Clients.ToList();
        Clients.Clear();
        foreach (var c in list)
            Clients.Add(c with { Name = Host.DisplayName(c.Mac, c.Hostname), CustomNamed = Host.Aliases.Has(c.Mac) });
        if (Selected is not null)
            Selected = Clients.FirstOrDefault(c => c.Mac == Selected.Mac);
    }

    /// <summary>打开设备流量详情：三个统计周期 + 每日趋势，全部来自与概览页相同的本地历史。</summary>
    private void OpenDetail(ClientRow row, bool silent = false)
    {
        Selected = row;
        DetailVisible = true;
        AliasInput = Host.Aliases.Get(row.Mac) ?? "";

        DetailHostname = row.Hostname;
        DetailMac = row.Mac;
        DetailIp = row.Ip;
        DetailConnType = row.ConnType;
        DetailOnline = row.Status;

        var h = Host.History;
        var today = h.DeviceTotal(row.Mac, DateTime.Today);
        DetailToday = $"↓ {J.Bytes(today.Rx)} · ↑ {J.Bytes(today.Tx)} · 合计 {J.Bytes(today.Rx + today.Tx)}";

        var week = h.DeviceDaily(row.Mac, 7);
        var wRx = week.Sum(x => x.Rx);
        var wTx = week.Sum(x => x.Tx);
        DetailWeek = $"↓ {J.Bytes(wRx)} · ↑ {J.Bytes(wTx)} · 合计 {J.Bytes(wRx + wTx)}";

        var month = h.MonthDaily(true);
        var monthRows = h.DeviceDaily(row.Mac, DateTime.Today.Day);
        var mRx = monthRows.Sum(x => x.Rx);
        var mTx = monthRows.Sum(x => x.Tx);
        DetailMonthLocal = $"↓ {J.Bytes(mRx)} · ↑ {J.Bytes(mTx)} · 合计 {J.Bytes(mRx + mTx)}";

        DetailMonthDevice = row.CounterSuspect
            ? $"设备计数器异常，已隐藏（{row.CounterWarning}）"
            : row.MonthRx == "—"
                ? "设备未上报该终端的本月计数器"
                : $"↓ {row.MonthRx} · ↑ {row.MonthTx}（含软件未运行期间的流量）";

        DetailIdentityNote = Host.Aliases.Has(row.Mac)
            ? "已按 MAC 地址关联自定义名称与历史流量，IP 变化不影响识别。"
            : "未设置自定义名称，显示设备上报的主机名（若主机名变化，历史仍按 MAC 归属）。";
        _ = month;

        DeviceBars.Clear();
        foreach (var p in week) DeviceBars.Add(new BarItem(p.Key, p.Label, p.Rx, p.Tx));

        foreach (var n in new[]
                 {
                     nameof(DetailHostname), nameof(DetailMac), nameof(DetailIp), nameof(DetailConnType),
                     nameof(DetailOnline), nameof(DetailToday), nameof(DetailWeek), nameof(DetailMonthLocal),
                     nameof(DetailMonthDevice), nameof(DetailIdentityNote), nameof(DetailTitle),
                 })
            OnPropertyChanged(n);

        if (!silent)
        {
            Message = null;
            IsError = false;
        }
    }
}
