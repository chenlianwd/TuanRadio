const { getSession } = require('../util/verify_bridge_sessions');
const getVerifyInfo = require('./get_verify_info');

/**
 * 滑块验证会话桥（二）：获取验证类型
 *
 * 验证页凭 sessionId 调用；上游请求由代理用会话保存的完整登录态发出，
 * 返回 v_type（23 腾讯滑块 / 32 短信 / 38 需重新登录确认）与 txappid。
 */
module.exports = (params, useAxios) => {
  const session = getSession(params?.sessionId);
  if (!session) {
    return {
      status: 404,
      body: { status: 0, error: 'verify session expired or not found' },
      cookie: [],
    };
  }

  // 浏览器请求自带代理注入的 KUGOU_API_*（mid/webgl 与服务进程一致），
  // 应用登录态（token/userid/dfid）覆盖其上，构成与触发挑战时一致的完整身份。
  const cookie = Object.assign({}, params?.cookie || {}, session.cookie);
  return getVerifyInfo({ eventid: session.eventid, cookie }, useAxios);
};
