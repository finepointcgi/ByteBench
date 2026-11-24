// Browser push on the notification preferences page (Components/Notifications/PushPreferences.razor).
// Loaded as a module by the component. The permission prompt only ever follows a click on the page's own button: the click
// handler runs here, in the browser, so the browser sees a real user gesture.
// Flow: register /sw.js -> Notification.requestPermission() -> pushManager.subscribe(VAPID key) -> POST /push/subscriptions.

const SW_URL = '/sw.js';

function isIos() {
    return /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
}

function isStandalone() {
    return (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) || window.navigator.standalone === true;
}

function supported() {
    return window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
}

function keyBytes(base64url) {
    const s = base64url.replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(s + '='.repeat((4 - s.length % 4) % 4));
    return Uint8Array.from(raw, c => c.charCodeAt(0));
}

async function currentSubscription() {
    if (!supported()) return null;
    const reg = await navigator.serviceWorker.getRegistration('/');
    return reg ? reg.pushManager.getSubscription() : null;
}

/** What the page shows: support, permission, whether this browser is subscribed, and the iOS hints. */
export async function state() {
    const s = {
        supported: supported(),
        secureContext: window.isSecureContext,
        permission: 'Notification' in window ? Notification.permission : 'unsupported',
        subscribed: false,
        ios: isIos(),
        standalone: isStandalone(),
        error: null,
    };
    try { s.subscribed = !!(await currentSubscription()); } catch { s.subscribed = false; }
    return s;
}

/** The browser's IANA time zone (prefills the digest time zone). */
export function browserTimeZone() {
    try { return Intl.DateTimeFormat().resolvedOptions().timeZone || null; } catch { return null; }
}

async function send(method, body, token) {
    const res = await fetch('/push/subscriptions', {
        method,
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
        body: JSON.stringify(body),
    });
    if (!res.ok) {
        let message = 'The server refused the subscription (HTTP ' + res.status + ').';
        try { const j = await res.json(); if (j && j.error) message = j.error; } catch { /* not JSON */ }
        throw new Error(message);
    }
}

async function enable(token) {
    if (!supported()) throw new Error('This browser does not support web push here.');
    const permission = await Notification.requestPermission(); // inside the click handler
    if (permission !== 'granted') {
        throw new Error(permission === 'denied'
            ? 'Notifications are blocked for this site. Allow them in your browser\'s site settings, then try again.'
            : 'Notifications were not allowed.');
    }
    const reg = await navigator.serviceWorker.register(SW_URL, { scope: '/' });
    await navigator.serviceWorker.ready;
    const keyRes = await fetch('/push/vapid-public-key', { credentials: 'same-origin' });
    if (!keyRes.ok) throw new Error('Browser push is not available on this server.');
    const { publicKey } = await keyRes.json();
    let sub = await reg.pushManager.getSubscription();
    if (sub) {
        // A subscription made with another server key can't be reused.
        const existing = sub.options && sub.options.applicationServerKey ? new Uint8Array(sub.options.applicationServerKey) : null;
        const wanted = keyBytes(publicKey);
        if (!existing || existing.length !== wanted.length || existing.some((b, i) => b !== wanted[i])) {
            await sub.unsubscribe();
            sub = null;
        }
    }
    if (!sub) sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(publicKey) });
    await send('POST', sub.toJSON(), token);
}

async function disable(token) {
    const sub = await currentSubscription();
    if (!sub) return;
    const json = sub.toJSON();
    await sub.unsubscribe();
    await send('DELETE', { endpoint: json.endpoint }, token);
}

/**
 * Wires the buttons inside root ([data-push-action="enable|disable"]) and reports every change to the component
 * (dotnet.invokeMethodAsync('OnPushState', state)). Event delegation, so re-rendered buttons keep working.
 */
export function init(root, dotnet, token) {
    if (!root || root.dataset.pushBound === '1') return;
    root.dataset.pushBound = '1';
    root.addEventListener('click', async event => {
        const button = event.target.closest('[data-push-action]');
        if (!button || !root.contains(button) || button.disabled) return;
        event.preventDefault();
        const action = button.dataset.pushAction;
        button.disabled = true;
        let error = null;
        try {
            if (action === 'enable') await enable(token);
            else if (action === 'disable') await disable(token);
        } catch (e) {
            error = (e && e.message) || String(e);
        } finally {
            button.disabled = false;
        }
        const s = await state();
        s.error = error;
        try { await dotnet.invokeMethodAsync('OnPushState', s); } catch { /* the page was left */ }
    });
}
