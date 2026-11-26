// Generates the app and tray icons from the same branch glyph as site/favicon.svg. Pure Node (no image libraries).
// Usage: node scripts/make-icons.mjs   (writes build/icon.png, resources/icon.png, resources/tray*.png)
import { deflateSync } from 'node:zlib';
import { mkdirSync, writeFileSync } from 'node:fs';

function crc32(buf) {
  let c, crc = 0xffffffff;
  for (let n = 0; n < buf.length; n++) {
    c = (crc ^ buf[n]) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = (crc >>> 8) ^ c;
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}
function png(w, h, rgba) {
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) {
    raw[y * (w * 4 + 1)] = 0;
    rgba.copy(raw, y * (w * 4 + 1) + 1, y * w * 4, (y + 1) * w * 4);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

// Glyph in a 32x32 design space.
const circles = [[10, 9, 3], [10, 23, 3], [22, 16, 3]];
const arc = [];
for (let i = 0; i <= 12; i++) {
  const a = (Math.PI / 2) * (i / 12); // from (16,16) up to (19,13): centre (16,13), radius 3
  arc.push([16 + 3 * Math.sin(a), 13 + 3 * Math.cos(a)]);
}
const strokes = [[[10, 12], [10, 20]], [[10, 16], [16, 16]], arc];
function segDist(px, py, [ax, ay], [bx, by]) {
  const dx = bx - ax, dy = by - ay;
  const t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy || 1)));
  return Math.hypot(px - (ax + t * dx), py - (ay + t * dy));
}
function glyph(x, y) {
  for (const [cx, cy, r] of circles) if (Math.hypot(x - cx, y - cy) <= r) return true;
  for (const pts of strokes) for (let i = 0; i < pts.length - 1; i++) if (segDist(x, y, pts[i], pts[i + 1]) <= 1) return true;
  return false;
}
function inRoundRect(x, y, r) {
  const cx = Math.min(Math.max(x, r), 32 - r), cy = Math.min(Math.max(y, r), 32 - r);
  return Math.hypot(x - cx, y - cy) <= r;
}

function render(size, mode) {
  const buf = Buffer.alloc(size * size * 4);
  const ss = 4;
  for (let py = 0; py < size; py++) {
    for (let px = 0; px < size; px++) {
      let bg = 0, fg = 0;
      for (let sy = 0; sy < ss; sy++) {
        for (let sx = 0; sx < ss; sx++) {
          const x = ((px + (sx + 0.5) / ss) / size) * 32;
          const y = ((py + (sy + 0.5) / ss) / size) * 32;
          if (mode === 'app') {
            if (inRoundRect(x, y, 7)) { bg++; if (glyph(x, y)) fg++; }
          } else if (glyph(x, y)) fg++;
        }
      }
      const n = ss * ss, o = (py * size + px) * 4;
      if (mode === 'app') {
        const a = bg / n, f = bg ? fg / bg : 0;
        buf[o] = Math.round(0x2f * (1 - f) + 255 * f);
        buf[o + 1] = Math.round(0x6f * (1 - f) + 255 * f);
        buf[o + 2] = Math.round(0xeb * (1 - f) + 255 * f);
        buf[o + 3] = Math.round(a * 255);
      } else {
        const c = mode === 'template' ? 0 : 0x2f;
        buf[o] = c; buf[o + 1] = mode === 'template' ? 0 : 0x6f; buf[o + 2] = mode === 'template' ? 0 : 0xeb;
        buf[o + 3] = Math.round((fg / n) * 255);
      }
    }
  }
  return png(size, size, buf);
}

mkdirSync('build', { recursive: true });
mkdirSync('resources', { recursive: true });
writeFileSync('build/icon.png', render(1024, 'app'));
writeFileSync('resources/icon.png', render(512, 'app'));
writeFileSync('resources/trayTemplate.png', render(22, 'template'));
writeFileSync('resources/trayTemplate@2x.png', render(44, 'template'));
writeFileSync('resources/tray.png', render(32, 'color'));
console.log('icons written');
