# Vermilune v1.2.15-beta

- 启动前校验 Forge 安装完整性（版本 JSON 的 forge 库 + universal jar）
- Forge 安装失败自动清理破损版本，避免启动半成品
- 安装时无法访问 piston-data.mojang.com 会明确提示网络 / 下载源问题
- 保留自动补全原版、运行库缺失检测和启动日志滚动
