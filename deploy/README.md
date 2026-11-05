# Vermilune CurseForge 中转

把 CurseForge API Key 放在 Cloudflare Worker 上，启动器只请求 Worker，exe 里不包含 Key。

## 部署

在 `deploy` 目录执行：

```powershell
npm install -g wrangler
wrangler login
wrangler deploy
wrangler secret put CURSEFORGE_API_KEY
wrangler secret put PROXY_TOKEN
```

- `CURSEFORGE_API_KEY`：你的 CurseForge Key
- `PROXY_TOKEN`：自定义的一串随机字符串，用来防止别人白嫖你的 Worker

部署完成后会得到类似：

```text
https://vermilune-curseforge-proxy.<你的账号>.workers.dev
```

## 启动器设置

打开：

```text
设置 → 下载
```

填写：

- CurseForge 中转地址：上面的 Worker 地址
- 中转访问令牌：与 `PROXY_TOKEN` 相同

保存后不需要再填 CurseForge API Key。

## 说明

- Worker 只放行 `GET /v1/...`
- 请求会转发到 `https://api.curseforge.com`
- Key 只存在 Worker 环境变量，不进入客户端
- 建议在 Cloudflare 控制台加 Rate Limiting，避免额度被刷
