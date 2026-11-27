import { describe, expect, it } from 'vitest';
import { classifyUrl, isPortEntryUrl, parseWorkspaceOrigin } from '../../src/main/allowlist';
import { displayAddress, menuEntries, parseOpenResponse, parsePorts, parseWorkspaceList, serverSupportsPorts } from '../../src/main/workspace-ports';

const ID = 'abcdefghijklmnopqrst'.replace(/[^a-z2-7]/g, 'a'); // 20 characters of [a-z2-7]
const OTHER = 'bbbbbbbbbbbbbbbbbbbb';
const ctx = { serverOrigin: 'https://bb.example.com' };
const entry = (label: string, base = 'bb.example.com', q = 'ticket=T1&next=%2F') => `https://${label}.${base}/_bb/enter?${q}`;

describe('parseWorkspaceOrigin', () => {
  it('reads the workspace and port out of the label ByteBench mints', () => {
    expect(parseWorkspaceOrigin(`https://p5173-${ID}.bb.example.com/`, ctx)).toEqual({ kind: 'preview', port: 5173, workspaceId: ID });
    expect(parseWorkspaceOrigin(`https://ide-${ID}.bb.example.com/x`, ctx)).toEqual({ kind: 'ide', workspaceId: ID });
  });
  it('rejects look-alikes: odd ids, ports out of range, other servers and schemes', () => {
    for (const u of [
      'https://ide-abc.bb.example.com/', // not a real id
      `https://p0-${ID}.bb.example.com/`,
      `https://p99999-${ID}.bb.example.com/`,
      `https://p5173-${ID}.evil.example/`,
      `https://x.p5173-${ID}.bb.example.com/`,
      `http://p5173-${ID}.bb.example.com/`,
      `https://p5173-${ID}.bb.example.com:8443/`,
      'https://bb.example.com/',
      'javascript:alert(1)',
    ])
      expect(parseWorkspaceOrigin(u, ctx), u).toBeNull();
  });
  it('works for a localhost development server with its port', () => {
    const l = { serverOrigin: 'http://localhost:5108' };
    expect(parseWorkspaceOrigin(`http://p3000-${ID}.localhost:5108/`, l)?.port).toBe(3000);
    expect(parseWorkspaceOrigin(`http://p3000-${ID}.localhost:5109/`, l)).toBeNull();
  });
});

describe('isPortEntryUrl', () => {
  it('accepts exactly the single-use entry link for that workspace and port', () => {
    expect(isPortEntryUrl(entry(`p5173-${ID}`), ctx, ID, 5173)).toBe(true);
  });
  it('rejects another workspace, another port, the IDE, another path and extra parameters', () => {
    expect(isPortEntryUrl(entry(`p5173-${OTHER}`), ctx, ID, 5173)).toBe(false);
    expect(isPortEntryUrl(entry(`p5174-${ID}`), ctx, ID, 5173)).toBe(false);
    expect(isPortEntryUrl(entry(`ide-${ID}`), ctx, ID, 5173)).toBe(false);
    expect(isPortEntryUrl(`https://p5173-${ID}.bb.example.com/other?ticket=T&next=%2F`, ctx, ID, 5173)).toBe(false);
    expect(isPortEntryUrl(entry(`p5173-${ID}`, 'bb.example.com', 'ticket=T&next=%2F&x=1'), ctx, ID, 5173)).toBe(false);
    expect(isPortEntryUrl(entry(`p5173-${ID}`, 'bb.example.com', 'ticket=T'), ctx, ID, 5173)).toBe(false);
    expect(isPortEntryUrl(entry(`p5173-${ID}`, 'bb.example.com', 'ticket=&next=%2F'), ctx, ID, 5173)).toBe(false);
  });
  it('rejects a next target that leaves the origin', () => {
    for (const next of ['https%3A%2F%2Fevil.example%2F', '%2F%2Fevil.example', '%5C%5Cevil', 'foo']) {
      expect(isPortEntryUrl(entry(`p5173-${ID}`, 'bb.example.com', `ticket=T&next=${next}`), ctx, ID, 5173), next).toBe(false);
    }
  });
  it('rejects a fragment, credentials and other origins', () => {
    expect(isPortEntryUrl(entry(`p5173-${ID}`) + '#x', ctx, ID, 5173)).toBe(false);
    expect(isPortEntryUrl(`https://u:p@p5173-${ID}.bb.example.com/_bb/enter?ticket=T&next=%2F`, ctx, ID, 5173)).toBe(false);
    expect(isPortEntryUrl(entry(`p5173-${ID}`, 'evil.example'), ctx, ID, 5173)).toBe(false);
  });
  it('is still an ordinary workspace URL for the navigation policy (keeps working in app windows)', () => {
    expect(classifyUrl(entry(`p5173-${ID}`), ctx)).toBe('workspace');
    expect(classifyUrl(entry(`p5173-${ID}`, 'evil.example'), ctx)).toBe('external');
  });
});

