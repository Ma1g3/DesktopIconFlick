# Repository Guidelines

## 项目结构

- `src/` 存放 C# WPF 应用。`Program.cs` 是入口，`Training.cs` 处理瞄准计算和对局逻辑，`TrainingProfile.cs` 负责本地设置和个人最佳成绩。
- `TrainingWindow.cs` 管理桌面叠层，`RangeSurface.cs` 绘制靶子和准星，`WindowsDesktop.cs` 和 `MouseSession.cs` 负责 Windows 集成和鼠标接管。图标靶的素材在运行时从 Windows Shell 读取。
- `tests/` 存放测试运行器和桌面探测工具。`docs/desktop-acceptance.md` 提供手动验收清单。`openspec/changes/` 存放行为规范、设计和任务清单。

## 构建、测试与运行

在配有 .NET Framework 4.8 的 Windows 10 或 11 x64 上，从仓库根目录使用 PowerShell 7 执行以下命令。

- `.\build.ps1` 构建应用。用 `.\bin\DesktopIconFlick.exe` 启动，并将生成的 `.exe.config` 保留在可执行文件旁。
- `.\build.ps1 -Test` 构建应用并运行 C# 断言测试。
- `.\tests\desktop-smoke.ps1` 检查窗口边界、叠层命中和启动时光标是否可自由移动。先完成测试构建，再关闭已有应用实例，在交互式 Windows 会话中运行。
- 安装 OpenSpec CLI 后，运行 `openspec validate add-desktop-flick-mvp --strict` 严格校验 MVP 变更。

## 代码风格与命名

使用四个空格缩进，C# 代码块的大括号另起一行。类型和方法用 PascalCase，参数、局部变量和私有字段用 camelCase。保持 C# 5 兼容，构建会将编译器警告视为错误。界面文案和 OpenSpec 正文使用简体中文。

## 测试约定

`tests/Tests.cs` 中的独立 C# 运行器根据断言结果判定测试是否通过。新增测试按行为命名，例如 `PauseAtDeadline`，并在 `Tests.Main` 中调用。复用 `Require` 和 `Near` 断言。涉及随机数或时间时，使用固定种子或可控时钟。修改输入、叠层或 DPI 行为时，按 `docs/desktop-acceptance.md` 验收，并记录显示器配置和结果。

## 提交与拉取请求

仓库尚无提交历史，建议使用简短、以动词开头的提交标题，例如 `修复失焦时的光标释放`。拉取请求应概述改动、列出已运行的检查，并关联相关 Issue 或 OpenSpec 变更。界面变更附截图，输入或显示变更附桌面验收结果。提交时排除 `bin/` 和 `obj/` 下的生成文件。
