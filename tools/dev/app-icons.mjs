// Draws the fan app's placeholder icon and splash image (apps/mobile/assets) with no image library: a dark square,
// a purple disc, and the red velvet rope motif across it. Rerun after changing it: node tools/dev/app-icons.mjs
import { deflateSync } from 'node:zlib';
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const out = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'apps', 'mobile', 'assets');
const BACKGROUND = [0x0f, 0x11, 0x15];
const ACCENT = [0x6c, 0x5c, 0xe7];
const ROPE = [0xb3, 0x1b, 0x2c];
const POST = [0xd4, 0xaf, 0x37];

/** Coverage 0..1 of a disc of radius r at (cx, cy), with one pixel of anti-aliasing. */
const disc = (x, y, cx, cy, r) => Math.min(1, Math.max(0, r - Math.hypot(x - cx, y - cy) + 0.5));

function draw(size, opaque) {
  const pixels = Buffer.alloc(size * size * 4);
  const mid = size / 2;
  const sag = (x) => mid - size * 0.05 + size * 0.16 * Math.cos((Math.PI * (x - mid)) / (size * 0.66)); // the rope sags in the middle
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let rgb = opaque ? BACKGROUND : [0, 0, 0];
      let alpha = opaque ? 1 : 0;
      const blend = (color, cover) => {
        if (cover <= 0) return;
        rgb = rgb.map((c, i) => c * (1 - cover) + color[i] * cover);
        alpha = alpha + cover * (1 - alpha);
      };
      blend(ACCENT, disc(x, y, mid, mid, size * 0.36));
      const onRope = Math.abs(x - mid) < size * 0.33;
      if (onRope) blend(ROPE, Math.min(1, Math.max(0, size * 0.045 - Math.abs(y - sag(x)) + 0.5)));
      for (const px of [mid - size * 0.33, mid + size * 0.33]) {
        const top = sag(px) - size * 0.07;
        const inPost = Math.abs(x - px) < size * 0.03 && y > top && y < mid + size * 0.36;
        if (inPost) blend(POST, 1);
        blend(POST, disc(x, y, px, top, size * 0.05));
      }
      const o = (y * size + x) * 4;
      pixels[o] = Math.round(rgb[0]);
      pixels[o + 1] = Math.round(rgb[1]);
      pixels[o + 2] = Math.round(rgb[2]);
      pixels[o + 3] = Math.round(alpha * 255);
    }
  }
  return pixels;
}

function png(size, pixels, opaque) {
  const bpp = opaque ? 3 : 4;
  const raw = Buffer.alloc((size * bpp + 1) * size);
  for (let y = 0; y < size; y++) {
    raw[y * (size * bpp + 1)] = 0; // filter: none
    for (let x = 0; x < size; x++) {
      pixels.copy(raw, y * (size * bpp + 1) + 1 + x * bpp, (y * size + x) * 4, (y * size + x) * 4 + bpp);
    }
  }
  const chunk = (type, data) => {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));
    return Buffer.concat([length, body, crc]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(size, 0);
  header.writeUInt32BE(size, 4);
  header[8] = 8; // bit depth
  header[9] = opaque ? 2 : 6; // colour type: RGB or RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

const table = new Uint32Array(256).map((_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) crc = table[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

// App Store icons must be opaque; the splash image and Android's adaptive foreground sit on a coloured background.
writeFileSync(join(out, 'icon.png'), png(1024, draw(1024, true), true));
writeFileSync(join(out, 'splash-icon.png'), png(512, draw(512, false), false));
console.log('wrote', out);
