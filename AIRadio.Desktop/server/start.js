// Auto-start NeteaseCloudMusicApi server for TuanRadio
const fs = require('fs');
const path = require('path');
const tmpPath = require('os').tmpdir();

// Set port and bind to localhost only before requiring the API module
process.env.PORT = '37250';
process.env.HOST = '127.0.0.1';

// 上游 app.js 的启动序列：anonymous_token 文件 + generateConfig + serveNcmApi。
// 不直接 require app.js 是因为需要在请求进 Express 前做 Origin 准入——上游只设
// CORS 响应头（反射 Origin），拦不住跨站简单请求发出并执行：任意网页可驱动本机
// 代理以用户 IP 调用上游。app.use 后挂不行（Express 栈按注册顺序分发，请求在上游
// 路由即被响应），改为包一层 http.Server 的 request 监听：准入通过后转交原处理器。
// 携带 Origin 的请求只放行同源与显式白名单 CORS_ALLOW_ORIGIN；
// 桌面 HttpClient 不发 Origin，不受影响。
async function start() {
  if (!fs.existsSync(path.resolve(tmpPath, 'anonymous_token'))) {
    fs.writeFileSync(path.resolve(tmpPath, 'anonymous_token'), '', 'utf-8');
  }
  await require('./node_modules/NeteaseCloudMusicApi/generateConfig')();
  const app = await require('./node_modules/NeteaseCloudMusicApi/server').serveNcmApi({
    checkVersion: true,
  });

  const server = app.server;
  const upstreamHandlers = server.listeners('request');
  server.removeAllListeners('request');
  server.on('request', (req, res) => {
    const origin = req.headers.origin;
    if (origin && origin !== `http://${req.headers.host}` && origin !== process.env.CORS_ALLOW_ORIGIN) {
      res.writeHead(403);
      res.end();
      return;
    }
    for (const handler of upstreamHandlers) {
      handler(req, res);
    }
  });

  console.log('TuanRadio Music API server started on port 37250');
}

start().catch((e) => {
  console.error('Failed to start music server:', e.message);
  process.exit(1);
});
