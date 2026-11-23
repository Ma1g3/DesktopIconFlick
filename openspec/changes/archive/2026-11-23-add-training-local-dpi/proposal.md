# Proposal

## Why

玩家在开局前通常只想直接调快或调慢训练转向。现有流程要求先填写硬件 DPI 和转身距离，增加了不需要精确校准的玩家的操作负担。

## What Changes

- 开局页提供一个「局内 DPI」输入。数值越大，同样的鼠标输入产生的训练转角越大。调整只作用于训练视角。
- 把硬件 DPI 与 `cm/360` 放入默认收起的高级校准。应用校准后，将精确转身距离换算成主界面上的局内 DPI。
- 读取旧配置时按原转角换算新值，沿用个人最佳和其他设置。

## Capabilities

### New Capabilities

### Modified Capabilities

- `flick-training` 增加默认的局内 DPI 设置与可选的物理距离校准，并规定旧配置的兼容行为。

## Impact

涉及 `TrainingWindow` 的开局设置区、`AimSettings` 的角度换算、`TrainingProfile` 的兼容读写，以及使用说明和验收清单。
