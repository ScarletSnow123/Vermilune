# Vermilune v1.2.14-beta

- 启动前自动检查并补全 Forge / Fabric 继承的原版及客户端 jar
- 启动前检查 classpath 运行库，缺失时自动补下；仍失败会明确列出缺失路径
- 报错分析新增“运行库文件缺失”，支持识别 Invalid paths argument
- 启动状态日志支持滚动查看
- Forge 安装无法访问 piston-data.mojang.com 时给出明确网络提示
- 启动前校验 Forge 版本 JSON 是否包含 Forge 库，缺库时直接提示重装
- Forge 安装失败时自动清理不完整的版本，避免生成可启动的破损实例
- 启动前校验 Forge universal 库，缺失时提示重新安装而不是无提示闪退
- Forge 安装失败时自动清理不完整的版本，避免启动破损实例
