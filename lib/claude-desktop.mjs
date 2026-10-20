import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { spawn } from 'node:child_process';
import { createDecipheriv } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const helper = fileURLToPath(new URL('../scripts/unprotect-claude-key.ps1', import.meta.url));
const API_URL = 'https://api.anthropic.com/api/oauth/usage';
const POLL_MS = 5 * 60 * 1000;

export class ClaudeConnectionError extends Error {
  constructor(code) { super(code); this.code = code; }
}

export async function desktopDirectories({ local = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), roaming = process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming') } = {}) {
  const candidates = [];
  // Newer Claude builds move settings and sign-in to %LOCALAPPDATA%\Claude-Data\claude_<id> and leave
  // the old folders without config.json. Checked first, so a stale copy in an old folder never wins.
  try {
    const moved = await fs.readdir(path.join(local, 'Claude-Data'), { withFileTypes: true });
    for (const entry of moved) if (entry.isDirectory() && /^claude(_[a-z0-9]+)?$/i.test(entry.name)) candidates.push(path.join(local, 'Claude-Data', entry.name));
  } catch {}
  candidates.push(path.join(roaming, 'Claude'));
  try {
    const packages = await fs.readdir(path.join(local, 'Packages'), { withFileTypes: true });
    for (const entry of packages) if (entry.isDirectory() && /^Claude_[a-z0-9]+$/i.test(entry.name)) {
      candidates.push(path.join(local, 'Packages', entry.name, 'LocalCache', 'Roaming', 'Claude'));
    }
  } catch {}
  return candidates;
}

