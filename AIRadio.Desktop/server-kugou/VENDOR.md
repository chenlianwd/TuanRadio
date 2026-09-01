# KuGouMusicApi vendor 说明

- 上游仓库：`https://github.com/MakcRe/KuGouMusicApi`
- 选择性同步基线：`bcf4b8af1c4c514b8c15fc1233b03d5b4377aab5`（2026-08-31）
- 对齐版本：`1.6.2`

本目录保留 TuanRadio 自有的滑块验证会话桥、令牌不进 URL、日志脱敏和进程级指纹稳定化，不能用上游目录整体覆盖。

本次仅引入 `user_verify`、`song_auth`、`song_url_auth`、`song_url_auth_merge` 四个 Auth 播放模块；`song_url_auth_merge` 额外修复了上游在授权数据缺失时解构 `undefined` 的异常路径。
