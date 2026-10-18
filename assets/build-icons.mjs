import { execSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';

const SIZES = [256, 128, 64, 48, 32, 16];
const EDGE_PATH = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const assetsDir = path.resolve('D:/rea/mirdm/assets');
const renderUrl = `file:///${assetsDir.replace(/\\/g, '/')}/render.html`;

console.log('Generating PNGs at multiple resolutions...');
const pngBuffers = [];

for (const size of SIZES) {
  const pngPath = path.join(assetsDir, `icon_${size}.png`);
  const cmd = `"${EDGE_PATH}" --headless --screenshot="${pngPath}" --window-size=${size},${size} --default-background-color=00000000 "${renderUrl}"`;
  execSync(cmd, { stdio: 'pipe' });
  const buf = fs.readFileSync(pngPath);
  console.log(`Rendered ${size}x${size} PNG: ${buf.length} bytes`);
  pngBuffers.push({ size, buffer: buf });
}

// Also save high-res icon.png
fs.copyFileSync(path.join(assetsDir, 'icon_256.png'), path.join(assetsDir, 'icon.png'));

// Build Windows .ico binary
console.log('Assembling multi-resolution icon.ico...');
const count = SIZES.length;
const headerSize = 6 + count * 16;
let currentOffset = headerSize;

// 6 bytes ICONDIR header
const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0); // Reserved
header.writeUInt16LE(1, 2); // Type 1 = Icon
header.writeUInt16LE(count, 4); // Number of images

const dirEntries = [];
for (const item of pngBuffers) {
  const entry = Buffer.alloc(16);
  // Width & Height (0 represents 256)
  entry.writeUInt8(item.size === 256 ? 0 : item.size, 0);
  entry.writeUInt8(item.size === 256 ? 0 : item.size, 1);
  entry.writeUInt8(0, 2); // Color count (0 = >=8bpp)
  entry.writeUInt8(0, 3); // Reserved
  entry.writeUInt16LE(1, 4); // Color planes
  entry.writeUInt16LE(32, 6); // Bits per pixel
  entry.writeUInt32LE(item.buffer.length, 8); // Image size in bytes
  entry.writeUInt32LE(currentOffset, 12); // Image offset

  dirEntries.push(entry);
  currentOffset += item.buffer.length;
}

const icoBuffer = Buffer.concat([
  header,
  ...dirEntries,
  ...pngBuffers.map((p) => p.buffer),
]);

const icoPath = path.join(assetsDir, 'icon.ico');
fs.writeFileSync(icoPath, icoBuffer);
console.log(`Successfully generated ${icoPath} (${icoBuffer.length} bytes)`);

// Also copy icon.ico to mirdm root and public
fs.copyFileSync(icoPath, path.resolve('D:/rea/mirdm/icon.ico'));
fs.copyFileSync(icoPath, path.resolve('D:/rea/mirdm/src/server/public/favicon.ico'));
if (fs.existsSync('D:/rea/mirdm/dist/server/public')) {
  fs.copyFileSync(icoPath, path.resolve('D:/rea/mirdm/dist/server/public/favicon.ico'));
}
console.log('Icon copied to project roots and web assets.');
