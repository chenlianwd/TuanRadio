const { createSession } = require('../util/verify_bridge_sessions');

/**
 * 滑块验证会话桥（一）：开启会话
 *
 * 桌面应用携带 Authorization（token/userid/dfid 完整登录态）与 eventid 调用，
 * 换取一次性 sessionId 交给浏览器验证页使用；登录态全程不进 URL。
 */
module.exports = (params) => {
  const eventid = params?.eventid || '';
  const cookie = params?.cookie || {};
  if (!eventid || !cookie.token || !cookie.userid) {
    return {
      status: 400,
      body: { status: 0, error: 'eventid and logged-in cookie (token/userid) are required' },
      cookie: [],
    };
  }

  const sessionId = createSession(cookie, eventid);
  return { status: 200, body: { status: 1, data: { sessionId } }, cookie: [] };
};
