# 桌面甩枪 · Desktop Icon Flick

在自己的 Windows 桌面上练快速切靶。壁纸和任务栏保持原位，准星固定在视口中央；鼠标转动游戏视角，左键击发。默认直接用桌面图标的图像做靶子，保留图标本身的形状和透明区域。也可以取消勾选改用普通小球。

## 运行

在 Windows 10/11 x64 的 PowerShell 7（`pwsh`）中，从项目根目录运行：

```powershell
.\build.ps1
```

双击 `bin\DesktopIconFlick.exe`。需要 .NET Framework 4.8（Windows 11 和新版 Windows 10 已内置），构建不需要 .NET SDK 或外部包。移动程序时请保留相邻的 `DesktopIconFlick.exe.config`。训练层透过的是窗口后方的真实画面：若看到其他应用而不是壁纸，先最小化挡住桌面的窗口。

1. 先露出主屏桌面，再打开程序。图标读取完成后，点击「开始训练」。
2. 设置与硬件一致的鼠标 DPI，并使用熟悉的 `cm/360` 和水平 FOV。默认值是 800 DPI、40 cm/360、100°。
3. 30 秒内，三枚靶子集中在开局朝向附近。打掉一枚后立即在同一区域补一枚。能定位的图标和文字也会从桌面原位暂时消失，直到本局暂停或结束。点击「继续训练」可恢复本局，暂停时再次按 `Esc` 可退出。

可定位的图标原位会露出对齐的壁纸。Wallpaper Engine 使用独立壁纸窗口时，露出的背景会继续播放。壁纸无法可靠显示时，原位图标仍可见。

程序不截取桌面、不修改或打开真实图标，也不联网。设置与个人最佳保存在 `%LOCALAPPDATA%\DesktopIconFlick\profile.txt`。

## 验证

```powershell
.\build.ps1 -Test
.\tests\desktop-smoke.ps1
$env:OPENSPEC_TELEMETRY='0'
openspec validate --specs --strict
```

自动测试覆盖灵敏度、连续补位、暂停时钟、图标来源和壁纸遮盖。冒烟脚本在交互式 Windows 会话中检查物理窗口边界、空白叠层是否接收鼠标命中，以及启动时光标是否自由。实机试玩步骤见 [桌面验收清单](docs/desktop-acceptance.md)。
