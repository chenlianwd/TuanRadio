FROM node:lts-alpine

RUN apk add --no-cache tini

ENV NODE_ENV=production

WORKDIR /app

RUN chown node:node /app

COPY --chown=node:node package.json package-lock.json ./

USER node

# 按锁文件精确安装且禁用安装脚本（本服务依赖树为纯 JS，无原生编译需求）；
# 不硬编码第三方镜像源，registry 随构建环境默认值
RUN npm ci --omit=dev --ignore-scripts

COPY --chown=node:node . ./

# 容器内必须绑 0.0.0.0 端口映射才可达（显式声明以免依赖隐式默认）。
# 代理侧 Origin 请求准入只放行同源与 CORS_ALLOW_ORIGIN 白名单：
# 公开部署必须显式配置 CORS_ALLOW_ORIGIN 为真实站点 origin，否则跨域请求一律 403
ENV HOST=0.0.0.0

EXPOSE 3000

ENTRYPOINT ["/sbin/tini", "--"]
CMD ["node", "app.js"]
