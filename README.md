# AirPods Head Mouse

用支持头部追踪的 AirPods 在 Windows 上控制鼠标。转头移动光标，开启「点头左键」后可以点头点击。蓝牙直连时，AirPods 直接和电脑通信。

## 开始使用

需要 64 位 Windows 10 或 11、蓝牙，以及可用的 [MagicAAP 驱动](https://magicpods.app/magicaap/)。下载包里有鼠标程序、姿态采集程序和启动脚本。MagicAAP 由其维护者单独提供。

1. 从 [Releases](https://github.com/gulzxeric/airpods-head-mouse/releases) 下载并解压 v0.2.0 或更新版本的 Windows ZIP。
2. 按 [MagicAAP 安装文档](https://magicpods.app/magicaap/) 选适合你 Windows 版本的驱动。按 [Apple 的说明](https://support.apple.com/en-gb/108786#nonapple) 在 Windows 的蓝牙设置里配对并连接 AirPods。戴上两只耳机。如果 MagicPods 在占用耳机控制连接，从系统托盘退出它。
3. 双击 `启动 AirPods 鼠标.cmd`，选耳机型号，再按 **T** 测试。先面朝屏幕片刻，再左右转头。终端应出现变化的 `YPR`，结束时 `sentSamples` 应大于 0。
4. 重新运行启动脚本，选择同一型号，再按 **S**。等窗口显示「已收到姿态」，点「启用鼠标控制」。AirPods 5 也可以继续用原来的两个专用脚本。

### 鼠标操作

| 操作 | 用法 |
| --- | --- |
| 移动 | 左右转头或抬头低头。可以调整灵敏度，也可以反转每个轴。 |
| 左键 | 勾选「点头左键」。把光标移到目标上，停一下，快速点头后回到原位。 |
| 右键 | 用实体鼠标右键。当前版本提供点头左键。 |
| 暂停或继续 | `Ctrl+Alt+F8`，或点击窗口里的按钮。 |
| 重置姿态 | `Ctrl+Alt+F9`。 |
| 退出 | 在采集终端按 `Ctrl+C`，再关闭鼠标窗口。 |

`No MagicAAP/AAP device interfaces` 表示程序没有找到驱动接口。先确认耳机已连接，并在设备管理器的「蓝牙」分类里检查 MagicAAP。`headPackets=0` 表示采集程序还没读到姿态报告。如果提示 UDP 4242 被占用，退出占用该端口的 OpenTrack 或其他实例后重试。

## 兼容性与连接方式

| 型号 | 蓝牙直连状态 |
| --- | --- |
| AirPods 5 | 本项目在 Windows 11 24H2 和社区版 MagicAAP 上验证过姿态采集和本机 UDP 接收。 |
| AirPods Pro 2 | 已接入 `devmotion6` 和对应的姿态轴校正。[HeadTracker 实测型号为 A2698](https://github.com/Untrosss/HeadTracker/tree/367b307764cd71052737c0080759bbee41d69e89#hardware-support)，本项目待真机复测。 |
| AirPods Max 2 | 已接入上游的专用采集方式。[AAP Head Tracker 已实测 A3454、固件 8E258](https://github.com/FIocker/aap-head-tracker#compatibility)，本项目待真机复测。 |
| AirPods 4（含降噪版） | 实验支持。接入了 [SoundStage 用于采集的启动方式](https://github.com/capriqqw/soundstage-for-airpods/tree/114143c27670275a5ef6336b91047d84b8a16eb5)及对应的姿态格式，本项目待真机验证。 |
| AirPods 3、AirPods Pro 1 / 3、AirPods Max 1 | 实验支持。提供采集预设，实际取决于固件返回的报文。 |
| 普通 AirPods 1 / 2 | 不在 [Apple 的头部追踪支持范围](https://support.apple.com/en-lb/guide/airpods/dev00eb7e0a3/27/web/27)内。 |

如果某个型号的 `sentSamples=0`，确认耳机已戴好并保持连接。也可以在 PowerShell 里试另一种启动格式。例如 AirPods 4 的备选命令：

```powershell
.\aap-headtracker.exe bridge --model airpods4 --start-format legacy --seconds 12
```

Windows 光标移动、左键事件和断流停止已用模拟姿态测试。

蓝牙模式通过 MagicAAP 读取耳机姿态，采集端用 OpenTrack 格式把数据发到本机 UDP 4242。鼠标端根据姿态变化生成 Windows 鼠标事件。

也可尝试 `启动 iPhone 转发.cmd`。AirPods 连 iPhone，手机和电脑连同一个局域网。在 [Smart Head Track](https://apps.apple.com/app/id1531547793) 选 **Headphone**，先确认预览随转头变化。Windows 启动脚本里填手机 IPv4，手机 App 里填电脑 IPv4 和端口 `4242`。该 App 的公开兼容列表没有列出 AirPods 5。 [Smart Head Track 的连接指南](https://www.tinacinc.com/SmartHeadTrack/faq.html)

## 从源码构建

鼠标端用 Windows 自带的 .NET Framework 编译器，采集端需要 .NET 9 SDK。在仓库根目录打开 PowerShell。

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /out:AirCursorWindows.exe AirCursorWindows.cs
dotnet publish .\Tracker\AAPHeadtracker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o .\Tracker\publish
Copy-Item .\Tracker\publish\aap-headtracker.exe .
.\aap-headtracker.exe --self-test
```

## 上游与许可证

- Windows 鼠标端在本仓库实现，交互灵感来自 [AirCursor](https://github.com/m-afham/AirCursor)。
- `Tracker/` 基于 [AAP Head Tracker 的 767bbd3 版本](https://github.com/FIocker/aap-head-tracker/tree/767bbd37b93979084bdcc3e4dc9b9f13c72f82ce)，增加了多个型号的采集预设、报告分帧和自测。
- AirPods 5 的启动和停止方式、报告布局以及相对姿态计算参考 [PodsOnDroid](https://github.com/HXLH50K/PodsOnDroid/tree/4cfd64e16c7e3872408bbac82aa8460acb2b1852) 和其上游 [LibrePods](https://github.com/kavishdevar/librepods)。

- AirPods Pro 2 的 RTBuddy 流报文和姿态轴校正参考 [HeadTracker](https://github.com/Untrosss/HeadTracker/tree/367b307764cd71052737c0080759bbee41d69e89)。AirPods 4 的启动方式和报文布局也参考 [SoundStage](https://github.com/capriqqw/soundstage-for-airpods/tree/114143c27670275a5ef6336b91047d84b8a16eb5)。

本项目使用 [GPL-3.0](LICENSE)，上游采集端的许可保留在 [Tracker/LICENSE](Tracker/LICENSE)。
