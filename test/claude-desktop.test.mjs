import test from 'node:test';
import assert from 'node:assert/strict';
import { createCipheriv, randomBytes } from 'node:crypto';
import { ClaudeConnectionError, ClaudeDesktopQuota, decryptCache, parseDesktopUsage, selectCredential } from '../lib/claude-desktop.mjs';

// Entirely synthetic credentials. These tests never read real accounts or use the network.
const time = Date.parse('2026-10-15T07:00:00Z');
const cacheKey = (account = 'account-a', org = 'org-a', host = 'https://api.anthropic.com') => `acct:${account}|client-id:${org}:${host}:user:profile user:inference`;
const entry = { token: 'SYNTHETIC_TOKEN', refreshToken: 'SYNTHETIC_REFRESH', expiresAt: time + 3600000 };
const config = { lastKnownAccountUuid: 'account-a' };
const credential = { token: entry.token, expiresAt: entry.expiresAt, account: 'account-a', org: 'org-a' };
const data = { five_hour: { utilization: 25, resets_at: '2026-10-15T10:00:00Z' }, seven_day: { utilization: 40, resets_at: '2026-10-19T10:00:00Z' } };
const ok = raw => new Response(JSON.stringify(raw), { status: 200, headers: { 'content-type': 'application/json' } });

test('Desktop encrypted cache validates AES-GCM and rejects unknown formats', () => {
  const key = randomBytes(32), iv = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', key, iv);
  const raw = { [cacheKey()]: entry };
  const encrypted = Buffer.concat([Buffer.from('v10'), iv, cipher.update(JSON.stringify(raw)), cipher.final(), cipher.getAuthTag()]);
  assert.deepEqual(decryptCache(encrypted.toString('base64'), key), raw);
  encrypted[20] ^= 1;
  assert.throws(() => decryptCache(encrypted.toString('base64'), key));
  encrypted.write('v20');
  assert.throws(() => decryptCache(encrypted.toString('base64'), key), { code: 'unsupported' });
});

test('Desktop selects only the current account and official host, honors revocation and expiry', () => {
  const cache = { [cacheKey()]: entry, [cacheKey('other-user')]: { ...entry, token: 'OTHER_USER' }, [cacheKey('account-a', 'org-b', 'https://example.test')]: { ...entry, token: 'WRONG_HOST' } };
  assert.equal(selectCredential(config, [cache], time).token, entry.token);
  assert.throws(() => selectCredential(config, [cache, { [cacheKey()]: null }], time), { code: 'signed-out' });
  assert.throws(() => selectCredential(config, [cache], entry.expiresAt), { code: 'expired' });
  assert.throws(() => selectCredential({}, [cache], time), { code: 'signed-out' });
  assert.throws(() => selectCredential(config, [{ ...cache, [cacheKey('account-a', 'org-b')]: entry }], time), { code: 'ambiguous-account' });
});

test('Desktop quota parser distinguishes unknown, zero and full usage and parses reset timestamps', () => {
  assert.deepEqual(parseDesktopUsage({}, time).windows, []);
  assert.deepEqual(parseDesktopUsage({ five_hour: { utilization: null } }, time).windows, []);
  const snapshot = parseDesktopUsage({ ...data, five_hour: { utilization: 0, resets_at: null }, seven_day_opus: { utilization: 100 } }, time);
  assert.equal(snapshot.windows[0].remainingPercent, 100);
  assert.equal(snapshot.windows[0].resetsAt, null);
  assert.equal(snapshot.windows[1].resetsAt, Date.parse(data.seven_day.resets_at));
  assert.equal(snapshot.windows[2].remainingPercent, 0);
  assert.equal(snapshot.staleAfterMs, 360000);
  const meters = parseDesktopUsage({ limits: [{ kind: 'session', percent: 30, resets_at: data.five_hour.resets_at }, { kind: 'weekly_all', percent: 55 }] }, time);
  assert.deepEqual(meters.windows.map(w => w.remainingPercent), [70, 45]);
});

test('Desktop polling uses one GET to the fixed official endpoint and publishes no credentials', async () => {
  let now = time, reads = 0, requests = 0;
  const quota = new ClaudeDesktopQuota({ now: () => now, credential: async () => { reads++; return credential; }, fetchImpl: async (url, options) => {
    requests++;
    assert.equal(url, 'https://api.anthropic.com/api/oauth/usage');
    assert.equal(options.method, 'GET'); assert.equal(options.redirect, 'error');
    assert.equal(options.headers.Authorization, `Bearer ${entry.token}`);
    assert.equal(options.body, undefined);
    return ok(data);
  } });
  assert.equal(reads, 0); // Construction/import cannot access the account.
  await quota.refresh();
  assert.equal(quota.status, 'available'); assert.equal(quota.snapshot.windows[0].remainingPercent, 75);
  assert.ok(!JSON.stringify(quota.snapshot).includes('SYNTHETIC'));
  await quota.refresh(); assert.equal(requests, 1);
  now += 300000; await quota.refresh(); assert.equal(requests, 2); assert.equal(reads, 2);
  quota.close(); now += 300000; await quota.refresh(); assert.equal(requests, 2);
});

test('Desktop rate limits back off and mark old quota stale; unauthorized clears it', async () => {
  let now = time, response = ok(data), calls = 0;
  const quota = new ClaudeDesktopQuota({ now: () => now, credential: async () => credential, fetchImpl: async () => { calls++; return response; } });
  await quota.refresh(); now += 300000;
  response = new Response('not logged', { status: 429, headers: { 'retry-after': '0' } });
  await quota.refresh();
  assert.equal(quota.status, 'rate-limited'); assert.equal(quota.snapshot.stale, true);
  assert.ok(quota.nextAttemptAt >= now + 900000);
  now += 300000; await quota.refresh(); assert.equal(calls, 2);
  now = quota.nextAttemptAt; response = new Response('SECRET_BODY', { status: 401 }); await quota.refresh();
  assert.equal(quota.status, 'unauthorized'); assert.equal(quota.snapshot, null);
  assert.ok(!quota.error.includes('SECRET_BODY'));
});

test('Desktop account changes discard previous quota even when the new request fails', async () => {
  let now = time, selected = credential, response = ok(data);
  const quota = new ClaudeDesktopQuota({ now: () => now, credential: async () => selected, fetchImpl: async () => response });
  await quota.refresh(); assert.ok(quota.snapshot);
  selected = { ...credential, account: 'account-b' }; response = new Response('', { status: 503 }); now += 300000;
  await quota.refresh(); assert.equal(quota.snapshot, null);
  assert.equal(quota.status, 'unavailable');
});

test('Desktop expired sign-in does not attempt refresh or contact any endpoint', async () => {
  let calls = 0;
  const quota = new ClaudeDesktopQuota({ now: () => time, credential: async () => { throw new ClaudeConnectionError('expired'); }, fetchImpl: async () => { calls++; } });
  await quota.refresh();
  assert.equal(calls, 0); assert.equal(quota.status, 'expired'); assert.equal(quota.snapshot, null);
});
