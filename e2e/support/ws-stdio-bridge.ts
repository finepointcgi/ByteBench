// Bridges stdin/stdout to ByteBench's workspace editor tunnel (WebSocket), so `ssh -o ProxyCommand=...` can use it.
// This is what `bytebench workspace tunnel <id>` does for a real editor; the e2e suite uses this small stand-in so it exercises the server side
// of the tunnel without needing the packaged CLI.  Usage: tsx ws-stdio-bridge.ts <wss-or-ws-url> <personal-access-token>
import WebSocket from 'ws';

const [url, token] = process.argv.slice(2);
const ws = new WebSocket(url, { headers: { Authorization: `Bearer ${token}` } });
ws.on('open', () => {
  process.stdin.on('data', (d) => ws.send(d, { binary: true }));
  process.stdin.on('end', () => ws.close());
});
ws.on('message', (d) => process.stdout.write(d as Buffer));
ws.on('close', () => process.exit(0));
ws.on('error', (e) => { process.stderr.write(`tunnel error: ${e.message}\n`); process.exit(1); });
