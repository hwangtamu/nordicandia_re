import { copyFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join, resolve } from 'node:path';

const clientDirectory = resolve(process.argv[2] ?? 'dist/desktop');
const multiplier = Number(process.argv[3] ?? '10');
const dllPath = join(clientDirectory, 'GameAssembly.dll');
const backupPath = join(clientDirectory, 'GameAssembly.dll.magic-find.orig');
const manifestPath = join(clientDirectory, 'private-build.json');

if (multiplier !== 10) {
  throw Error('Nordicandia 1.9.3 currently has a verified patch only for a 10x magic-find multiplier');
}
if (!existsSync(dllPath)) throw Error(`GameAssembly.dll is missing from ${clientDirectory}`);

const PATCH_VA = 0x180d541b0; // ItemGenerator.CreateRandomItem: load magicFindFactorMultiplier
const TEN_VA = 0x1838d6d80;   // existing aligned double constant 10.0 in .rdata
const EXPECTED = Buffer.from('f20f10bdb0010000', 'hex'); // movsd xmm7, [rbp+0x1b0]

function sha256(data) {
  return createHash('sha256').update(data).digest('hex');
}

function readPe(buffer) {
  const pe = buffer.readUInt32LE(0x3c);
  if (buffer.toString('ascii', pe, pe + 4) !== 'PE\0\0') throw Error('GameAssembly.dll is not a PE file');
  const sectionCount = buffer.readUInt16LE(pe + 6);
  const optionalSize = buffer.readUInt16LE(pe + 20);
  const optional = pe + 24;
  if (buffer.readUInt16LE(optional) !== 0x20b) throw Error('Expected a 64-bit PE image');
  const imageBase = Number(buffer.readBigUInt64LE(optional + 24));
  const sectionTable = optional + optionalSize;

  const fileOffset = (virtualAddress) => {
    const rva = virtualAddress - imageBase;
    for (let i = 0; i < sectionCount; i++) {
      const section = sectionTable + i * 40;
      const virtualSize = buffer.readUInt32LE(section + 8);
      const sectionRva = buffer.readUInt32LE(section + 12);
      const rawSize = buffer.readUInt32LE(section + 16);
      const rawOffset = buffer.readUInt32LE(section + 20);
      if (rva >= sectionRva && rva < sectionRva + Math.max(virtualSize, rawSize)) {
        return rawOffset + rva - sectionRva;
      }
    }
    throw Error(`Virtual address 0x${virtualAddress.toString(16)} is outside the PE sections`);
  };

  return { imageBase, fileOffset };
}

const dll = readFileSync(dllPath);
const before = sha256(dll);
const pe = readPe(dll);
if (pe.imageBase !== 0x180000000) throw Error(`Unexpected image base 0x${pe.imageBase.toString(16)}`);

const patchOffset = pe.fileOffset(PATCH_VA);
const tenOffset = pe.fileOffset(TEN_VA);
if (dll.readDoubleLE(tenOffset) !== multiplier) throw Error('The expected 10.0 constant is missing');

// Keep the eight-byte instruction length unchanged:
//   movsd xmm7, [rbp+0x1b0]  ->  movsd xmm7, [rip+TEN_VA]
const replacement = Buffer.alloc(8);
replacement.set(Buffer.from('f20f103d', 'hex'));
replacement.writeInt32LE(TEN_VA - (PATCH_VA + replacement.length), 4);
const current = dll.subarray(patchOffset, patchOffset + EXPECTED.length);
const alreadyPatched = current.equals(replacement);

if (current.equals(EXPECTED)) {
  if (!existsSync(backupPath)) copyFileSync(dllPath, backupPath);
  replacement.copy(dll, patchOffset);
  writeFileSync(dllPath, dll);
} else if (!current.equals(replacement)) {
  throw Error(`Unexpected bytes at the magic-find patch site: ${current.toString('hex')}`);
}

const afterData = readFileSync(dllPath);
const after = sha256(afterData);
const manifest = existsSync(manifestPath)
  ? JSON.parse(readFileSync(manifestPath, 'utf8'))
  : {};
manifest.gameplayPatches ??= {};
const previousPatch = manifest.gameplayPatches.magicFind;
manifest.gameplayPatches.magicFind = {
  multiplier,
  function: 'Game.Items.ItemGenerator.CreateRandomItem',
  virtualAddress: `0x${PATCH_VA.toString(16)}`,
  fileOffset: `0x${patchOffset.toString(16)}`,
  originalSha256: previousPatch?.originalSha256 ?? before,
  patchedSha256: after,
};
writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);

console.log(alreadyPatched ? 'Magic-find patch was already installed.' : 'Installed 10x magic-find patch.');
console.log(`Client: ${clientDirectory}`);
console.log(`GameAssembly.dll SHA-256: ${after}`);
