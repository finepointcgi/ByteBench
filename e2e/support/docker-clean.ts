// `e2e/run.sh docker-clean [--images]`: removes what an aborted docker-tier run left behind (containers, volumes and networks of this checkout's
// e2e server, and `bb-e2e-*` networks). With --images it also deletes the cached e2e workspace images (`bytebench-e2e-workspace:*`).
// It never touches `bytebench-workspace:*` images or another ByteBench server's containers.
import { spawnSync } from 'node:child_process';
import { cleanupDockerTier, instanceKey, E2E_IMAGE_REPO } from './docker.ts';

const d = (a: string[]) => (spawnSync('docker', a, { encoding: 'utf8' }).stdout ?? '').trim().split('\n').filter(Boolean);

await cleanupDockerTier({ tag: '', network: 'bb-e2e-none', instanceKey: instanceKey(), serverEnv: {} });
for (const n of d(['network', 'ls', '--format', '{{.Name}}', '--filter', 'name=^bb-e2e-'])) spawnSync('docker', ['network', 'rm', n], { stdio: 'inherit' });
if (process.argv.includes('--images')) {
  for (const i of d(['images', E2E_IMAGE_REPO, '--format', '{{.Repository}}:{{.Tag}}'])) spawnSync('docker', ['rmi', i], { stdio: 'inherit' });
}
console.log('[e2e] docker-clean done');
