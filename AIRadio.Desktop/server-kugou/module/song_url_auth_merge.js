const { randomString } = require('../util/util');
const songAuth = require('./song_auth');
const songAuthURL = require('./song_url_auth');

// 先换取单曲 auth/open_time，再请求 v5 播放地址。
module.exports = async (params, useAxios) => {
  const answer = { status: 500, body: {}, cookie: [] };
  const hash = (params?.hash || '').toLowerCase();
  const album_audio_id = Number(params.album_audio_id ?? 0);
  const cookie = Object.assign({}, { dfid: randomString(24) }, params?.cookie);
  const authorization = params.auth || params.cookie?.auth || '';

  const authData = await songAuth(
    { hash, album_audio_id, auth: authorization, cookie },
    useAxios,
  );
  const authPayload = authData?.body?.data || {};
  const { auth, open_time } = authPayload;
  if (!auth || !open_time) {
    throw { ...answer, body: { error: '获取 auth 和 open_time 失败', status: 0 } };
  }

  return songAuthURL(
    { ...params, auth, open_time, hash, album_audio_id, cookie },
    useAxios,
  );
};
