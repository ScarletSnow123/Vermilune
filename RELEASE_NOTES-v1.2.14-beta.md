# Vermilune v1.2.14-beta

- 启动前自动检查并补全 Forge / Fabric 继承的原版及客户端 jar
- 启动前检查 classpath 运行库，缺失时自动补下；仍失败会明确列出缺失路径
- 报错分析新增“运行库文件缺失”，支持识别 Invalid paths argument
- 启动状态日志支持滚动查看
