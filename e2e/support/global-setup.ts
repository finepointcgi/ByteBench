import { startServer, freePort, type RunningServer, writeState, E2E_ROOT, serverErrors } from './server.ts';
import { prepareDockerTier, cleanupDockerTier, type DockerTier } from './docker.ts';
import path from 'node:path';
import { mkdirSync, writeFileSync } from 'node:fs';

let server: RunningServer | undefined;
let docker: DockerTier | undefined;

/** Which tier is running: `E2E_TIER` (set by run.sh and the npm scripts), else inferred from --project on the command line. */
export function currentTier(): 'smoke' | 'docker' {
  if (process.env.E2E_TIER === 'docker' || process.env.E2E_TIER === 'smoke') return process.env.E2E_TIER;
  return process.argv.some((a) => a === '--project=docker' || a === 'docker') ? 'docker' : 'smoke';
}

export default async function globalSetup(): Promise<() => Promise<void>> {
  const tier = currentTier();
  const env: Record<string, string> = {};
  let listenAll = false;
  let port: number | undefined;

  if (tier === 'smoke') {
    // No Docker at all: the server must not even try to reach a daemon or build the workspace image.
    env.Workspaces__DockerExecutable = 'docker-disabled-for-e2e';
  } else {
    port = await freePort();
    docker = await prepareDockerTier(port);
    listenAll = true;
    Object.assign(env, docker.serverEnv);
    if (process.env.E2E_REAL_AI === '1') {
      // The opt-in real AI run (tests/docker/ai-run.spec.ts): the local Claude Code CLI as the backend, with small limits so a run costs cents.
      env.Workspaces__AiRuns__Backend = 'claude-cli';
      env.Workspaces__AiRuns__MaxSteps = process.env.E2E_AI_MAX_STEPS ?? '20';
      env.Workspaces__AiRuns__MaxCostUsd = process.env.E2E_AI_MAX_USD ?? '1.5';
      env.Workspaces__AiRuns__MaxDurationMinutes = '12';
      env.Workspaces__AiRuns__PollSeconds = '2';
    }
  }

  server = await startServer({ env, listenAll, port });
  process.env.E2E_BASE_URL = server.baseUrl;
  process.env.E2E_DATA_DIR = server.dataDir;
  process.env.E2E_SERVER_LOG = server.logPath;
  process.env.E2E_TIER = tier;
  if (docker) process.env.E2E_DOCKER_TAG = docker.tag;
  mkdirSync(path.join(E2E_ROOT, 'test-results'), { recursive: true });
  writeState(path.join(E2E_ROOT, '.e2e-state.json'), { tier, baseUrl: server.baseUrl, dataDir: server.dataDir, logPath: server.logPath, pid: server.pid });
  console.log(`[e2e] ${tier} tier against ${server.baseUrl} (data ${server.dataDir})`);

  return async () => {
    const errs = server!.errorLines();
    // Keep the server log with the test artifacts (CI uploads test-results/).
    try {
      const out = path.join(E2E_ROOT, 'test-results');
      mkdirSync(out, { recursive: true });
      const { copyFileSync } = await import('node:fs');
      copyFileSync(server!.logPath, path.join(out, 'server.log'));
      writeFileSync(path.join(out, 'server-errors.txt'), errs.join('\n\n'));
    } catch { /* best effort */ }
    await server!.stop();
    if (docker && process.env.E2E_KEEP_DOCKER !== '1') await cleanupDockerTier(docker);
    else if (docker) console.log(`[e2e] E2E_KEEP_DOCKER=1: containers of instance ${docker.instanceKey} and network ${docker.network} left running; remove with: e2e/run.sh docker-clean`);
    if (errs.length && process.env.E2E_ALLOW_SERVER_ERRORS !== '1') {
      throw new Error(`The server logged ${errs.length} error(s) (fail:/crit:) during the run; see test-results/server-errors.txt.\n\n${errs.slice(0, 5).join('\n\n')}`);
    }
  };
}

export { serverErrors };
