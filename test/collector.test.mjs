import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { Collector } from '../lib/collector.mjs';

test('Collector resumes partial JSONL lines, reads only appended data, and resets truncated files', async t => {
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agent-hud-test-'));
  t.after(()=>fs.rm(root,{recursive:true,force:true}));
  const file=path.join(root,'session.jsonl');
  const collector=new Collector({codexHome:root,claudeHome:root,dataDir:root});
  const meta={type:'session_meta',payload:{id:'test'}};
  const usage={type:'token_usage_record',timestamp:'2026-10-14T10:00:00Z',payload:{thread_id:'test',response_id:'r',usage:{input_tokens:42,output_tokens:8}}};
  const serialized=JSON.stringify(usage);
  await fs.writeFile(file,JSON.stringify(meta)+'\n'+serialized.slice(0,40));
  let session=await collector.readFile(file,'codex');assert.equal(session.records.size,0);
  await fs.appendFile(file,serialized.slice(40)+'\n');
  session=await collector.readFile(file,'codex');assert.equal(session.records.size,1);assert.equal(session.records.get('r').total,50);
  await collector.readFile(file,'codex');assert.equal(session.records.size,1);
  await fs.writeFile(file,JSON.stringify(meta)+'\n');session=await collector.readFile(file,'codex');assert.equal(session.records.size,0);
});

test('Cross-file copied Claude responses count only once and content is excluded from snapshots', async t => {
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agent-hud-test-'));
  t.after(()=>fs.rm(root,{recursive:true,force:true}));
  await fs.mkdir(path.join(root,'projects'));
  for(const id of ['one','two'])await fs.writeFile(path.join(root,'projects',id+'.jsonl'),JSON.stringify({type:'assistant',sessionId:id,timestamp:'2026-10-14T10:00:00Z',message:{id:'same',model:'claude',content:'SECRET_PROMPT_SENTINEL',usage:{input_tokens:4,output_tokens:6}}})+'\n');
  const collector=new Collector({codexHome:root,claudeHome:root,dataDir:root});
  const snapshot=await collector.refresh();
  assert.equal(snapshot.sessions.flatMap(s=>s.records).length,1);
  assert.ok(!JSON.stringify(snapshot).includes('SECRET_PROMPT_SENTINEL'));
  assert.ok(!JSON.stringify(snapshot).includes(root.replaceAll('\\','\\\\')));
});

test('Claude distinguishes desktop sessions, CLI waiting, absent quota fields, and real zero usage', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'agent-hud-quota-test-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  await fs.mkdir(path.join(root, 'projects'));
  await fs.writeFile(path.join(root, 'settings.json'), JSON.stringify({ statusLine: { command: 'node /hud/scripts/claude-statusline.mjs' } }));
  const collector = new Collector({ codexHome: root, claudeHome: root, dataDir: root });
  const claude = async () => (await collector.refresh()).providers.find(p => p.id === 'claude');
  let provider = await claude();
  assert.equal(provider.quotaStatus, 'waiting-for-cli');
  assert.equal(provider.limits, null);

  await fs.writeFile(path.join(root, 'projects', 'desktop.jsonl'), JSON.stringify({ type: 'assistant', entrypoint: 'claude-desktop', sessionId: 'desktop', timestamp: '2026-10-15T06:40:26Z', message: { id: 'response', usage: { input_tokens: 10, output_tokens: 2 } } }) + '\n');
  provider = await claude();
  assert.equal(provider.quotaStatus, 'desktop-not-connected');
  assert.match(provider.quotaMessage, /桌面版/);
  assert.equal(provider.bridgeConnected, true);
  assert.equal(provider.limits, null);

  const snapshot = path.join(root, 'claude-status-cli.json');
  await fs.writeFile(snapshot, JSON.stringify({ session_id: 'cli', collectedAt: Date.now(), rate_limits: {} }));
  provider = await claude();
  assert.equal(provider.quotaStatus, 'fields-unavailable');
  assert.deepEqual(provider.limits.windows, []);

  await fs.writeFile(snapshot, JSON.stringify({ session_id: 'cli', collectedAt: Date.now(), rate_limits: { five_hour: { used_percentage: 0, resets_at: Math.floor(Date.now() / 1000) + 3600 } } }));
  provider = await claude();
  assert.equal(provider.quotaStatus, 'available');
  assert.equal(provider.limits.windows[0].remainingPercent, 100);
  assert.equal(provider.quotaMessage, null);
});
