import { describe, expect, it } from 'vitest';
import { classifyUrl, isSafeExternal, permissionAllowed, workspaceBases } from '../../src/main/allowlist';

const ctx = { serverOrigin: 'https://bb.example.com', extraOrigins: ['https://login.idp.example'] };

describe('classifyUrl', () => {
  it('keeps the server origin internal', () => {
    expect(classifyUrl('https://bb.example.com/p/WEB/issues/1', ctx)).toBe('internal');
  });
  it('treats a different scheme, host or port as another origin', () => {
    expect(classifyUrl('http://bb.example.com/', ctx)).toBe('external');
    expect(classifyUrl('https://bb.example.com:8443/', ctx)).toBe('external');
    expect(classifyUrl('https://evil.example/', ctx)).toBe('external');
    expect(classifyUrl('https://bb.example.com.evil.example/', ctx)).toBe('external');
  });
  it('allows workspace origins of the same server only', () => {
    expect(classifyUrl('https://ide-abc123.bb.example.com/', ctx)).toBe('workspace');
    expect(classifyUrl('https://p3000-abc123.bb.example.com/x', ctx)).toBe('workspace');
    expect(classifyUrl('https://ide-abc123.evil.example/', ctx)).toBe('external');
    expect(classifyUrl('https://x.ide-abc123.bb.example.com/', ctx)).toBe('external');
    expect(classifyUrl('https://other.bb.example.com/', ctx)).toBe('external');
    expect(classifyUrl('http://ide-abc.bb.example.com/', ctx)).toBe('external');
    expect(classifyUrl('https://ide-abc.bb.example.com:9000/', ctx)).toBe('external');
    expect(classifyUrl('https://ide-.bb.example.com/', ctx)).toBe('external');
  });
  it('honors an advertised parent base domain, but not unrelated ones', () => {
    const c = { ...ctx, advertisedWorkspaceBase: 'example.com' };
    expect(classifyUrl('https://ide-abc.example.com/', c)).toBe('workspace');
    expect(classifyUrl('https://ide-abc.com/', { ...ctx, advertisedWorkspaceBase: 'com' })).toBe('external');
    expect(classifyUrl('https://ide-abc.other.org/', { ...ctx, advertisedWorkspaceBase: 'other.org' })).toBe('external');
  });
  it('supports localhost workspaces with the server port', () => {
    const l = { serverOrigin: 'http://localhost:5108' };
    expect(classifyUrl('http://ide-abc.localhost:5108/', l)).toBe('workspace');
    expect(classifyUrl('http://ide-abc.localhost:5109/', l)).toBe('external');
  });
  it('allows user trusted sign-in origins (https only)', () => {
    expect(classifyUrl('https://login.idp.example/auth', ctx)).toBe('sso');
    expect(classifyUrl('http://login.idp.example/auth', ctx)).toBe('external');
  });
  it('blocks non-web schemes and credentials, and sends mailto to the OS', () => {
    for (const u of ['file:///etc/passwd', 'javascript:alert(1)', 'data:text/html,hi', 'blob:https://bb.example.com/x', 'about:blank', 'bytebench://open', 'ftp://bb.example.com', 'not a url', 'https://u:p@bb.example.com/']) {
      expect(classifyUrl(u, ctx)).toBe('blocked');
    }
    expect(classifyUrl('mailto:a@b.example', ctx)).toBe('external');
  });
});

describe('helpers', () => {
  it('isSafeExternal', () => {
    expect(isSafeExternal('https://x.example')).toBe(true);
    expect(isSafeExternal('file:///x')).toBe(false);
    expect(isSafeExternal('smb://x')).toBe(false);
  });
  it('workspaceBases ignores public suffixes and unrelated domains', () => {
    expect(workspaceBases('bb.example.com', 'example.com')).toEqual(['bb.example.com', 'example.com']);
    expect(workspaceBases('bb.example.com', 'com')).toEqual(['bb.example.com']);
    expect(workspaceBases('bb.example.com', 'evil.org')).toEqual(['bb.example.com']);
  });
  it('permissions: notifications and clipboard only, for the right origins', () => {
    expect(permissionAllowed('notifications', 'https://bb.example.com/', ctx)).toBe(true);
    expect(permissionAllowed('notifications', 'https://ide-a.bb.example.com/', ctx)).toBe(false);
    expect(permissionAllowed('clipboard-read', 'https://ide-a.bb.example.com/', ctx)).toBe(true);
    expect(permissionAllowed('clipboard-sanitized-write', 'https://bb.example.com/', ctx)).toBe(true);
    for (const p of ['media', 'geolocation', 'openExternal', 'midi', 'hid', 'usb', 'fullscreen']) expect(permissionAllowed(p, 'https://bb.example.com/', ctx)).toBe(false);
    expect(permissionAllowed('notifications', 'https://evil.example/', ctx)).toBe(false);
  });
});
