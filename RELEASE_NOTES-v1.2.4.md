# Vermilune v1.2.4-beta

这一版主要把「装整合包」和「第一次进游戏」这两步补齐了。

## 拖入整合包，直接安装

- 把 CurseForge / Modrinth 整合包 `.zip` 拖到启动器窗口任意位置即可开始安装
- 自动识别 `manifest.json`（CurseForge）或 `modrinth.index.json`（Modrinth）
- 自动安装对应 Forge / Fabric 加载器
- 自动下载整合包文件、解压 `overrides`、创建独立版本实例
- 安装过程显示进度，支持取消
- CurseForge 整合包需要先在设置中填写 API Key
- 只有 `mods` 文件夹、没有索引文件的普通压缩包会明确提示不支持

## 新实例默认中文

- 启动前检查实例目录的 `options.txt`
- 文件不存在，或存在但没有 `lang:` 行时，自动写入 `lang:zh_cn`
- 已经手动选过语言的不会被覆盖
- 开启版本隔离时写入对应实例目录，不影响其他版本

## 顺手修掉的问题

- 切换皮肤后 3D 预览和账号头像不刷新
- 头像改为直接从当前皮肤裁切，不再依赖第三方头像缓存
- 后台挂 VPN / 代理时披风读取失败：
  - 完整披风列表读取失败时，回退到 sessionserver 读取当前披风
  - 披风贴图 HTTPS 失败时自动回退 HTTP
- 多账号保存与免登录切换：Microsoft、离线、Yggdrasil 外置账号都可以保留并直接切换

## 升级说明

- 版本号：`v1.2.4-beta`
- 目标框架：`.NET 10 + WPF`
- 发布方式：框架依赖单文件，需要安装 .NET 10 Desktop Runtime
- 旧配置、皮肤库、账号信息会保留
