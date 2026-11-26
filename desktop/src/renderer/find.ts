(() => {
const q = document.getElementById('q') as HTMLInputElement;
const count = document.getElementById('count') as HTMLElement;

q.addEventListener('input', () => { count.textContent = ''; void bb.find(q.value, true, false); });
q.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { void bb.find(q.value, !e.shiftKey, true); e.preventDefault(); }
  else if (e.key === 'Escape') void bb.closeFind();
});
document.getElementById('next')?.addEventListener('click', () => void bb.find(q.value, true, true));
document.getElementById('prev')?.addEventListener('click', () => void bb.find(q.value, false, true));
document.getElementById('close')?.addEventListener('click', () => void bb.closeFind());
bb.onFindResult((r) => { count.textContent = q.value ? `${r.active}/${r.matches}` : ''; });
q.focus();
})();
