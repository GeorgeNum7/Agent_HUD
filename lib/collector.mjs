import fs from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import readline from 'node:readline';
import path from 'node:path';
import os from 'node:os';
import { newSession, consumeCodex, consumeClaude, publicSession, claudeLimits } from './metrics.mjs';

async function discover(root) {
  const found = [];
  async function walk(dir) {
    let entries;
    try { entries = await fs.readdir(dir, { withFileTypes: true }); }
    catch (e) { if (e.code === 'ENOENT') return; throw e; }
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) await walk(full);
      else if (entry.isFile() && entry.name.endsWith('.jsonl')) found.push(full);
    }
  }
  await walk(root);
  return found;
}

export class Collector {
  constructor({ codexHome = process.env.CODEX_HOME || path.join(os.homedir(), '.codex'), claudeHome = process.env.CLAUDE_CONFIG_DIR || path.join(os.homedir(), '.claude'), dataDir }) {
    this.codexHome = codexHome;
    this.claudeHome = claudeHome;
    this.dataDir = dataDir;
    this.cache = new Map();
    this.names = new Map();
    this.value = { sessions: [], providers: [], collectedAt: null, loading: true };
  }

  async readFile(file, provider) {
    const stat = await fs.stat(file);
    let item = this.cache.get(file);
    if (item && item.size === stat.size && item.mtime === stat.mtimeMs) return item.session;
    if (!item || stat.size < item.size || (stat.size === item.size && stat.mtimeMs !== item.mtime)) {
      const session = newSession(provider, file);
      if (provider === 'claude' && (file.includes(`${path.sep}subagents${path.sep}`) || path.basename(file).startsWith('agent-'))) {
        session.id = path.basename(file, '.jsonl');
        session.kind = 'subagent';
        session.parentId = file.includes(`${path.sep}subagents${path.sep}`) ? path.basename(path.dirname(path.dirname(file))) : null;
      }
      item = { size: 0, mtime: 0, pending: '', session };
      this.cache.set(file, item);
    }
    if (stat.size > item.size) {
      const stream = createReadStream(file, { start: item.size, end: stat.size - 1, encoding: 'utf8' });
      let buffer = item.pending;
      for await (const chunk of stream) {
        buffer += chunk;
        let start = 0, end;
        while ((end = buffer.indexOf('\n', start)) !== -1) {
          const line = buffer.slice(start, end);
          start = end + 1;
          // Do not parse or retain message contents, tool outputs, instructions, or credentials.
          const relevant = provider === 'codex'
            ? /"type"\s*:\s*"(?:session_meta|turn_context|token_usage_record|event_msg)"/.test(line)
            : /"type"\s*:\s*"(?:assistant|user|custom-title|agent-name)"/.test(line);
          if (!relevant) continue;
          try {
            const row = JSON.parse(line);
            (provider === 'codex' ? consumeCodex : consumeClaude)(item.session, row);
          } catch { item.session.warnings++; }
        }
        buffer = buffer.slice(start);
      }
      item.pending = buffer;
    }
    item.size = stat.size;
    item.mtime = stat.mtimeMs;
    return item.session;
  }

  async refresh() {
    if (this.inFlight) return this.inFlight;
    this.inFlight = this.collect().finally(() => { this.inFlight = null; });
    return this.inFlight;
  }

