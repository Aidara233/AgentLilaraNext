/* The entire outbound surface of the console: one catalog read, one action call, Bearer auth.
   Keeping it this small is the point — every view goes through here, so no screen can invent a
   second way to reach the core, and there is exactly one place that knows how errors look. */

export class ApiError extends Error {
  /**
   * @param {number} status HTTP status, or 0 when the request never left the browser.
   * @param {string} code   Server error code (Denied, Invalid, Conflict, …) or a local one.
   * @param {string} message Human readable, already localised by the caller if needed.
   */
  constructor(status, code, message) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

/** Turns what a human types into an absolute base URL. Bare "127.0.0.1:5199" is the common case. */
export function normalizeBase(input) {
  const raw = String(input ?? '').trim();
  if (!raw) throw new ApiError(0, 'Invalid', '接口地址不能为空。');
  const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(raw) ? raw : `http://${raw}`;
  let url;
  try {
    url = new URL(withScheme);
  } catch {
    throw new ApiError(0, 'Invalid', `看不懂的接口地址：${raw}`);
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw new ApiError(0, 'Invalid', `只支持 http/https，收到 ${url.protocol}`);
  }
  return url.origin;
}

export class Api {
  constructor(base, token) {
    this.base = normalizeBase(base);
    this.token = token;
  }

  /** @returns {Promise<{actor:{id:string,kind:string}, actions:Array<object>}>} */
  async catalog() {
    return this.#request('GET', '/actions');
  }

  /**
   * @param {string} id Action id, e.g. "adapters.list".
   * @param {object} [args] Action arguments; omitted means "no arguments", not "empty object sent".
   */
  async invoke(id, args) {
    const sent = await this.#request('POST', `/actions/${encodeURIComponent(id)}`, args ?? {});
    return sent?.data;
  }

  async #request(method, path, body) {
    const headers = { Accept: 'application/json' };
    if (this.token) headers.Authorization = `Bearer ${this.token}`;
    const init = { method, headers };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }

    let response;
    try {
      response = await fetch(`${this.base}${path}`, init);
    } catch (cause) {
      // A browser reports CORS refusal and a dead port identically: say both, so nobody spends an
      // hour on the wrong one.
      throw new ApiError(0, 'Network',
        `连不上 ${this.base}（${cause.message}）。确认核心在跑，并且 api/server.json 的 cors 里列出了本页地址。`);
    }

    const text = await response.text();
    let payload = null;
    if (text) {
      try { payload = JSON.parse(text); } catch { payload = null; }
    }

    if (response.status === 401) {
      throw new ApiError(401, 'Unauthorized', '令牌被拒绝或已失效。');
    }
    if (!response.ok || payload?.ok === false) {
      const code = payload?.code ?? `Http${response.status}`;
      const message = payload?.error ?? payload?.message ?? `HTTP ${response.status}`;
      throw new ApiError(response.status, code, message);
    }
    return payload;
  }
}

/** Short, stable description of a failure for a toast or a panel. */
export function explain(error) {
  if (!(error instanceof ApiError)) return error?.message ?? String(error);
  switch (error.code) {
    case 'Denied': return `当前身份没有这个动作需要的能力（${error.message}）`;
    case 'Invalid': return `参数或动作 id 不被接受：${error.message}`;
    case 'Conflict': return `状态冲突：${error.message}`;
    case 'Unavailable': return `目标当前不可用：${error.message}`;
    case 'Timeout': return `超时：${error.message}`;
    default: return error.message;
  }
}
