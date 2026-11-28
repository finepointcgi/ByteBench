// `e2e/run.sh serve`: starts a disposable seeded server and keeps it running until Ctrl+C (handy for poking at what the tests see).
// E2E_SERVE_DOCKER=1 uses the docker tier's setup (the e2e workspace image, unique network, prebuilds on) so workspaces really start.
import { startServer, freePort } from './server.ts';
import { prepareDockerTier, cleanupDockerTier, type DockerTier } from './docker.ts';

const withDocker = process.env.E2E_SERVE_DOCKER === '1';
let tier: DockerTier | undefined;
let port: number | undefined;
let env: Record<string, string> = { Workspaces__DockerExecutable: 'docker-disabled-for-e2e' };
if (withDocker) {
  port = await freePort();
  tier = await prepareDockerTier(port);
  env = tier.serverEnv;
}
// E2E_SERVE_ENV='{"Ai__Enabled":"true"}': extra server settings as JSON.
if (process.env.E2E_SERVE_ENV) env = { ...env, ...JSON.parse(process.env.E2E_SERVE_ENV) };
const s = await startServer({ env, port, listenAll: withDocker });
console.log(`ByteBench e2e server: ${s.baseUrl}  (alice / bob / carol / dan, password ByteBench123; alice is the admin)`);
console.log(`data: ${s.dataDir}\nlog:  ${s.logPath}`);
const stop = async () => { await s.stop(); if (tier) await cleanupDockerTier(tier); process.exit(0); };
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
await new Promise(() => { /* until killed */ });
