// Shared helper: one non-interactive, tool-less model call through the Claude Code CLI (`claude -p`).
// The CLI uses the signed-in account locally and ANTHROPIC_API_KEY in CI. No tools are enabled, so whatever is in the prompt
// (logs, diffs and issue text are untrusted data) can only influence the text that comes back, never run anything.
import { spawn } from 'node:child_process';

export interface AskResult { text: string; costUsd: number }

export async function haveModel(): Promise<boolean> {
  return new Promise((resolve) => { const c = spawn('claude', ['--version'], { stdio: 'ignore' }); c.on('error', () => resolve(false)); c.on('exit', (code) => resolve(code === 0)); });
}

export async function ask(prompt: string, system: string, opts: { model?: string; maxUsd?: number } = {}): Promise<AskResult> {
  const args = ['-p', '--output-format', 'json', '--tools', '', '--no-session-persistence', '--append-system-prompt', system, '--max-budget-usd', String(opts.maxUsd ?? 1)];
  if (opts.model) args.push('--model', opts.model);
  return new Promise((resolve, reject) => {
    const c = spawn('claude', args, { stdio: ['pipe', 'pipe', 'pipe'] });
    let out = '', err = '';
    c.stdout.on('data', (d) => { out += d; });
    c.stderr.on('data', (d) => { err += d; });
    c.on('error', reject);
    c.on('exit', (code) => {
      try {
        const j = JSON.parse(out);
        if (j.is_error) return reject(new Error(`model call failed: ${String(j.result).slice(0, 300)}`));
        resolve({ text: String(j.result ?? ''), costUsd: Number(j.total_cost_usd ?? 0) });
      } catch { reject(new Error(`claude exited ${code}: ${(err || out).slice(0, 400)}`)); }
    });
    c.stdin.end(prompt); // the prompt goes over stdin (it can be large)
  });
}

/** The first JSON object or array in a model answer (it may be wrapped in a ```json fence). */
export function extractJson<T>(text: string): T | null {
  const fenced = /```(?:json)?\s*([\s\S]*?)```/.exec(text);
  const candidates = [fenced?.[1], text].filter(Boolean) as string[];
  for (const c of candidates) {
    const start = c.search(/[[{]/);
    if (start < 0) continue;
    for (let end = c.length; end > start; end--) {
      const ch = c[end - 1];
      if (ch !== '}' && ch !== ']') continue;
      try { return JSON.parse(c.slice(start, end)) as T; } catch { /* keep shrinking */ }
    }
  }
  return null;
}

/** Keeps untrusted text from pinging people or injecting markdown structure into a GitHub comment. */
export function sanitizeForComment(s: string): string {
  return s.replace(/@([A-Za-z0-9_-]+)/g, '@​$1').replace(/<!--/g, '&lt;!--').replace(/<\/?(script|iframe|img|style)[^>]*>/gi, '');
}
