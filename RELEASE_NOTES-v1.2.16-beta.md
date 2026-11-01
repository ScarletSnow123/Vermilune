# Vermilune v1.2.16-beta

- 修复 Forge 启动时 ignoreList 使用错误版本名导致的模块冲突，游戏可正常进入
- Forge 安装自动从 BMCLAPI 获取映射表并跳过 DownloadMojmaps，不再卡 piston-data.mojang.com
- 安装失败自动清理破损版本，启动前校验 universal jar
- 保留原版 / 运行库自动补全与启动日志滚动
