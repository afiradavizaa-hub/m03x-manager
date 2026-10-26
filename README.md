# M03X 路由器管理器

面向 **M03X LTE 无线路由器**（波导 / 督舟 / 广猫 / 麦力猫等贴牌，基带为 ASR1803 方案，Marvell MiFi 后台）
的第三方 Windows 桌面管理软件。软件**直接对接设备原厂接口**，不经过任何中间服务器，不使用任何模拟数据。

---

## 1. 快速开始

```
dist-v3.3\M03XManager.exe
```

1. 先让电脑连上路由器的 Wi-Fi 或网线（默认管理地址 `http://192.168.1.1`）。
2. 打开软件，在登录面板确认设备地址和账号，并输入你自己的管理密码，点「连接」。
3. 连接成功后左侧出现功能导航；顶部可调整刷新间隔（默认 5 秒，范围 2–60 秒）。

运行环境：Windows 10/11 x64 + **.NET 10 桌面运行时**（本机已安装 `Microsoft.WindowsDesktop.App 10.0.12`）。

## 2. 从源码构建

```powershell
# 需要 .NET SDK 10。若本机没有 SDK，可用便携版 SDK：
& "D:\Software\_tooling\dotnet-sdk\dotnet.exe" build src\M03XManager\M03XManager.csproj -c Release

# 生成可分发目录
& "D:\Software\_tooling\dotnet-sdk\dotnet.exe" publish src\M03XManager\M03XManager.csproj -c Release -r win-x64 --self-contained false -o dist
```

**零第三方依赖**：只使用 .NET 基础类库与 WPF 自带能力，图表、主题、MVVM 基础设施均为自研，
因此离线可编译、体积小、无需还原任何 NuGet 包。

## 3. 目录结构

```
m03x-manager/
├─ docs/                             设计文档与真机验证记录
│  ├─ 01-原厂接口逆向分析.md           协议、30 个接口、编码约定、功能清单
│  └─ 02-功能覆盖表.md                 每项功能的实现与验证状态、安全发现
├─ src/M03XManager/
│  ├─ Core/                          设备通信层（与界面完全解耦）
│  │  ├─ RouterClient.cs             Digest 认证、请求发送、超时/重试/会话恢复
│  │  ├─ DigestAuth.cs               MD5 Digest 摘要算法
│  │  ├─ RouterApi.cs                30 个接口的类型化封装（读写载荷）
│  │  ├─ RouterEndpoints.cs          接口名常量
│  │  ├─ Models.cs                   设备返回结构（严格对齐真机字段）
│  │  ├─ HexText.cs                  SSID / 终端名的十六进制编解码
│  │  ├─ TrafficSampler.cs           本机采样（速率/信号序列）
│  │  ├─ AlertRules.cs               弱信号状态机与流量阈值去重规则
│  │  ├─ NetworkQuality.cs           ICMP/TCP 网络质量探测与分类
│  │  ├─ CsvExport.cs                CSV 生成、脱敏与公式注入防护
│  │  ├─ AppSettings.cs              配置持久化（密码用 DPAPI 加密）
│  │  ├─ DpapiHelper.cs              Windows DPAPI（P/Invoke）
│  │  └─ ActivityLog.cs              统一运行日志（内存 + 持久化 + 轮转 + 脱敏）
│  ├─ Infrastructure/                MVVM 基础设施与转换器
│  ├─ ViewModels/                    各页面视图模型
│  ├─ Views/                         各页面 XAML
│  ├─ Controls/TrendChart.cs         自绘实时曲线控件
│  ├─ Themes/Theme.xaml              统一视觉规范（配色/圆角/控件样式）
│  └─ UiSmokeTest.cs                 界面自检（含联机模式）
└─ tests/M03XManager.SelfTest/       真机集成自检（复用同一份通信层）
```

## 4. 测试

### 4.1 真机集成自检（21 项）

```powershell
# 只读验证
SelfTest.exe

# 追加：写入链路验证（原值回写，不改变设备设置）+ 认证探测
SelfTest.exe http://192.168.1.1 admin "<your-password>" --write
```

覆盖内容：Digest 登录、18 个读接口的字段解析、`wan_set`/`wlan_set` 原值回写与回读一致性、
不存在的接口是否被正确识别、设备是否强制校验凭据。

### 4.2 界面自检

```powershell
M03XManager.exe --ui-smoke          # 离线：逐页实例化 + 双分辨率布局
M03XManager.exe --ui-smoke --live   # 联机：真实连接设备、拉取真实数据后渲染每个页面
```

结果写入 `ui-smoke-report.txt`，同时捕获 XAML 解析异常与 WPF 数据绑定错误。
联机模式还会校验关键字段是否真的进入了界面模型（设备名、WAN IP 等）。

## 5. 数据真实性与口径说明

- 所有设备数据均来自设备接口的实时响应；接口失败时界面显示错误提示，**不会用 0 或默认值冒充正常状态**。
- 速率曲线标注为「本机采样」：由设备累计字节数按时间差分得到，与设备上报的瞬时速率分开显示，避免口径混淆。
- 工程模式下的 RSRP/RSRQ/SINR/CQI 是基带原始编码值（非 dBm），原厂页面同样原样显示，本软件不擅自换算。
- 设备不支持的模块（短信/USSD/电话本）不会出现在界面上。
- 日志中的密码、摘要响应等敏感字段在写入前统一打码；密码仅在用户勾选"记住密码"时以 DPAPI 密文保存。

## 6. 已知限制

详见 [docs/02-功能覆盖表.md](docs/02-功能覆盖表.md) 第三、四节。摘要：

