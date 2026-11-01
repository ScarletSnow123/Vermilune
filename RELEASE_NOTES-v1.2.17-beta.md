# Vermilune v1.2.17-beta

- 修复 Forge 启动时 ignoreList 版本名错误导致的模块冲突
- Forge 部署前自动创建 launcher_profiles.json，安装器不再直接退出
- 自动修复损坏或缺少 profiles 的 launcher_profiles.json
- 重装 Forge 前自动备份旧版本目录，修复已有版本导致安装器立即退出的问题
- 缺少 forge-universal.jar 时自动从 Forge Maven 补全
- Forge 安装改用 BMCLAPI 映射表，跳过 DownloadMojmaps，不再卡 piston-data.mojang.com
