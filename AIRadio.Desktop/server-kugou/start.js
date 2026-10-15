// 与 server/start.js(网易云本地代理)同构:酷狗本地 API 服务入口。
// KuGouMusicApi(MakcRe/KuGouMusicApi, MIT)通过 PORT/HOST 环境变量读取监听参数。
// 无条件覆盖：父进程环境若带 PORT/HOST（Web 开发机常见）会让代理绑到 0.0.0.0
// 静默对局域网暴露，且健康检查无法发现
process.env.PORT = '37251';
process.env.HOST = '127.0.0.1';
require('./app.js');
