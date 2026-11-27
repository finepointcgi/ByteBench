// Starts a real, disposable ByteBench server for end-to-end tests and the AI explorer.
//
// Everything the server writes (SQLite database, bare repositories, attachments, data-protection keys, SSH host key, ...)
// goes to a fresh temp directory, so a run can never touch a developer's own bytebench.db / repos / keys, and the port is
// random, so it can never collide with a server on 5108.
import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { createWriteStream, mkdirSync, mkdtempSync, rmSync, readFileSync, existsSync, writeFileSync } from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
export const E2E_ROOT = path.resolve(REPO_ROOT, 'e2e');

export interface ServerOptions {
  /** Extra configuration, as environment variables (use the ASP.NET `A__B` form). */
  env?: Record<string, string>;
  /** Listen on all interfaces (the docker tier: containers must reach the server through host.docker.internal). */
  listenAll?: boolean;
  /** Skip `dotnet build` (a build already exists). Also E2E_NO_BUILD=1. */
  noBuild?: boolean;
  /** Fixed port (the docker tier needs it before the server starts, for Workspaces:GitBaseUrl). */
  port?: number;
  /** Where to keep the data directory; default is a fresh directory under the OS temp dir. */
  dataDir?: string;
}

export interface RunningServer {
  baseUrl: string;
  port: number;
  dataDir: string;
  logPath: string;
  pid: number;
  /** Stops the server; removes the data directory unless E2E_KEEP_DATA=1. */
  stop(): Promise<void>;
  /** Server log lines that are errors (`fail:` / `crit:` with their message), for the "no unhandled server errors" invariant. */
  errorLines(): string[];
}

export async function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.once('error', reject);
    s.listen(0, '127.0.0.1', () => {
      const { port } = s.address() as net.AddressInfo;
      s.close(() => resolve(port));
    });
  });
}

function buildApp(): void {
  const r = spawnSync('dotnet', ['build', 'ByteBench.csproj', '--nologo', '-v', 'q'], { cwd: REPO_ROOT, stdio: 'inherit' });
  if (r.status !== 0) throw new Error('dotnet build failed');
}

export function appDll(): string {
  const dll = path.join(REPO_ROOT, 'bin', 'Debug', 'net10.0', 'ByteBench.dll');
  if (!existsSync(dll)) throw new Error(`${dll} not found; run: dotnet build ByteBench.csproj`);
  return dll;
}

export async function startServer(opts: ServerOptions = {}): Promise<RunningServer> {
  if (!opts.noBuild && process.env.E2E_NO_BUILD !== '1') buildApp();
  const dll = appDll();

  const dataDir = opts.dataDir ?? mkdtempSync(path.join(os.tmpdir(), 'bytebench-e2e-'));
  mkdirSync(dataDir, { recursive: true });
  const port = opts.port ?? await freePort();
  const host = opts.listenAll ? '0.0.0.0' : '127.0.0.1';
  const logPath = path.join(dataDir, 'server.log');
  const log = createWriteStream(logPath);

  const env: NodeJS.ProcessEnv = {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Development',
    ASPNETCORE_URLS: `http://${host}:${port}`,
    DOTNET_NOLOGO: '1',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    // Fresh, isolated storage for everything.
    ConnectionStrings__Default: `Data Source=${path.join(dataDir, 'e2e.db')}`,
    Git__RepositoryRoot: path.join(dataDir, 'repos'),
    Attachments__Root: path.join(dataDir, 'attachments'),
    DataProtection__KeysPath: path.join(dataDir, 'keys'),
    Lfs__Root: path.join(dataDir, 'lfs'),
    Packages__Root: path.join(dataDir, 'packages'),
    Pages__Root: path.join(dataDir, 'pages'),
    Backup__Directory: path.join(dataDir, 'backups'),
    Ssh__HostKeyPath: path.join(dataDir, 'ssh_host_key'),
    Ssh__Port: String(await freePort()), // Development listens for git over SSH on 2222, which another server (or run) may hold
    // Seeded demo accounts: alice (admin), bob, carol, dan; password ByteBench123.
    Seed__DemoData: 'true',
    // Never let a test run send mail, call the Anthropic API or follow a developer's own user secrets.
    Email__Enabled: 'false',
    Ai__Enabled: 'false',
    Logging__LogLevel__Default: 'Information',
    ...opts.env,
  };
  // The developer's own settings must not leak in.
  for (const k of ['ANTHROPIC_API_KEY', 'Ai__ApiKey']) if (!opts.env || !(k in opts.env)) delete env[k];

  const child: ChildProcess = spawn('dotnet', [dll], { cwd: REPO_ROOT, env, stdio: ['ignore', 'pipe', 'pipe'] });
  child.stdout!.pipe(log, { end: false });
  child.stderr!.pipe(log, { end: false });
  let exited: number | null = null;
  child.on('exit', (code) => { exited = code ?? -1; });

  const baseUrl = `http://127.0.0.1:${port}`;
  const deadline = Date.now() + 120_000;
  for (;;) {
    if (exited !== null) throw new Error(`ByteBench exited early (code ${exited}). Log: ${logPath}\n${tail(logPath)}`);
    try {
      const r = await fetch(`${baseUrl}/health/ready`);
      if (r.ok) break;
    } catch { /* not up yet */ }
    if (Date.now() > deadline) { child.kill('SIGKILL'); throw new Error(`ByteBench did not become ready in 120s. Log: ${logPath}\n${tail(logPath)}`); }
    await new Promise((r) => setTimeout(r, 400));
  }

  const server: RunningServer = {
    baseUrl, port, dataDir, logPath, pid: child.pid!,
    async stop() {
      if (exited === null) {
        child.kill('SIGTERM');
        await new Promise<void>((resolve) => {
          const t = setTimeout(() => { child.kill('SIGKILL'); resolve(); }, 15_000);
          child.once('exit', () => { clearTimeout(t); resolve(); });
        });
      }
      log.end();
      if (process.env.E2E_KEEP_DATA !== '1') {
        try { rmSync(dataDir, { recursive: true, force: true, maxRetries: 3 }); } catch { /* best effort */ }
      }
    },
    errorLines() { return serverErrors(logPath); },
  };
  return server;
}

function tail(file: string, lines = 40): string {
  try { return readFileSync(file, 'utf8').split('\n').slice(-lines).join('\n'); } catch { return ''; }
}

/** `fail:` and `crit:` entries (with the first lines of their message) from an ASP.NET console log. */
export function serverErrors(logPath: string): string[] {
  let text = '';
  try { text = readFileSync(logPath, 'utf8'); } catch { return []; }
  const lines = text.split('\n');
  const out: string[] = [];
  for (let i = 0; i < lines.length; i++) {
    if (/^(fail|crit): /.test(lines[i])) out.push([lines[i], lines[i + 1] ?? '', lines[i + 2] ?? ''].join('\n').trim());
  }
  return out;
}

export function writeState(file: string, state: unknown): void {
  writeFileSync(file, JSON.stringify(state, null, 2));
}
