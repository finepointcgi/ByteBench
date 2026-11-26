(() => {
const q = document.getElementById('q') as HTMLInputElement;
const list = document.getElementById('list') as HTMLUListElement;
let all: BbProfile[] = [];
let shown: BbProfile[] = [];
let sel = 0;

function render(): void {
  const f = q.value.trim().toLowerCase();
  shown = all.filter((p) => !f || p.name.toLowerCase().includes(f) || p.origin.toLowerCase().includes(f));
  sel = Math.min(sel, Math.max(0, shown.length - 1));
  list.replaceChildren(
    ...shown.map((p, i) => {
      const li = document.createElement('li');
      li.className = i === sel ? 'sel' : '';
      const name = document.createElement('span');
      name.textContent = p.name;
      name.style.fontWeight = '600';
      const url = document.createElement('span');
      url.textContent = p.origin;
      url.className = 'url';
      li.append(name, url);
      if (p.unread > 0) {
        const b = document.createElement('span');
        b.className = 'badge';
        b.textContent = String(p.unread);
        li.append(b);
      }
      li.onclick = () => void bb.openProfile(p.id);
      return li;
    }),
  );
}

q.addEventListener('input', () => { sel = 0; render(); });
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') void bb.closePalette();
  else if (e.key === 'ArrowDown') { sel = Math.min(sel + 1, shown.length - 1); render(); e.preventDefault(); }
  else if (e.key === 'ArrowUp') { sel = Math.max(sel - 1, 0); render(); e.preventDefault(); }
  else if (e.key === 'Enter' && shown[sel]) void bb.openProfile(shown[sel].id);
});
void bb.listProfiles().then((r) => { all = r.profiles; render(); q.focus(); });
})();
