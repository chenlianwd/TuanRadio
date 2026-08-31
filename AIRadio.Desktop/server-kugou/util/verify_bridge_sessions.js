/**
 * @fileoverview 滑块验证会话桥的会话存储
 *
 * 桌面应用持有完整登录态（token/userid/dfid，经 Authorization 头进入代理），
 * 但浏览器验证页无法携带 token（项目约束：登录态严禁进 URL/日志/缓存键）。
 * 会话桥用一次性 sessionId 在两者之间中转身份：
 * - 桌面应用 POST /verify/bridge/start（带 Authorization）→ 换取 sessionId
 * - 验证页凭 sessionId 调 /verify/bridge/info、/verify/bridge/submit
 *   由代理用会话里的完整身份请求酷狗上游
 *
 * 会话仅存内存、短 TTL、数量上限，进程重启即清空。
 */

const crypto = require('node:crypto');

/** 会话有效期：6 分钟（桌面端轮询预算 5 分钟 + 1 分钟余量；超时由应用冷却后重新触发） */
const SESSION_TTL_MS = 6 * 60 * 1000;

/** 并发会话上限（FIFO 淘汰，防泄漏） */
const MAX_SESSIONS = 16;

/** @type {Map<string, {cookie: Record<string,string>, eventid: string, expires: number}>} */
const sessions = new Map();

/**
 * 创建验证会话
 * @param {Record<string,string>} cookie - 完整登录态（token/userid/dfid + 代理注入的设备标识）
 * @param {string} eventid - 风控挑战事件 ID（ssaCode）
 * @returns {string} sessionId
 */
function createSession(cookie, eventid) {
  const now = Date.now();
  // 惰性清理过期会话
  for (const [id, session] of sessions) {
    if (session.expires <= now) sessions.delete(id);
  }
  // 数量超限时淘汰最早创建的会话
  while (sessions.size >= MAX_SESSIONS) {
    sessions.delete(sessions.keys().next().value);
  }

  const id = crypto.randomBytes(24).toString('hex');
  sessions.set(id, { cookie, eventid, expires: now + SESSION_TTL_MS });
  return id;
}

/**
 * 查询验证会话（过期自动清除）
 * @param {string} id - sessionId
 * @returns {{cookie: Record<string,string>, eventid: string, expires: number}|null}
 */
function getSession(id) {
  if (!id || typeof id !== 'string') return null;
  const session = sessions.get(id);
  if (!session) return null;
  if (session.expires <= Date.now()) {
    sessions.delete(id);
    return null;
  }
  return session;
}

/**
 * 删除验证会话（验证成功后消费，一次性使用，防 TTL 内重放提交）
 * @param {string} id - sessionId
 */
function removeSession(id) {
  if (id && typeof id === 'string') sessions.delete(id);
}

module.exports = { createSession, getSession, removeSession };
