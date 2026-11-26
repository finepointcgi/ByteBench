// Copies the static renderer files (html, css) next to the compiled scripts.
import { cpSync, mkdirSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const from = 'src/renderer';
const to = 'dist/renderer';
mkdirSync(to, { recursive: true });
for (const f of readdirSync(from)) {
  if (f.endsWith('.html') || f.endsWith('.css')) cpSync(join(from, f), join(to, f));
}
