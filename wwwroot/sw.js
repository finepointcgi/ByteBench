// ByteBench service worker: shows browser push notifications (Services/Push) and opens their link on click.
// Served from the site root, so its scope is the whole site. It caches nothing and never touches page requests.
'use strict';

const FALLBACK = '/notifications';

// Only same-origin links: a payload can never send the user to another site.
function safeUrl(value) {
    try {
        const url = new URL(typeof value === 'string' && value ? value : FALLBACK, self.location.origin);
        return url.origin === self.location.origin ? url.href : new URL(FALLBACK, self.location.origin).href;
    } catch {
        return new URL(FALLBACK, self.location.origin).href;
    }
}

function text(value, max) {
    const s = typeof value === 'string' ? value : '';
    return s.length > max ? s.slice(0, max - 1) + '…' : s;
}

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

self.addEventListener('push', event => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch { data = {}; }
    const title = text(data.title, 120) || 'ByteBench';
    event.waitUntil(self.registration.showNotification(title, {
        body: text(data.body, 120),
        icon: '/favicon.png',
        badge: '/favicon.png',
        tag: typeof data.tag === 'string' ? data.tag.slice(0, 64) : undefined,
        data: { url: safeUrl(data.url) },
    }));
});

function keyBytes(base64url) {
    const s = base64url.replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(s + '='.repeat((4 - s.length % 4) % 4));
    return Uint8Array.from(raw, c => c.charCodeAt(0));
}

// The browser rotated or expired the subscription: subscribe again with the same server key and tell the server, which swaps the
// old endpoint for the new one. The session cookie authenticates it; if the user is signed out the server answers 401 and the
// next visit to the notification settings shows the browser as not subscribed.
async function resubscribe(event) {
    const old = event.oldSubscription || null;
    let sub = event.newSubscription || null;
    if (!sub) {
        let key = old && old.options && old.options.applicationServerKey ? new Uint8Array(old.options.applicationServerKey) : null;
        if (!key) {
            const res = await fetch('/push/vapid-public-key', { credentials: 'same-origin' });
            if (!res.ok) return;
            key = keyBytes((await res.json()).publicKey);
        }
        sub = await self.registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
    }
    await fetch('/push/resubscribe', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-ByteBench-Push': 'resubscribe' },
        body: JSON.stringify({ oldEndpoint: old ? old.endpoint : null, subscription: sub.toJSON() }),
    });
}

self.addEventListener('pushsubscriptionchange', event => {
    event.waitUntil(resubscribe(event).catch(() => { /* retried on the next change; the settings page shows the real state */ }));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = safeUrl(event.notification.data && event.notification.data.url);
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        const exact = windows.find(w => w.url === url);
        if (exact) return exact.focus();
        const same = windows.find(w => new URL(w.url).origin === self.location.origin);
        if (same && 'navigate' in same) {
            const focused = await same.focus();
            return (focused || same).navigate(url);
        }
        return self.clients.openWindow(url);
    })());
});
