import { Notification } from 'electron';
import type { Profile } from '../shared/types';
import type { Compat } from './compat';
import { nextPollDelayMs, parseNotificationList, pathForNotification, selectToAnnounce } from './notify-logic';
import { getJson } from './net';

export interface NotifierHost {
  profiles(): Profile[];
  enabled(): boolean;
  session(profile: Profile): Electron.Session;
  token(profileId: string): string | undefined;
  compat(profile: Profile): Compat | undefined;
  setLastNotified(profileId: string, id: number): void;
  openProfile(profileId: string, path: string): void;
  unreadChanged(total: number, perProfile: ReadonlyMap<string, number>): void;
}

/**
 * Polls each saved server's GET /api/v1/notifications (with the stored token if there is one, else the web session's cookie),
 * shows an OS notification per new unread item, and reports the unread total for the badge.
 * Servers that do not advertise the "notifications" capability are not polled. Failures back off up to 5 minutes.
 */
export class Notifier {
  private timers = new Map<string, NodeJS.Timeout>();
  private failures = new Map<string, number>();
  private unread = new Map<string, number>();
  private running = false;
  /** Notifications we showed: a reference is kept so they are not garbage collected before the user clicks. */
  private shown = new Set<Notification>();

  constructor(private readonly host: NotifierHost) {}

  start(): void {
    this.running = true;
    this.sync();
  }

  stop(): void {
    this.running = false;
    for (const t of this.timers.values()) clearTimeout(t);
    this.timers.clear();
  }

  /** Re-read the profile list: start polling new profiles and forget removed ones. */
  sync(): void {
    if (!this.running) return;
    const ids = new Set(this.host.profiles().map((p) => p.id));
    for (const id of [...this.timers.keys()]) {
      if (!ids.has(id)) {
        clearTimeout(this.timers.get(id));
        this.timers.delete(id);
        this.unread.delete(id);
        this.failures.delete(id);
      }
    }
    for (const id of ids) if (!this.timers.has(id)) this.schedule(id, 1500);
    this.report();
  }

  unreadFor(profileId: string): number {
    return this.unread.get(profileId) ?? 0;
  }

  /** Poll now (menu "Check notifications"). */
  pollNow(): void {
    for (const id of this.timers.keys()) this.schedule(id, 0);
  }

  private schedule(profileId: string, delayMs: number): void {
    clearTimeout(this.timers.get(profileId));
    this.timers.set(
      profileId,
      setTimeout(() => void this.poll(profileId), delayMs),
    );
  }

  private async poll(profileId: string): Promise<void> {
    const profile = this.host.profiles().find((p) => p.id === profileId);
    if (!profile || !this.running) return;
    let ok: boolean;
    try {
      ok = await this.pollOnce(profile);
    } catch {
      ok = false;
    }
    if (!this.running || !this.timers.has(profileId)) return;
    const failures = ok ? 0 : (this.failures.get(profileId) ?? 0) + 1;
    this.failures.set(profileId, failures);
    this.schedule(profileId, nextPollDelayMs(failures));
  }

  /** @returns false when the server could not be reached (so the caller backs off). */
  private async pollOnce(profile: Profile): Promise<boolean> {
    const compat = this.host.compat(profile);
    if (compat && (compat.level === 'legacy' || compat.level === 'not-bytebench')) return true; // nothing to poll; check again at the normal rate
    if (compat && 'info' in compat && !compat.info.capabilities.includes('notifications')) return true;

    const res = await getJson(this.host.session(profile), profile.origin, '/api/v1/notifications?limit=50', { token: this.host.token(profile.id) });
    if (res.status === null || res.status >= 500) return false;
    if (res.status === 401 || res.status === 403 || res.status === 404) {
      // Not signed in (or token rejected, or an old server): no unread count to show.
      this.setUnread(profile.id, 0);
      return true;
    }
    const list = res.status === 200 ? parseNotificationList(res.body) : null;
    if (!list) return true;

    this.setUnread(profile.id, list.unreadCount);
    const { show, more, newest } = selectToAnnounce(list.items, profile.lastNotifiedId);
    if (this.host.enabled() && Notification.isSupported()) {
      for (const n of show) this.announce(profile, n.title, n.actor ?? undefined, pathForNotification(n, profile.origin));
      if (more > 0) this.announce(profile, `${more} more unread notifications`, undefined, '/notifications');
    }
    if (profile.lastNotifiedId === undefined) this.host.setLastNotified(profile.id, newest ?? 0);
    else if (newest !== undefined) this.host.setLastNotified(profile.id, newest);
    return true;
  }

  private announce(profile: Profile, body: string, actor: string | undefined, path: string): void {
    const n = new Notification({ title: actor ? `${profile.name}: ${actor}` : profile.name, body, silent: false });
    this.shown.add(n);
    n.on('click', () => {
      this.shown.delete(n);
      this.host.openProfile(profile.id, path);
    });
    n.on('close', () => this.shown.delete(n));
    n.show();
  }

  private setUnread(profileId: string, count: number): void {
    if (this.unread.get(profileId) === count) return;
    this.unread.set(profileId, count);
    this.report();
  }

  private report(): void {
    let total = 0;
    for (const v of this.unread.values()) total += v;
    this.host.unreadChanged(total, this.unread);
  }
}
