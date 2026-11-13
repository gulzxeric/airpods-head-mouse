# AirPods Head Mouse

[简体中文](README.md) · English

**[Download the Windows x64 package (v0.2.0)](https://github.com/gulzxeric/airpods-head-mouse/releases/download/v0.2.0/AirPods-Head-Mouse-v0.2.0-windows-x64.zip)**

Move the Windows pointer by turning your head while wearing AirPods. Enable nod-to-click for a left click. In Bluetooth mode, the AirPods connect directly to the PC.

## Set up and launch

1. **Download and extract the full package.** Under **Assets** on the GitHub release page, choose `AirPods-Head-Mouse-v0.2.0-windows-x64.zip`. GitHub’s **Code → Download ZIP** and **Source code (zip)** downloads require a build. Right-click the Windows ZIP and choose **Extract All**. Open the extracted folder and check that `启动 AirPods 鼠标.cmd`, `aap-headtracker.exe`, and `AirCursorWindows.exe` are together.
2. **Install MagicAAP and connect your AirPods.** Direct Bluetooth mode requires 64-bit Windows 10 or 11 and the separately installed MagicAAP driver. After connecting, look for `MagicAAP` under **Bluetooth** in Device Manager. If MagicPods is using the driver, quit it from the system tray. [MagicAAP installation](https://magicpods.app/magicaap/) · [Pair AirPods over Bluetooth](https://support.apple.com/en-gb/108786#nonapple)
3. **Test head tracking.** Open `启动 AirPods 鼠标.cmd`, select your model, and press **T**. For AirPods Pro 2, choose **2**. Wear the AirPods, face forward for a moment, then turn your head. Check that the `YPR` values change and `sentSamples` is greater than zero at the end of the test.
4. **Enable mouse control.** Open the same script again, select your model, and press **S**. When the window shows `已收到姿态` (tracking data received), click `启用鼠标控制` (Enable mouse control). The current app window uses Chinese labels.

## Mouse controls

| Action | How to use it |
| --- | --- |
| Move | Turn your head left or right, or look up or down. Adjust sensitivity and axis direction in the window. |
| Left-click | Turn on `点头左键` (Nod to left-click). Aim at the target, pause briefly, then nod and return to your starting position. |
| Right-click | Use the right button on a physical mouse. |
| Pause or resume | Press `Ctrl+Alt+F8`. |
| Recenter | Press `Ctrl+Alt+F9`. |
| Exit | Press `Ctrl+C` in the console, then close the mouse control window. |

## AirPods compatibility

| Model | Direct Bluetooth verification |
| --- | --- |
| AirPods 5 | This project verified motion capture and local data reception on Windows 11 24H2 with the community MagicAAP driver. |
| AirPods Pro 2 | Dedicated preset. The upstream project tested model A2698. Hardware verification in this app is pending. |
| AirPods Max 2 | Dedicated preset. The upstream project tested model A3454 with firmware 8E258. Hardware verification in this app is pending. |
| AirPods 3, AirPods 4 (including ANC), AirPods Pro 1 / 3, AirPods Max 1 | Experimental presets. Use **T** to test your model and firmware. |
| Standard AirPods 1 / 2 | These models lack the head tracking required by this app. |

## Troubleshooting

| Message or symptom | What to do |
| --- | --- |
| `aap-headtracker.exe is missing` or `AirCursorWindows.exe is missing` | Download the Windows package above, extract it fully, and run the script from the extracted folder. If a file is still missing, check **Protection history** in Windows Security. |
| `No MagicAAP/AAP device interfaces` or `Could not open a MagicAAP interface` | Confirm the AirPods are connected in Windows Bluetooth settings and check MagicAAP in Device Manager. If MagicPods is running, quit it from the system tray and try again. |
| `sentSamples=0` or `YPR` does not change | Check your model selection, wear the AirPods, and hold still facing forward briefly before testing again. Experimental presets may depend on firmware. |
| UDP port 4242 is in use | Close OpenTrack or any other instance listening on that port, then launch again. |

## Optional iPhone relay

Connect your AirPods to the iPhone and put the iPhone and PC on the same local network. In Smart Head Track, select **Headphone** and check that the preview responds when you turn your head. Run `启动 iPhone 转发.cmd` and enter the iPhone’s IPv4 address. In the iPhone app, enter the PC’s IPv4 address and port `4242`. AirPods 5 compatibility with this iPhone app is still unverified. [Smart Head Track](https://apps.apple.com/app/id1531547793) · [Connection guide](https://www.tinacinc.com/SmartHeadTrack/faq.html)

## Build from source

The mouse control app uses the Windows .NET Framework compiler. The tracker requires the .NET 9 SDK. Open PowerShell in the repository root.

~~~powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /out:AirCursorWindows.exe AirCursorWindows.cs
dotnet publish .\Tracker\AAPHeadtracker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o .\Tracker\publish
Copy-Item .\Tracker\publish\aap-headtracker.exe .
.\aap-headtracker.exe --self-test
~~~

When troubleshooting the experimental AirPods 4 preset, you can try `.\aap-headtracker.exe bridge --model airpods4 --start-format legacy --seconds 12`.

## Credits and license

- AirCursor inspired the mouse controls. [AirCursor](https://github.com/m-afham/AirCursor)
- `Tracker/` builds on AAP Head Tracker, which also documented the AirPods Max 2 test on model A3454. [AAP Head Tracker source](https://github.com/FIocker/aap-head-tracker/tree/767bbd37b93979084bdcc3e4dc9b9f13c72f82ce) · [Max 2 test record](https://github.com/FIocker/aap-head-tracker#compatibility)
- HeadTracker provided a reference for the Pro 2 report format and axis correction. SoundStage provided a reference for AirPods 4 capture. [Pro 2 A2698 test record](https://github.com/Untrosss/HeadTracker#hardware-support) · [SoundStage](https://github.com/capriqqw/soundstage-for-airpods/tree/114143c27670275a5ef6336b91047d84b8a16eb5)
- PodsOnDroid and LibrePods provided references for AirPods 5 capture. [PodsOnDroid](https://github.com/HXLH50K/PodsOnDroid/tree/4cfd64e16c7e3872408bbac82aa8460acb2b1852) · [LibrePods](https://github.com/kavishdevar/librepods)

The project is licensed under GPL-3.0. [Project license](LICENSE) · [Tracker license](Tracker/LICENSE)
