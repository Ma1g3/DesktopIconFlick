# Proposal

## Why

现在只能在自己的电脑上单独训练，朋友之间要靠口头报分才能比较。局域网竞赛让同一网络里的玩家一起开局，打完直接看到同一份排名。

## What Changes

- 增加「局域网竞赛」入口。首版支持 2 至 8 人，一台电脑建房，其他玩家输入房主显示的 IPv4 地址和端口加入，无需账号或独立服务器。
- 大家准备后由房主发起倒计时。每人打自己屏幕上的三枚靶，共用开局靶位、时限和补靶规则。局内 DPI、水平 FOV、图标或小球和声音仍按各自设置。
- 每轮有共同的 30 秒计时，按命中数降序排名，同分并列，同时展示射击次数和命中率。中途放弃或掉线的玩家单独标记，留在房间的玩家可以再赛。
- 单机训练入口继续提供暂停、恢复和本地个人最佳。局域网竞赛成绩只用于当轮排名。

## Capabilities

### New Capabilities

- `lan-competition` 规定建房、加入、准备、同步开赛、排名和异常退出。

### Modified Capabilities

- `flick-training` 区分单机活动时间与竞赛统一时限，明确个人最佳和 HUD 在两种模式下的行为。
- `desktop-training-layer` 把玩家主动准备、前台开赛及竞赛中断时的输入释放纳入桌面叠层约束。

## Impact

涉及 `TrainingWindow` 的页面和输入生命周期、`TrainingRound` 的计时与靶位生成、`TrainingProfile` 个人最佳的调用路径，以及局域网房间、测试、使用说明与两台 Windows 电脑的桌面验收。沿用 Windows 10/11 x64、.NET Framework 4.8 和现有 PowerShell 构建。
