import * as fs from 'node:fs';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { cloneArgs, cloneEnv, cloneUrl, editorFromEnvironment, repoFromPath } from '../../src/main/clone';
import { evaluateServerInfo } from '../../src/main/compat';
import { nextPollDelayMs, parseNotificationList, pathForNotification, selectToAnnounce } from '../../src/main/notify-logic';
import { Channels } from '../../src/shared/types';

describe('compat', () => {
  const good = { name: 'ByteBench', version: '1.2.3', apiVersion: 1, capabilities: ['notifications'], workspaceBaseDomain: null };
  it('accepts a matching server', () => expect(evaluateServerInfo(200, good).level).toBe('ok'));
  it('flags newer servers', () => expect(evaluateServerInfo(200, { ...good, apiVersion: 9 }).level).toBe('newer'));
  it('treats an older ByteBench (JSON problem 404) as legacy but a random site as not ByteBench', () => {
    expect(evaluateServerInfo(404, { title: 'Not Found', status: 404 }).level).toBe('legacy');
    expect(evaluateServerInfo(404, null).level).toBe('not-bytebench');
    expect(evaluateServerInfo(200, { hello: 'world' }).level).toBe('not-bytebench');
  });
  it('reports outages as unavailable', () => {
    expect(evaluateServerInfo(null, null).level).toBe('unavailable');
    expect(evaluateServerInfo(502, null).level).toBe('unavailable');
  });
});

describe('notifications', () => {
  const item = (id: number, isRead = false) => ({ id, kind: 'Assigned', title: `n${id}`, url: '/p/X/issues/1', isRead, createdAt: '' });
  it('parses defensively', () => {
    expect(parseNotificationList(null)).toBeNull();
    expect(parseNotificationList({ unreadCount: 'x', items: [] })).toBeNull();
    const l = parseNotificationList({ unreadCount: 2, items: [{ id: 1, title: 't', url: '/a' }, { id: 'bad' }, null, { id: 2, title: 'x'.repeat(500), url: '/b', isRead: true }] });
    expect(l?.items.map((i) => i.id)).toEqual([1, 2]);
    expect(l?.items[1]?.title).toHaveLength(200);
  });
  it('announces nothing on the first poll but records where we are', () => {
    expect(selectToAnnounce([item(5), item(3)], undefined)).toEqual({ show: [], more: 0, newest: 5 });
  });
  it('announces only unread items newer than the last one, oldest first, capped', () => {
    const r = selectToAnnounce([item(9), item(8, true), item(7), item(6), item(5), item(4), item(3), item(2)], 3, 3);
    expect(r.show.map((i) => i.id)).toEqual([6, 7, 9]);
    expect(r.more).toBe(2);
    expect(r.newest).toBe(9);
  });
  it('does not move backwards', () => expect(selectToAnnounce([item(2)], 5).newest).toBeUndefined());
  it('only opens server paths from notifications', () => {
    expect(pathForNotification({ url: '/p/X/issues/1' }, 'https://bb.example.com')).toBe('/p/X/issues/1');
    expect(pathForNotification({ url: 'https://evil.example/' }, 'https://bb.example.com')).toBe('/notifications');
    expect(pathForNotification({ url: '//evil.example/' }, 'https://bb.example.com')).toBe('/notifications');
  });
  it('backs off to five minutes', () => {
    expect(nextPollDelayMs(0)).toBe(30_000);
    expect(nextPollDelayMs(2)).toBe(120_000);
    expect(nextPollDelayMs(20)).toBe(300_000);
  });
});

describe('clone helpers', () => {
  it('maps page and git paths to a repository', () => {
    expect(repoFromPath('/p/WEB/repos/api/blob/main/x.cs')).toEqual({ key: 'WEB', slug: 'api' });
    expect(repoFromPath('/git/WEB/api.git/info/refs')).toEqual({ key: 'WEB', slug: 'api' });
    expect(repoFromPath('/p/WEB/issues/1')).toBeNull();
  });
  it('rejects hostile names', () => {
    for (const p of ['/p/-rf/repos/x', '/p/WEB/repos/..', '/p/WEB/repos/-x', '/p/WE%2FB/repos/x', '/p/WEB/repos/a%00b', '/p/WEB/repos/%E0%A4%A']) expect(repoFromPath(p)).toBeNull();
  });
  it('builds an argument array with -- and no credentials', () => {
    const url = cloneUrl('https://bb.example.com', { key: 'WEB', slug: 'api' });
    expect(url).toBe('https://bb.example.com/git/WEB/api.git');
    expect(cloneArgs(url, '/tmp/api')).toEqual(['clone', '--', url, '/tmp/api']);
  });
  it('passes the token through git config environment, not the command line', () => {
    const env = cloneEnv('alice', 'gbp_x');
    expect(env.GIT_CONFIG_VALUE_0).toContain('Basic ');
    expect(Object.values(cloneArgs('u', 't')).join(' ')).not.toContain('gbp_x');
    expect(cloneEnv(undefined, undefined)).toEqual({ GIT_TERMINAL_PROMPT: '0' });
  });
  it('only trusts known graphical editors from the environment', () => {
    expect(editorFromEnvironment({ EDITOR: 'vim' })).toBeUndefined();
    expect(editorFromEnvironment({ EDITOR: 'vim', VISUAL: '/usr/local/bin/code --wait' })).toBe('/usr/local/bin/code');
  });
});

describe('preload channel names', () => {
  it('match the shared constants (the sandboxed preloads cannot import them)', () => {
    const ui = fs.readFileSync(path.join(__dirname, '../../src/preload/ui.ts'), 'utf8');
    const web = fs.readFileSync(path.join(__dirname, '../../src/preload/web.ts'), 'utf8');
    for (const [k, v] of Object.entries(Channels)) {
      if (k === 'cloneRepository' || k === 'webInfo') expect(web).toContain(`'${v}'`);
      else expect(ui).toContain(`'${v}'`);
    }
  });
});
