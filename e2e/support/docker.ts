// Docker-tier helpers: build a separate, e2e-only workspace image, name things uniquely, and clean up only what this run created.
//
// Safety: the e2e image uses its own repository name (`bytebench-e2e-workspace`), never `bytebench-workspace:*` (a developer's own
// image); containers, volumes and networks are found by the server instance's label/name prefix, which derives from the install
// path of the server binaries, so another ByteBench server on the same daemon is never touched.
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { REPO_ROOT, appDll } from './server.ts';

export interface DockerTier {
  tag: string;
  network: string;
  instanceKey: string;
  serverEnv: Record<string, string>;
}

export const E2E_IMAGE_REPO = 'bytebench-e2e-workspace';

function docker(args: string[], opts: { allowFail?: boolean } = {}): string {
  const r = spawnSync('docker', args, { encoding: 'utf8' });
  if (r.status !== 0 && !opts.allowFail) throw new Error(`docker ${args.join(' ')} failed: ${r.stderr || r.stdout}`);
  return (r.stdout ?? '').trim();
}

export function dockerAvailable(): boolean {
  const r = spawnSync('docker', ['info', '--format', '{{.ServerVersion}}'], { encoding: 'utf8' });
  return r.status === 0 && !!r.stdout.trim();
}

/** Same as CiDocker.InstanceKey in the server: first 12 hex chars of SHA-256 of AppContext.BaseDirectory (with trailing slash). */
export function instanceKey(): string {
  const baseDir = path.dirname(appDll()) + path.sep;
  return createHash('sha256').update(baseDir, 'utf8').digest('hex').slice(0, 12);
}

function contextHash(): string {
  const dir = path.join(REPO_ROOT, 'docker', 'workspace');
  const h = createHash('sha256');
  for (const f of readdirSync(dir).sort()) h.update(f).update(readFileSync(path.join(dir, f)));
  return h.digest('hex').slice(0, 12);
}

export async function prepareDockerTier(port: number): Promise<DockerTier> {
  if (!dockerAvailable()) throw new Error('The docker tier needs a running Docker daemon (docker info failed). Start Docker, or run the smoke tier instead.');
  const tag = `${E2E_IMAGE_REPO}:${contextHash()}`;
  const have = docker(['image', 'inspect', tag, '--format', '{{.Id}}'], { allowFail: true });
  if (!have) {
    console.log(`[e2e] building the workspace image ${tag} from docker/workspace (first run takes a few minutes; cached afterwards)`);
    execFileSync('docker', ['build', '-t', tag, path.join(REPO_ROOT, 'docker', 'workspace')], { stdio: 'inherit' });
  } else {
    console.log(`[e2e] workspace image ${tag} already built`);
  }
  const network = `bb-e2e-${randomBytes(4).toString('hex')}`;
  const key = instanceKey();
  return {
    tag, network, instanceKey: key,
    serverEnv: {
      Workspaces__Image: tag,
      Workspaces__Network: network,
      Workspaces__GitBaseUrl: `http://host.docker.internal:${port}`,
      Workspaces__StartupTimeoutSeconds: '240',
      Workspaces__IdleTimeoutMinutes: '120',
      Workspaces__Prebuilds__Enabled: 'true',
    },
  };
}

/** Removes the containers, volumes and networks this run's server instance created. Leaves images (a cache) alone. */
export async function cleanupDockerTier(t: DockerTier): Promise<void> {
  const ids = (cmd: string[]) => docker(cmd, { allowFail: true }).split('\n').filter(Boolean);
  const k = t.instanceKey;
  const containers = [
    ...ids(['ps', '-aq', '--filter', `label=bytebench-workspaces=${k}`]),
    ...ids(['ps', '-aq', '--filter', `name=^bb-(ws|wss)-${k}-`]),
  ];
  if (containers.length) docker(['rm', '-f', '-v', ...new Set(containers)], { allowFail: true });
  const nets = ids(['network', 'ls', '-q', '--filter', `name=^bb-wsn-${k}-`]);
  for (const n of nets) docker(['network', 'rm', n], { allowFail: true });
  docker(['network', 'rm', t.network], { allowFail: true });
  const vols = ids(['volume', 'ls', '-q', '--filter', `name=^bb-wsv-${k}-`]);
  if (vols.length) docker(['volume', 'rm', '-f', ...vols], { allowFail: true });
  // Prebuild images made by this run (labelled by the server) are small and tagged under the e2e repository.
}

/** Runs `docker` against the workspace of a given public id: returns the container name. */
export function containerFor(publicId: string): string {
  const k = instanceKey();
  const name = `bb-ws-${k}-${publicId.slice(0, 10)}`;
  return name;
}

export function dockerExec(container: string, cmd: string, user = 'coder'): { code: number; out: string } {
  const r = spawnSync('docker', ['exec', '-u', user, container, 'bash', '-lc', cmd], { encoding: 'utf8' });
  return { code: r.status ?? -1, out: (r.stdout ?? '') + (r.stderr ?? '') };
}

export function dockerLogs(container: string): string {
  const r = spawnSync('docker', ['logs', container], { encoding: 'utf8' });
  return (r.stdout ?? '') + (r.stderr ?? '');
}
