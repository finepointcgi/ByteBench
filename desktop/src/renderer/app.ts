(() => {
// The app's own pages: server picker, "connecting" and the friendly error page. Plain DOM calls only (textContent, never innerHTML).
const root = document.getElementById('root') as HTMLElement;
const view = new URLSearchParams(location.search).get('view') ?? 'picker';

function el<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> & { class?: string } = {}, ...kids: (Node | string)[]): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  const { class: cls, ...rest } = props;
  if (cls) e.className = cls;
  Object.assign(e, rest);
  for (const k of kids) e.append(k);
  return e;
}

function header(title: string, lead: string): HTMLElement {
  return el('div', {}, el('div', { class: 'brand' }, el('div', { class: 'logo' }), el('h1', { textContent: title })), el('p', { class: 'lead', textContent: lead }));
}

// ---------------------------------------------------------------- picker

let editing: BbProfile | null = null;

async function renderPicker(): Promise<void> {
  const { profiles, message } = await bb.listProfiles();
  root.replaceChildren();
  const main = el('main');
  root.append(main);
  main.append(
    header(profiles.length ? 'Your ByteBench servers' : 'Welcome to ByteBench', profiles.length ? 'Pick a server, or add another one.' : 'Add the address of your ByteBench server to get started.'),
  );
  if (message) main.append(el('div', { class: 'msg warn', textContent: message }));

  for (const p of profiles) {
    const row = el('div', { class: 'card row' });
    const info = el('div', { class: 'grow' }, el('div', { class: 'name', textContent: p.name }), el('div', { class: 'url', textContent: p.origin }));
    row.append(info);
    if (p.allowSelfSigned) row.append(el('span', { class: 'tag', textContent: p.pinnedCert ? 'pinned certificate' : 'self-signed allowed' }));
    if (p.unread > 0) row.append(el('span', { class: 'badge', textContent: String(p.unread) }));
    row.append(
      el('button', { class: 'primary', textContent: 'Open', onclick: () => void bb.openProfile(p.id) }),
      el('button', { textContent: 'Edit', onclick: () => { editing = p; void renderPicker(); } }),
    );
    main.append(row);
  }
  main.append(el('h2', { textContent: editing ? `Edit ${editing.name}` : 'Add a server' }), form(editing));
}

