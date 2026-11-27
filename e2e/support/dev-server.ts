// `e2e/run.sh serve`: starts a disposable seeded server and keeps it running until Ctrl+C (handy for poking at what the tests see).
import { startServer } from './server.ts';

const s = await startServer({ env: { Workspaces__DockerExecutable: process.env.E2E_SERVE_DOCKER === '1' ? 'docker' : 'docker-disabled-for-e2e' } });
console.log(`ByteBench e2e server: ${s.baseUrl}  (alice / bob / carol / dan, password ByteBench123; alice is the admin)`);
console.log(`data: ${s.dataDir}\nlog:  ${s.logPath}`);
const stop = async () => { await s.stop(); process.exit(0); };
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
await new Promise(() => { /* until killed */ });
