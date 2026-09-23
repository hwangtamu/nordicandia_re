import { copyFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join, resolve } from 'node:path';

// Triple the portal weight multiplier used by Game.Items.ItemGenerator.CreateRandomItem.
//
// CreateRandomItem computes the value handed to ItemGenerator.InitializeItemTypeDropPool as
//     effectivePortalsWeightMult = contextPortalsWeightMult + Portals_Drop_Weight_Bonus_Percent
// (see steam_analysis/create_random_item.asm @ 0x180d53bb9-0x180d53bcd).
//
// The two `movsd xmm2, [rbp+0x1c0]` loads of `contextPortalsWeightMult` (the normal path and
// the playerAttributes==null path) are rewritten to load an existing aligned double 3.0 from
// .rdata, so the portal item-type weight is scaled ~3x. This mirrors
// patch-desktop-magic-find.mjs, which forces the magic-find factor to a constant.
//
// The attribute is still added afterwards on the normal path, so a character wearing the
// PortalDropRate affix ends up at 3.0 + bonus rather than 3 * (1 + bonus); the base rate is
// tripled, which is what "3x portal drops" means in practice.

const clientDirectory = resolve(process.argv[2] ?? 'dist/desktop');
const multiplier = Number(process.argv[3] ?? '3');
const dllPath = join(clientDirectory, 'GameAssembly.dll');
const backupPath = join(clientDirectory, 'GameAssembly.dll.portal-drop.orig');
const manifestPath = join(clientDirectory, 'private-build.json');

if (multiplier !== 3) {
  throw Error('Nordicandia 1.9.3 currently has a verified patch only for a 3x portal-drop multiplier');
}
if (!existsSync(dllPath)) throw Error(`GameAssembly.dll is missing from ${clientDirectory}`);

// `movsd xmm2, [rbp+0x1c0]` -> `movsd xmm2, [rip+THREE_VA]`, both 8 bytes.
const PATCH_SITES = [0x180d53bc5, 0x180d53bd3]; // CreateRandomItem, contextPortalsWeightMult loads
const THREE_VA = 0x1838e1cd0;                    // existing aligned double constant 3.0 in .rdata
const EXPECTED = Buffer.from('f20f1095c0010000', 'hex'); // movsd xmm2, [rbp+0x1c0]

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

function replacementFor(siteVa) {
  // movsd xmm2, [rip+disp32]
  const instruction = Buffer.alloc(8);
  instruction.set(Buffer.from('f20f1015', 'hex'));
  instruction.writeInt32LE(THREE_VA - (siteVa + instruction.length), 4);
  return instruction;
}

const dll = readFileSync(dllPath);
const before = sha256(dll);
const pe = readPe(dll);
if (pe.imageBase !== 0x180000000) throw Error(`Unexpected image base 0x${pe.imageBase.toString(16)}`);

const threeOffset = pe.fileOffset(THREE_VA);
if (dll.readDoubleLE(threeOffset) !== multiplier) throw Error(`The expected ${multiplier}.0 constant is missing`);

const sites = PATCH_SITES.map((siteVa) => {
  const offset = pe.fileOffset(siteVa);
  const expected = EXPECTED;
  const replacement = replacementFor(siteVa);
  const current = Buffer.from(dll.subarray(offset, offset + expected.length));
  return { siteVa, offset, expected, replacement, current, alreadyPatched: current.equals(replacement) };
});
const alreadyPatched = sites.every((s) => s.alreadyPatched);
for (const s of sites) {
  if (!s.alreadyPatched && !s.current.equals(s.expected)) {
    throw Error(`Unexpected bytes at portal-drop patch site 0x${s.siteVa.toString(16)}: ${s.current.toString('hex')}`);
  }
}

if (alreadyPatched) {
  console.log('Portal-drop patch was already installed.');
} else {
  if (!existsSync(backupPath)) copyFileSync(dllPath, backupPath);
  for (const s of sites) s.replacement.copy(dll, s.offset);
  writeFileSync(dllPath, dll);
}

const afterData = readFileSync(dllPath);
const after = sha256(afterData);
const manifest = existsSync(manifestPath)
  ? JSON.parse(readFileSync(manifestPath, 'utf8'))
  : {};
manifest.gameplayPatches ??= {};
const previousPatch = manifest.gameplayPatches.portalDrop;
manifest.gameplayPatches.portalDrop = {
  multiplier,
  function: 'Game.Items.ItemGenerator.CreateRandomItem',
  parameter: 'contextPortalsWeightMult',
  virtualAddresses: PATCH_SITES.map((va) => `0x${va.toString(16)}`),
  fileOffsets: sites.map((s) => `0x${s.offset.toString(16)}`),
  originalSha256: previousPatch?.originalSha256 ?? before,
  patchedSha256: after,
};
writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);

console.log(alreadyPatched ? 'Portal-drop patch already present.' : 'Installed 3x portal-drop patch.');
console.log(`Client: ${clientDirectory}`);
for (const s of sites) console.log(`  site 0x${s.siteVa.toString(16)} -> file 0x${s.offset.toString(16)} : ${s.current.toString('hex')} -> ${s.replacement.toString('hex')}`);
console.log(`GameAssembly.dll SHA-256: ${after}`);