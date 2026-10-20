import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root=path.dirname(path.dirname(fileURLToPath(import.meta.url)));
async function temp(t) {
  const dir=await fs.mkdtemp(path.join(os.tmpdir(),'agent-hud-bridge-test-'));
  t.after(async()=>{assert.ok(path.resolve(dir).startsWith(path.resolve(os.tmpdir())+path.sep));await fs.rm(dir,{recursive:true,force:true});});
  return dir;
}
test('Claude bridge writes only allowlisted metrics and handles absent windows',async t=>{
  const dir=await temp(t);
  const result=spawnSync(process.execPath,[path.join(root,'scripts','claude-statusline.mjs')],{encoding:'utf8',windowsHide:true,env:{...process.env,AGENT_HUD_DATA_DIR:dir},input:JSON.stringify({session_id:'test',rate_limits:{five_hour:{used_percentage:25,resets_at:1792015883}},context_window:{used_percentage:50,context_window_size:200000,total_input_tokens:100000},secret:'DO_NOT_SAVE',transcript_path:'DO_NOT_SAVE'})});
  assert.equal(result.status,0);assert.match(result.stdout,/5h left 75%/);
  const stored=await fs.readFile(path.join(dir,'claude-status-test.json'),'utf8');
  assert.ok(!stored.includes('DO_NOT_SAVE'));assert.equal(JSON.parse(stored).rate_limits.seven_day,undefined);
  assert.equal(JSON.parse(stored).context.percent,50);
});
test('Claude installation and removal preserve other settings and create backups',async t=>{
  const dir=await temp(t);const settings=path.join(dir,'settings.json');
  await fs.writeFile(settings,JSON.stringify({theme:'dark',permissions:{allow:['Read']}}));
  const script=path.join(root,'scripts','connect-claude.mjs');
  const options={encoding:'utf8',windowsHide:true,env:{...process.env,CLAUDE_CONFIG_DIR:dir}};
  assert.equal(spawnSync(process.execPath,[script],options).status,0);
  let saved=JSON.parse(await fs.readFile(settings,'utf8'));assert.equal(saved.theme,'dark');assert.match(saved.statusLine.command,/claude-statusline/);
  assert.ok((await fs.readdir(dir)).some(f=>f.endsWith('.bak')));
  saved.newSetting=true;await fs.writeFile(settings,JSON.stringify(saved));
  assert.equal(spawnSync(process.execPath,[script,'--remove'],options).status,0);
  saved=JSON.parse(await fs.readFile(settings,'utf8'));assert.equal(saved.statusLine,undefined);assert.equal(saved.newSetting,true);assert.deepEqual(saved.permissions,{allow:['Read']});
});
test('An existing custom Claude status line is never overwritten',async t=>{
  const dir=await temp(t);const original=JSON.stringify({statusLine:{type:'command',command:'my-status-line'}});
  await fs.writeFile(path.join(dir,'settings.json'),original);
  const result=spawnSync(process.execPath,[path.join(root,'scripts','connect-claude.mjs')],{encoding:'utf8',windowsHide:true,env:{...process.env,CLAUDE_CONFIG_DIR:dir}});
  assert.equal(result.status,1);assert.equal(await fs.readFile(path.join(dir,'settings.json'),'utf8'),original);
});
