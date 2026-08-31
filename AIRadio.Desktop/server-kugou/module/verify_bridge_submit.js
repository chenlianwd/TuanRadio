const { getSession, removeSession } = require('../util/verify_bridge_sessions');
const verifyUserInfo = require('./verify_user_info');
const { generateSimulate } = require('../util/generate_simulate');

/**
 * 滑块验证会话桥（三）：提交验证结果
 *
 * 验证页凭 sessionId + verifycode（腾讯滑块 ticket 或短信码）调用；
 * sid/edt 由代理按应用完整身份（mid/userid/dfid/webgl）现场生成，
 * 页面无需引入行为指纹库。
 */
module.exports = async (params, useAxios) => {
  const session = getSession(params?.sessionId);
  if (!session) {
    return {
      status: 404,
      body: { status: 0, error: 'verify session expired or not found' },
      cookie: [],
    };
  }

  if (!params?.verifycode) {
    return {
      status: 400,
      body: { status: 0, error: 'verifycode is required' },
      cookie: [],
    };
  }

  const cookie = Object.assign({}, params?.cookie || {}, session.cookie);
  const { sid, edt } = generateSimulate(
    cookie.KUGOU_API_MID,
    cookie.userid || 0,
    cookie.dfid || '-',
    cookie.KUGOU_API_WEBGL
  );

  // verifyUserInfo 经 createRequest 返回 Promise，必须 await 后才能读 body
  const result = await verifyUserInfo(
    {
      eventid: session.eventid,
      v_type: params?.v_type,
      verifycode: params.verifycode,
      sid,
      edt,
      cookie,
    },
    useAxios
  );

  // 验证成功即消费会话：sessionId 一次性使用，TTL 内不可重放提交
  if (result?.body?.status) removeSession(params.sessionId);
  return result;
};
