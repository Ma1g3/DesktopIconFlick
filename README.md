# 桌面甩枪 · Desktop Icon Flick

在自己的 Windows 桌面上练快速切靶。壁纸和任务栏保持原位，准星固定在视口中央；鼠标转动游戏视角，左键击发。默认用桌面图标当圆靶外观，也可以取消勾选改用普通小球。

## 运行

在 Windows 10/11 x64 的 PowerShell 7（`pwsh`）中，从项目根目录运行：

```powershell
.\build.ps1
```

双击 `bin\DesktopIconFlick.exe`。需要 .NET Framework 4.8（Windows 11 和新版 Windows 10 已内置），构建不需要 .NET SDK 或外部包。移动程序时请保留相邻的 `DesktopIconFlick.exe.config`。训练层透过的是窗口后方的真实画面：若看到其他应用而不是壁纸，先最小化挡住桌面的窗口。

1. 先露出主屏桌面，再打开程序，点击「开始训练」。
2. 设置与硬件一致的鼠标 DPI，并使用熟悉的 `cm/360` 和水平 FOV。默认值是 800 DPI、40 cm/360、100°。
3. 30 秒内，三枚靶子中打掉一枚就补一枚。`Esc` 或切换窗口会暂停，点击「继续训练」才重新锁定鼠标；暂停时再次按 `Esc` 可退出。

程序不截取桌面、不修改或打开真实图标，也不联网。设置与个人最佳保存在 `%LOCALAPPDATA%\DesktopIconFlick\profile.txt`。

## 验证

```powershell
.\build.ps1 -Test
.\tests\desktop-smoke.ps1
$env:OPENSPEC_TELEMETRY='0'
openspec validate add-desktop-flick-mvp --strict
```

自动测试覆盖灵敏度、投影、三靶补位、暂停时钟、本地持久化、鼠标接管失败及离屏渲染。冒烟脚本在交互式 Windows 会话中检查物理窗口边界、空白叠层是否接收鼠标命中，以及启动时光标是否自由。实际点击、真实鼠标手感和多屏边界另需验收，见 [桌面验收清单](docs/desktop-acceptance.md)。