function unprotectKey(encrypted) {
  return new Promise((resolve, reject) => {
    const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
    const child = spawn(powershell, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', helper], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    let output = '', finished = false;
    const timer = setTimeout(() => { child.kill(); finish(new ClaudeConnectionError('locked')); }, 10000);
    function finish(error, value) { if (finished) return; finished = true; clearTimeout(timer); error ? reject(error) : resolve(value); }
    child.stdout.on('data', chunk => { output += chunk; if (output.length > 1024) { child.kill(); finish(new ClaudeConnectionError('locked')); } });
    child.stderr.on('data', () => {});
    child.stdin.on('error', () => {});
    child.on('error', () => finish(new ClaudeConnectionError('locked')));
    child.on('close', code => {
      const key = Buffer.from(output.trim(), 'base64'); output = '';
      if (code !== 0 || key.length !== 32) { key.fill(0); finish(new ClaudeConnectionError('locked')); }
      else finish(null, key);
    });
    child.stdin.end(encrypted.toString('base64'));
  });
}

export function decryptCache(encoded, key) {
  const data = Buffer.from(encoded, 'base64');
  if (data.length < 32 || data.subarray(0, 3).toString() !== 'v10') throw new ClaudeConnectionError('unsupported');
  const decipher = createDecipheriv('aes-256-gcm', key, data.subarray(3, 15));
  decipher.setAuthTag(data.subarray(-16));
  const plain = Buffer.concat([decipher.update(data.subarray(15, -16)), decipher.final()]);
  try { return JSON.parse(plain.toString('utf8')); } finally { plain.fill(0); }
}

export function selectCredential(config, caches, now = Date.now()) {
  const account = config.lastKnownAccountUuid;
  if (typeof account !== 'string' || !account) throw new ClaudeConnectionError('signed-out');
  const entries = new Map();
  // V2 is authoritative, including tombstones. Do not resurrect a revoked V1 entry.
  for (const cache of caches) for (const [key, value] of Object.entries(cache || {})) entries.set(key, value);
  const candidates = [];
  for (const [key, value] of entries) {
    const match = /^acct:([^|]+)\|([^:]+):([^:]+):(https:\/\/api\.anthropic\.com):(.+)$/.exec(key);
    if (!match || match[1].toLowerCase() !== account.toLowerCase() || !match[5].split(/\s+/).includes('user:profile')) continue;
    if (!value || typeof value.token !== 'string' || !value.token || !Number.isFinite(value.expiresAt)) continue;
    candidates.push({ token: value.token, expiresAt: value.expiresAt, org: match[3], account, scope: match[5] });
  }
  const fresh = candidates.filter(c => c.expiresAt > now + 30000);
  if (!fresh.length) throw new ClaudeConnectionError(candidates.length ? 'expired' : 'signed-out');
  if (new Set(fresh.map(c => c.org)).size > 1) throw new ClaudeConnectionError('ambiguous-account');
  // Prefer the Code token used by the desktop's own usage panel.
  fresh.sort((a, b) => Number(b.scope.includes('user:inference')) - Number(a.scope.includes('user:inference')) || b.expiresAt - a.expiresAt);
  return fresh[0];
}

export async function readDesktopCredential() {
  if (process.platform !== 'win32') throw new ClaudeConnectionError('unsupported');
  let failure = new ClaudeConnectionError('not-found');
  for (const directory of await desktopDirectories()) {
    let key;
    try {
      const config = JSON.parse(await fs.readFile(path.join(directory, 'config.json'), 'utf8'));
      if (!config['oauth:tokenCache'] && !config['oauth:tokenCacheV2']) continue;
      const state = JSON.parse(await fs.readFile(path.join(directory, 'Local State'), 'utf8'));
      const encrypted = Buffer.from(state.os_crypt?.encrypted_key || '', 'base64');
      if (encrypted.subarray(0, 5).toString() !== 'DPAPI') throw new ClaudeConnectionError('unsupported');
      key = await unprotectKey(encrypted.subarray(5));
      const caches = ['oauth:tokenCache', 'oauth:tokenCacheV2'].map(name => config[name] ? decryptCache(config[name], key) : {});
      return selectCredential(config, caches);
    } catch (error) {
      if (error.code !== 'ENOENT') failure = error instanceof ClaudeConnectionError ? error : new ClaudeConnectionError('locked');
    } finally { key?.fill(0); }
  }
  throw failure;
}

function resetTime(value) {
  const result = typeof value === 'number' ? value * 1000 : typeof value === 'string' ? Date.parse(value) : NaN;
  return Number.isFinite(result) && result > 0 ? result : null;
}

export function parseDesktopUsage(raw, collectedAt) {
  const windows = [];
  function add(name, percent, minutes, resetsAt, id) {
    if (typeof percent !== 'number' || !Number.isFinite(percent)) return;
    windows.push({ name, usedPercent: Math.max(0, Math.min(100, percent)), remainingPercent: Math.max(0, Math.min(100, 100 - percent)), windowMinutes: minutes, resetsAt: resetTime(resetsAt), id });
  }
  const named = [['five_hour', '5 小时', 300], ['seven_day', '7 天', 10080], ['seven_day_opus', 'Opus · 7 天', 10080], ['seven_day_sonnet', 'Sonnet · 7 天', 10080]];
  for (const [key, name, minutes] of named) add(name, raw?.[key]?.utilization, minutes, raw?.[key]?.resets_at, key);
  // Newer desktop responses can carry meters in `limits` instead of the legacy keys.
  if (Array.isArray(raw?.limits)) for (const meter of raw.limits) {
    const key = meter.kind === 'session' ? 'five_hour' : meter.kind === 'weekly_all' ? 'seven_day' : null;
    if (key && !windows.some(w => w.id === key)) add(key === 'five_hour' ? '5 小时' : '7 天', meter.percent, key === 'five_hour' ? 300 : 10080, meter.resets_at, key);
  }
  windows.sort((a, b) => (a.id === 'five_hour' ? 0 : a.id === 'seven_day' ? 1 : 2) - (b.id === 'five_hour' ? 0 : b.id === 'seven_day' ? 1 : 2));
  return { windows, plan: null, collectedAt, source: 'Claude 桌面账户额度', staleAfterMs: POLL_MS + 60000 };
}

const MESSAGES = {
  'not-found': '未找到 Claude 桌面版登录连接，请先打开 Claude 并登录。',
  'signed-out': 'Claude 桌面版尚未提供账户连接，请打开 Code 标签页并确认已登录。',
  expired: 'Claude 桌面版登录连接已过期，请打开 Claude，让它自动续期。',
  locked: '暂时无法读取 Claude 的加密账户连接，将稍后重试。',
  unsupported: '当前 Claude 版本的账户存储格式尚未支持。',
  'ambiguous-account': '发现多个 Claude 组织的有效连接，无法确定当前额度所属组织。',
  unauthorized: 'Claude 账户连接已失效，请在 Claude 桌面版中重新登录。',
  forbidden: 'Claude 额度接口拒绝了此账户连接。',
  'rate-limited': 'Claude 额度查询被限流，已降低查询频率，稍后自动重试。',
  unavailable: 'Claude 额度服务暂不可用，稍后自动重试。',
  'fields-unavailable': 'Claude 返回了响应，但没有提供额度百分比。',
};

export class ClaudeDesktopQuota {
  constructor({ credential = readDesktopCredential, fetchImpl = fetch, now = Date.now } = {}) {
    this.credential = credential; this.fetch = fetchImpl; this.now = now;
    this.snapshot = null; this.error = null; this.status = 'connecting'; this.nextAttemptAt = 0; this.failures = 0;
    this.identity = null; this.inFlight = null; this.closed = false;
  }
  async refresh() {
    if (this.closed || this.inFlight || this.now() < this.nextAttemptAt) return;
    this.inFlight = this.read().finally(() => { this.inFlight = null; });
    return this.inFlight;
  }
  async read() {
    this.nextAttemptAt = this.now() + POLL_MS;
    try {
      const credential = await this.credential();
      const identity = `${credential.account}:${credential.org}`;
      if (identity !== this.identity) this.snapshot = null;
      this.identity = identity;
      this.controller = new AbortController();
      const timer = setTimeout(() => this.controller.abort(), 15000);
      let response, raw;
      try {
        response = await this.fetch(API_URL, { method: 'GET', redirect: 'error', signal: this.controller.signal, headers: { Authorization: `Bearer ${credential.token}`, 'anthropic-beta': 'oauth-2025-04-20', Accept: 'application/json' } });
        if (response.status === 429) {
          const retry = response.headers.get('retry-after');
          const delay = /^\d+$/.test(retry || '') ? Number(retry) * 1000 : Date.parse(retry) - this.now();
          this.nextAttemptAt = this.now() + Math.max(POLL_MS * 3, Number.isFinite(delay) ? delay : 0);
          throw new ClaudeConnectionError('rate-limited');
        }
        if (!response.ok) throw new ClaudeConnectionError(response.status === 401 ? 'unauthorized' : response.status === 403 ? 'forbidden' : 'unavailable');
        raw = await response.json();
      } finally { clearTimeout(timer); this.controller = null; }
      const snapshot = parseDesktopUsage(raw, this.now());
      if (!snapshot.windows.length) throw new ClaudeConnectionError('fields-unavailable');
      this.snapshot = snapshot; this.error = null; this.status = 'available'; this.failures = 0;
    } catch (error) {
      const code = error instanceof ClaudeConnectionError ? error.code : 'unavailable';
      this.status = code; this.error = MESSAGES[code] || MESSAGES.unavailable; this.failures++;
      if (['signed-out', 'expired', 'unauthorized', 'forbidden', 'ambiguous-account', 'not-found'].includes(code)) { this.snapshot = null; this.identity = null; }
      else if (this.snapshot) this.snapshot = { ...this.snapshot, stale: true };
      // Failures retain only quota counters. Raw responses and credentials never enter snapshots/logs.
      this.nextAttemptAt = Math.max(this.nextAttemptAt, this.now() + Math.min(30 * 60 * 1000, POLL_MS * 2 ** Math.min(this.failures - 1, 3)));
    }
  }
  close() { this.closed = true; this.controller?.abort(); }
}
