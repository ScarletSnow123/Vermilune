# Vermilune v1.2.15-beta

- 修复 Forge 启动时 ignoreList 使用错误版本名导致的模块冲突（原版 jar 与 Forge 模块重复）
- Forge 安装自动通过 BMCLAPI 下载映射表并跳过 DownloadMojmaps，避免 piston-data.mojang.com 卡住
- Forge 安装失败时自动清理破损版本，启动前校验 universal jar
- 保留自动补全原版、运行库缺失检测和启动日志滚动
