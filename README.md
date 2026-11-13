# AirPods Head Mouse

简体中文 · [English](README.en.md)

**[下载 Windows x64 运行包 v0.2.0](https://github.com/gulzxeric/airpods-head-mouse/releases/download/v0.2.0/AirPods-Head-Mouse-v0.2.0-windows-x64.zip)**

戴着 AirPods 转头控制 Windows 鼠标，点头可触发左键。蓝牙直连只需要耳机和电脑。

## 安装和启动

1. **下载运行包并全部解压。** 在 GitHub Release 的 **Assets** 里选 `AirPods-Head-Mouse-v0.2.0-windows-x64.zip`。GitHub 的 **Code → Download ZIP** 和 **Source code (zip)** 是源码下载入口，下载后需要自行编译。右键运行包选「全部解压缩」，打开解压后的文件夹，确认 `启动 AirPods 鼠标.cmd`、`aap-headtracker.exe` 和 `AirCursorWindows.exe` 在同一层。
2. **安装 MagicAAP 并连接耳机。** 蓝牙直连需要 64 位 Windows 10 或 11，以及单独安装的 MagicAAP 驱动。连接后，可在「设备管理器 → 蓝牙」查看 `MagicAAP`。如果 MagicPods 正在使用驱动，先从系统托盘退出它。 [MagicAAP 驱动安装](https://magicpods.app/magicaap/) · [AirPods 蓝牙配对](https://support.apple.com/en-gb/108786#nonapple)
3. **先测试头部追踪。** 双击 `启动 AirPods 鼠标.cmd`，选耳机型号，再按 **T**。AirPods Pro 2 选 **2**。戴好耳机，先面朝屏幕片刻，再转头。看到 `YPR` 数字跟着变化，且测试结束时 `sentSamples` 大于 0，就可以启动鼠标控制。
4. **启用鼠标控制。** 重新双击同一个脚本，选择型号，再按 **S**。等窗口显示「已收到姿态」，点「启用鼠标控制」。

## 鼠标操作

| 操作 | 用法 |
| --- | --- |
| 移动 | 左右转头或抬头低头。方向和灵敏度可以在窗口里调。 |
| 左键 | 勾选「点头左键」。把光标移到目标上，停一下，快速点头后回到原位。 |
| 右键 | 用实体鼠标右键。 |
| 暂停或继续 | 按 `Ctrl+Alt+F8`。 |
| 重置姿态 | 按 `Ctrl+Alt+F9`。 |
| 退出 | 在黑色终端按 `Ctrl+C`，再关闭鼠标窗口。 |

## 耳机兼容性

| 型号 | 蓝牙直连验证情况 |
| --- | --- |
| AirPods 5 | 本项目在 Windows 11 24H2 和社区版 MagicAAP 上实测过姿态采集和本机数据接收。 |
| AirPods Pro 2 | 有专用预设。上游项目实测过 A2698，本项目仍待真机验证。 |
| AirPods Max 2 | 有专用预设。上游项目实测过 A3454、固件 8E258，本项目仍待真机验证。 |
| AirPods 3、AirPods 4（含降噪版）、AirPods Pro 1 / 3、AirPods Max 1 | 实验预设，需用 **T** 验证具体型号和固件。 |
| 普通 AirPods 1 / 2 | 缺少此程序所需的头部追踪支持。 |

## 常见问题

| 提示或现象 | 怎么处理 |
| --- | --- |
| `aap-headtracker.exe is missing` 或 `AirCursorWindows.exe is missing` | 用页面顶部的运行包，全部解压后再打开脚本。若正确解压后文件仍缺失，查看 Windows 安全中心的「保护历史记录」。 |
| `No MagicAAP/AAP device interfaces` 或 `Could not open a MagicAAP interface` | 在 Windows 蓝牙设置确认耳机已连接，在设备管理器检查 MagicAAP。若 MagicPods 正在运行，从托盘退出再试。 |
| `sentSamples=0` 或转头时 `YPR` 不变 | 核对所选型号，戴好耳机，面朝屏幕保持片刻再测试。实验预设可能受耳机固件影响。 |
| UDP 4242 被占用 | 关闭正在监听该端口的 OpenTrack 或其他程序实例，再启动。 |

## 可选的 iPhone 转发

把 AirPods 连到 iPhone，让 iPhone 和电脑接入同一局域网。在 Smart Head Track 里选 **Headphone**，先确认转头时预览会动。运行 `启动 iPhone 转发.cmd` 并填手机的 IPv4 地址，再在手机 App 里填电脑的 IPv4 地址和端口 `4242`。手机 App 对 AirPods 5 的兼容性仍待验证。 [Smart Head Track](https://apps.apple.com/app/id1531547793) · [连接说明](https://www.tinacinc.com/SmartHeadTrack/faq.html)

## 从源码构建

鼠标端用 Windows 自带的 .NET Framework 编译器，采集端需要 .NET 9 SDK。在仓库根目录打开 PowerShell。

~~~powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /out:AirCursorWindows.exe AirCursorWindows.cs
dotnet publish .\Tracker\AAPHeadtracker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o .\Tracker\publish
Copy-Item .\Tracker\publish\aap-headtracker.exe .
.\aap-headtracker.exe --self-test
~~~

如需排查 AirPods 4 的实验预设，可以试 `.\aap-headtracker.exe bridge --model airpods4 --start-format legacy --seconds 12`。

## 致谢和许可证

- Windows 鼠标端的交互灵感来自 AirCursor。 [AirCursor](https://github.com/m-afham/AirCursor)
- `Tracker/` 基于 AAP Head Tracker。Max 2 的 A3454 实测记录也来自该项目。 [AAP Head Tracker 源码](https://github.com/FIocker/aap-head-tracker/tree/767bbd37b93979084bdcc3e4dc9b9f13c72f82ce) · [Max 2 实测记录](https://github.com/FIocker/aap-head-tracker#compatibility)
- Pro 2 的报文格式和姿态轴校正参考 HeadTracker，AirPods 4 的采集方式参考 SoundStage。 [Pro 2 A2698 实测记录](https://github.com/Untrosss/HeadTracker#hardware-support) · [SoundStage](https://github.com/capriqqw/soundstage-for-airpods/tree/114143c27670275a5ef6336b91047d84b8a16eb5)
- AirPods 5 的采集方式参考 PodsOnDroid 和 LibrePods。 [PodsOnDroid](https://github.com/HXLH50K/PodsOnDroid/tree/4cfd64e16c7e3872408bbac82aa8460acb2b1852) · [LibrePods](https://github.com/kavishdevar/librepods)

项目采用 GPL-3.0。 [项目许可证](LICENSE) · [采集端许可证](Tracker/LICENSE)