describe('parsing what the server sends', () => {
  it('keeps running workspaces with valid ids only, and cleans the text', () => {
    const list = parseWorkspaceList([
      { id: ID, project: 'WEB', repository: 'site', ref: 'main\nINJECT', state: 'Running' },
      { id: OTHER, project: 'WEB', repository: 'x', ref: 'm', state: 'Stopped' },
      { id: 'short', project: 'WEB', repository: 'x', ref: 'm', state: 'Running' },
      { id: 'A'.repeat(20), project: 'WEB', repository: 'x', ref: 'm', state: 'Running' },
      null,
      'x',
    ]);
    expect(list).toEqual([{ id: ID, repository: 'WEB/site', ref: 'main INJECT' }]);
    expect(parseWorkspaceList({ not: 'an array' })).toEqual([]);
  });
  it('keeps valid ports, sorted and unique, and drops origins that are not that port of that workspace', () => {
    const body = [
      { port: 8080, origin: `https://p8080-${ID}.bb.example.com` },
      { port: 5173, origin: `https://p5173-${OTHER}.bb.example.com` },
      { port: 5173 },
      { port: 0 }, { port: 70000 }, { port: 1.5 }, { port: '80' }, null,
    ];
    const ports = parsePorts(body, ctx, ID);
    expect(ports).toEqual([{ port: 5173, origin: null }, { port: 8080, origin: `https://p8080-${ID}.bb.example.com` }]);
    expect(parsePorts('nope', ctx, ID)).toEqual([]);
  });
  it('accepts the open response only when its URL is the entry link for that workspace and port', () => {
    const good = entry(`p5173-${ID}`);
    expect(parseOpenResponse({ port: 5173, url: good }, ctx, ID, 5173)).toBe(good);
    expect(parseOpenResponse({ url: entry(`p5173-${OTHER}`) }, ctx, ID, 5173)).toBeNull();
    expect(parseOpenResponse({ url: 'https://evil.example/' }, ctx, ID, 5173)).toBeNull();
    expect(parseOpenResponse({ url: 'file:///etc/passwd' }, ctx, ID, 5173)).toBeNull();
    expect(parseOpenResponse({ url: 5 }, ctx, ID, 5173)).toBeNull();
    expect(parseOpenResponse(null, ctx, ID, 5173)).toBeNull();
    expect(parseOpenResponse({ url: good + 'x'.repeat(3000) }, ctx, ID, 5173)).toBeNull();
  });
});

describe('display', () => {
  it('shows a local-looking address and tolerates garbage', () => {
    expect(displayAddress('http://p5173-abc.localhost:5108')).toBe('p5173-abc.localhost:5108');
    expect(displayAddress(null)).toBe('');
    expect(displayAddress('not a url')).toBe('');
  });
  it('builds one menu line per port', () => {
    const lines = menuEntries([
      { workspace: { id: ID, repository: 'WEB/site', ref: 'main' }, ports: [{ port: 5173, origin: 'http://p5173-x.localhost:5108' }, { port: 8080, origin: null }] },
      { workspace: { id: OTHER, repository: 'WEB/api', ref: 'dev' }, ports: [] },
    ]);
    expect(lines.map((l) => [l.workspaceId, l.port])).toEqual([[ID, 5173], [ID, 8080]]);
    expect(lines[0]!.label).toContain('p5173-x.localhost:5108');
  });
  it('needs the server capability', () => {
    expect(serverSupportsPorts(['notifications', 'workspace-ports'])).toBe(true);
    expect(serverSupportsPorts(['notifications'])).toBe(false);
    expect(serverSupportsPorts(undefined)).toBe(false);
  });
});