  async collect() {
    try {
      const rl = readline.createInterface({ input: createReadStream(path.join(this.codexHome, 'session_index.jsonl')), crlfDelay: Infinity });
      for await (const line of rl) { try { const x = JSON.parse(line); if (x.id && x.thread_name) this.names.set(x.id, x.thread_name); } catch {} }
    } catch {}
    const providers = [];
    const sessions = [];
    const visibleFiles = new Set();
    for (const [id, home, folder] of [['codex', this.codexHome, 'sessions'], ['claude', this.claudeHome, 'projects']]) {
      let error = null, files = [];
      try { files = await discover(path.join(home, folder)); } catch { error = '无法读取本机会话目录'; }
      let latestLimits = null;
      let lastEntrypoint = null, lastEntrypointAt = 0, bridgeReceivedAt = null;
      for (const file of files) {
        visibleFiles.add(file);
        try {
          const session = await this.readFile(file, id);
          if (!session.id) continue;
          if (session.entrypoint && session.updatedAt >= lastEntrypointAt) { lastEntrypoint = session.entrypoint; lastEntrypointAt = session.updatedAt; }
          if (session.latestLimits && (!latestLimits || session.latestLimits.collectedAt > latestLimits.collectedAt)) latestLimits = session.latestLimits;
          sessions.push(publicSession(session, this.names));
        } catch { error = '部分记录暂时无法读取，将在下次刷新重试'; }
      }
      if (id === 'claude') {
        try {
          const snapshots = (await fs.readdir(this.dataDir)).filter(f => f.startsWith('claude-status-') && f.endsWith('.json'));
          for (const file of snapshots) {
            try {
              const raw = JSON.parse(await fs.readFile(path.join(this.dataDir, file), 'utf8'));
              if (typeof raw.collectedAt === 'number' && raw.collectedAt > (bridgeReceivedAt || 0)) bridgeReceivedAt = raw.collectedAt;
              const candidate = claudeLimits(raw.rate_limits, raw.collectedAt);
              if (candidate && (!latestLimits || candidate.collectedAt > latestLimits.collectedAt)) latestLimits = candidate;
              const session = sessions.find(s => s.provider === 'claude' && s.id === raw.session_id);
              if (session && raw.context && raw.collectedAt >= session.updatedAt - 5000) session.context = raw.context;
            } catch {}
          }
        } catch {}
      }
      let bridgeConnected = false;
      if (id === 'claude') {
        try { const settings = JSON.parse(await fs.readFile(path.join(home, 'settings.json'), 'utf8')); bridgeConnected = /[\\/]scripts[\\/]claude-statusline\.mjs/.test(settings.statusLine?.command || ''); } catch {}
      }
      const desktopWithoutBridge = id === 'claude' && lastEntrypoint === 'claude-desktop' && !bridgeReceivedAt;
      const quotaStatus = id !== 'claude' ? null : latestLimits?.windows.length ? 'available' : desktopWithoutBridge ? 'desktop-not-connected' : bridgeReceivedAt ? 'fields-unavailable' : bridgeConnected ? 'waiting-for-cli' : 'not-connected';
      const quotaMessage = id !== 'claude' || latestLimits?.windows.length ? null : desktopWithoutBridge
        ? '检测到 Claude 桌面版会话，未收到终端状态栏数据；当前桥接不能自动显示桌面版额度。'
        : bridgeReceivedAt ? '状态栏已运行，但 Claude 未返回额度字段；字段取决于账户类型和版本。'
        : bridgeConnected ? '已安装终端版状态栏桥接，等待 Claude Code CLI 返回数据。'
        : '尚未接入 Claude Code CLI 状态栏。';
      providers.push({ id, name: id === 'codex' ? 'Codex' : 'Claude Code', connected: files.length > 0, fileCount: files.length, error, limits: latestLimits, bridgeConnected, quotaStatus, quotaMessage, bridgeReceivedAt });
    }
    for (const file of this.cache.keys()) if (!visibleFiles.has(file)) this.cache.delete(file);
    // Forks and resumed transcripts can contain the same upstream response. Count it once.
    const seen = new Set();
    sessions.sort((a,b) => a.startedAt - b.startedAt || (a.kind === 'session' ? -1 : 1));
    for (const session of sessions) session.records = session.records.filter(record => {
      const key = `${session.provider}:${record.id}`;
      if (seen.has(key)) return false;
      seen.add(key); return true;
    });
    this.value = { sessions: sessions.sort((a,b) => b.updatedAt - a.updatedAt), providers, collectedAt: Date.now(), loading: false };
    return this.value;
  }
}