function form(p: BbProfile | null): HTMLElement {
  const f = el('form', { class: 'card' });
  const name = el('input', { type: 'text', id: 'f-name', value: p?.name ?? '', placeholder: 'My ByteBench (optional)', maxLength: 60 });
  const url = el('input', { type: 'text', id: 'f-url', value: p?.origin ?? '', placeholder: 'https://bytebench.example.com or localhost:5108', disabled: !!p, spellcheck: false });
  const self = el('input', { type: 'checkbox', id: 'f-self', checked: p?.allowSelfSigned ?? false });
  const http = el('input', { type: 'checkbox', id: 'f-http' });
  const extra = el('input', { type: 'text', id: 'f-extra', value: (p?.extraOrigins ?? []).join(', '), placeholder: 'https://login.example.com (optional)' });
  const token = el('input', { type: 'password', id: 'f-token', placeholder: p?.hasToken ? 'A token is stored (leave empty to keep it)' : 'gbp_… (optional)', autocomplete: 'off' });
  const clear = el('input', { type: 'checkbox', id: 'f-clear' });
  const status = el('div');
  const httpRow = el('label', { class: 'check' }, http, el('span', { textContent: 'I understand plain http sends my password and data unencrypted.' }));
  httpRow.style.display = 'none';

  f.append(
    el('label', { htmlFor: 'f-name', textContent: 'Name' }), name,
    el('label', { htmlFor: 'f-url', textContent: 'Server address' }), url,
    el('p', { class: 'hint', textContent: p ? 'The address cannot be changed; add a new server instead.' : 'Use https. Plain http is fine for localhost and private networks.' }),
    httpRow,
    el('label', { class: 'check' }, self, el('span', { textContent: 'Trust a self-signed certificate for this server. You will be shown its fingerprint to confirm; only that exact certificate is accepted afterwards.' })),
    el('label', { htmlFor: 'f-extra', textContent: 'Trusted sign-in addresses (single sign-on)' }), extra,
    el('p', { class: 'hint', textContent: 'Only needed if your server signs in through another site. Comma separated https addresses; they open inside the app.' }),
    el('label', { htmlFor: 'f-token', textContent: 'API token (optional)' }), token,
    el('p', { class: 'hint', textContent: 'Used for desktop notifications and cloning. Stored encrypted with your operating system keychain. Without it, your signed-in session is used.' }),
  );
  if (p?.hasToken) f.append(el('label', { class: 'check' }, clear, el('span', { textContent: 'Remove the stored token' })));
  f.append(status);

  const submit = el('button', { class: 'primary', type: 'submit', textContent: p ? 'Save' : 'Add and open' });
  const bar = el('div', { class: 'row' }, submit);
  if (p) {
    bar.append(
      el('button', { type: 'button', textContent: 'Cancel', onclick: () => { editing = null; void renderPicker(); } }),
      el('span', { class: 'grow' }),
      el('button', {
        type: 'button', class: 'danger', textContent: 'Remove server',
        onclick: async () => {
          if (!confirm(`Remove ${p.name}? Its saved sign-in and cookies on this computer are deleted.`)) return;
          await bb.removeProfile(p.id);
          editing = null;
          void renderPicker();
        },
      }),
    );
  }
  f.append(el('div', { class: 'actions-top' }, bar));

  f.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    submit.disabled = true;
    const input: BbInput = {
      name: name.value,
      url: url.value,
      allowSelfSigned: self.checked,
      extraOrigins: extra.value.split(/[\s,]+/).filter(Boolean),
      confirmInsecureHttp: http.checked,
      token: token.value || undefined,
      clearToken: clear.checked,
    };
    const r = p ? await bb.updateProfile(p.id, input) : await bb.addProfile(input);
    submit.disabled = false;
    if (r.ok) {
      editing = null;
      if (p) void renderPicker();
      return;
    }
    status.replaceChildren(el('div', { class: 'msg error', textContent: r.error }));
    if (r.needsHttpConfirmation) httpRow.style.display = '';
  });
  return f;
}

// ---------------------------------------------------------------- connecting / error

function renderConnecting(): void {
  root.replaceChildren(el('div', { class: 'center' }, el('div', { class: 'inner' }, el('div', { class: 'spinner' }), el('p', { class: 'lead', textContent: 'Connecting…' }))));
}

async function renderError(): Promise<void> {
  const s = await bb.getErrorState();
  if (!s) return renderPicker();
  const inner = el('div', { class: 'inner' }, el('h1', { textContent: s.code === 'NOT_BYTEBENCH' ? 'This is not a ByteBench server' : `Cannot reach ${s.profileName}` }), el('p', { class: 'lead', textContent: s.description }), el('div', { class: 'url', textContent: s.url }));
  if (s.certificate) {
    const c = s.certificate;
    const box = el('div', { class: 'msg warn' });
    box.append(el('div', { textContent: c.pinned ? 'The server presented a different certificate than the one you trusted. This can mean someone is intercepting the connection.' : 'The server uses a certificate your computer does not trust.' }));
    box.append(el('div', { class: 'mono', textContent: `Subject: ${c.subject}\nIssuer: ${c.issuer}\nSHA-256: ${c.fingerprint}` }));
    if (!c.canTrust && !c.pinned) box.append(el('div', { class: 'hint', textContent: 'To trust it, edit this server and allow a self-signed certificate.' }));
    inner.append(box);
  }
  const actions = el('div', { class: 'actions' }, el('button', { class: 'primary', textContent: 'Try again', onclick: () => void bb.retry() }));
  if (s.certificate?.canTrust) actions.append(el('button', { textContent: 'Review and trust certificate…', onclick: () => void bb.trustCertificate() }));
  actions.append(el('button', { textContent: 'Servers…', onclick: () => { void bb.listProfiles(); location.search = '?view=picker'; } }));
  inner.append(actions, el('details', {}, el('summary', { textContent: 'Technical details' }), el('div', { class: 'mono', textContent: s.code })));
  root.replaceChildren(el('div', { class: 'center' }, inner));
}

if (view === 'connecting') renderConnecting();
else if (view === 'error') void renderError();
else {
  void renderPicker();
  bb.onProfilesChanged(() => { if (!editing) void renderPicker(); });
}
})();
