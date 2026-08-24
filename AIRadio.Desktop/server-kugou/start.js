// 与 server/start.js(网易云本地代理)同构:酷狗本地 API 服务入口。
// KuGouMusicApi(MakcRe/KuGouMusicApi, MIT)通过 PORT/HOST 环境变量读取监听参数。
process.env.PORT = process.env.PORT || '37251';
process.env.HOST = process.env.HOST || '127.0.0.1';
require('./app.js');