- MAC 黑白名单、WPS、自定义防火墙规则：只读（条目结构与取值语义未经真机确认）。
- 固件升级：未实现（无固件包可验证，失败不可回滚）。
- 短信 / USSD / 电话本：该固件不含这些模块（原厂页面 404）。
- 设备侧安全缺陷：实测该固件未校验登录凭据，软件会如实告警但无法修复。

---

## 7. 第二阶段（v2.0）新增

详细实现与验证记录见 [docs/03-第二阶段升级说明.md](docs/03-第二阶段升级说明.md)。

- **窗口白边修复**：白边来自 Windows 按系统主题绘制的非客户区（标题栏 + 窗口边框），
  改为用 DWM 把它们对齐到软件背景色，保持原生窗口行为（拖动/缩放/最小化/最大化/关闭不变）。
- **视觉重构**：深浅双主题 + 强调色 + 磁贴圆角/间距/内边距 + 界面缩放，全部通过 `DynamicResource` 实时生效。
- **流量统计**：概览新增总体流量卡片（累计与当日分区显示）与三个统计视图（当日 / 近七天 / 本月），
  图表支持悬停查看与点击下钻到各终端明细；本地历史按日按小时持久化，未采集时段如实单列。
- **设备自定义名称**：以 MAC 为主键，重启保留，不覆盖原始主机名；设备详情页给出四个周期的流量。
- **磁贴仪表盘**：8 个磁贴，可拖动排序、可调尺寸与标题显隐，布局独立持久化且升级时向后兼容。
- **设置中心**：外观、数据、连接、采样、通知、启动、备份、诊断八组设置，含设置导入导出与脱敏诊断日志。

新增源码目录：`Core/TrafficHistory.cs`、`Core/TileLayout.cs`、`Core/DeviceAliases.cs`、`Core/Notifications.cs`、
`Controls/BarChart.cs`、`Controls/TileGrid.cs`、`Infrastructure/ThemeManager.cs`、
`Infrastructure/WindowChromeHelper.cs`、`ViewModels/SettingsViewModel.cs`、`Views/SettingsView.xaml`。

本地数据文件（`%APPDATA%\M03XManager\`）：`settings.json`（含磁贴布局）、`traffic-history.json`、
`device-names.json`、`battery-history.json`、`activity.log` 及其轮转归档。

## 8. 第四阶段（v3.1）优化

详细实现、能力边界与验证记录见 [docs/05-第四阶段优化说明.md](docs/05-第四阶段优化说明.md)。

- **概览图表**：信号趋势与实时速率图支持同时间点悬停提示、十字线和自适应浮动卡片；
  速率图明确标注上传（青色 ↑）与下载（蓝色 ↓），图例、颜色和数值使用同一映射。
  无有效采样时显示占位提示，不补 0、不插值。
- **信号预警**：阈值使用设备原始 `signal_quality` 档位（0–5），连续 3 次确认后提醒，
  持续异常每 30 分钟最多一次，连续恢复后提示。RSSI 只展示设备上报值，不换算基带原始编码。
- **流量预警**：支持每日/每月阈值，同一周期/阈值配置跨重启只提醒一次，跨日/跨月自动重新判断。
- **网络质量检测**：设置中心可手动检测路由器/网关与外网连通性、ICMP 延迟和丢包；
  区分路由器不可达、外网连接失败与检测失败。自动检测默认关闭，开启后每 5 分钟最多一次。
- **数据导出**：当前流量视图与终端清单可导出为 UTF-8 BOM CSV；终端名称、主机名、MAC、IP 默认脱敏。
- **断连去抖**：单次轮询超时只记录，连续 2 次失败才判定断连，降低无线抖动误报。

本轮仍不实现终端新增/离线提醒：设备不提供连接事件时间线，直接对比轮询列表容易因 DHCP 续租、
Wi-Fi 休眠或接口短暂缺项产生大量误报。下一步需要本地事件表与多轮确认窗口。

## 9. 电池监测（v3.2）

详细可行性、字段语义与验证记录见 [docs/06-电池监测说明.md](docs/06-电池监测说明.md)。

- 新增独立「电池监测」页面：电量百分比、充电状态、原始字段、最后采样与本地历史覆盖。
- 设备实际只提供 `battery_percent`、`battery_charge`、`battery_charging` 和语义未确认的
  `battery_connect`；电压、电流、温度、剩余时间、Wh 均不提供，界面明确标注，不估算。
- 电量趋势图使用真实时间轴，支持 1 小时 / 6 小时 / 24 小时 / 7 天范围、悬停详情、
  拖动平移、滚轮缩放与复位；缺失、断连、重启区间断开曲线并着色标注。
- 复用现有 `status_info` 轮询做低频本地采样（默认 1 分钟，可配置），历史默认保留 30 天；
  分析区分放电、充电、已充满、数据缺口、断连与设备重启，不从两个点推算剩余续航。

## 10. 通信日志持久化与统一事件（v3.3）

- 统一日志文件：`%APPDATA%\M03XManager\activity.log`，JSON Lines 格式；
  单文件约 2 MB 后轮转为 `activity.1.log` … `activity.3.log`，默认保留 30 天。
- 内存日志页与磁盘文件使用同一数据源，支持按类别、级别和关键字查询；新增事件只增量刷新，
  不会在集合变更事件里递归刷新整个视图。
- 通知中心、设备连接/上线/离线、轮询与请求失败、会话恢复、电池采样、网络质量、软件启停、
  未捕获异常、配置与持久化异常统一进入该日志。
- 设备上线/离线/重启若固件没有事件时间线，日志明确写“本地检测时间”和判定依据，不伪造设备时间。
- 密码、Cookie、Authorization、Digest nonce/response、token 等写入前脱敏。
- 退出时同步冲刷待写队列；写入失败保留有界内存队列、写 `activity-write-error.txt` 并在界面告警，
  不会因日志故障递归写日志或阻止软件关闭。
