// Vermilune CurseForge 中转 Worker
// 部署后把 https://<worker>.workers.dev 填到启动器「设置 → 下载 → CurseForge 中转地址」。
// 密钥只放在 Worker 的环境变量里，不进入启动器 exe。

export default {
  async fetch(request, env) {
    const cors = {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Headers": "x-vermilune-token, content-type",
      "Access-Control-Allow-Methods": "GET, OPTIONS",
      "Cache-Control": "no-store",
    };

    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: cors });
    }
    if (request.method !== "GET") {
      return new Response("Method Not Allowed", { status: 405, headers: cors });
    }

    const token = env.PROXY_TOKEN || "";
    if (token && request.headers.get("x-vermilune-token") !== token) {
      return new Response("Unauthorized", { status: 401, headers: cors });
    }

    const url = new URL(request.url);
    if (!url.pathname.startsWith("/v1/")) {
      return new Response("Not Found", { status: 404, headers: cors });
    }

    if (!env.CURSEFORGE_API_KEY) {
      return new Response("CURSEFORGE_API_KEY is not configured", { status: 500, headers: cors });
    }

    const target = "https://api.curseforge.com" + url.pathname + url.search;
    const upstream = await fetch(target, {
      method: "GET",
      headers: {
        "Accept": "application/json",
        "x-api-key": env.CURSEFORGE_API_KEY,
      },
    });

    const body = await upstream.arrayBuffer();
    const headers = new Headers(cors);
    headers.set("Content-Type", upstream.headers.get("Content-Type") || "application/json");
    return new Response(body, { status: upstream.status, headers });
  },
};
