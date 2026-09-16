import { cpSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { createHash } from 'node:crypto';

const source = resolve(process.argv[2] ?? 'C:/Program Files (x86)/Steam/steamapps/common/Nordicandia');
const destination = resolve(process.argv[3] ?? 'dist/desktop');
if (source === destination || destination.startsWith(source + '/')) throw Error('Use a separate output directory');
if (!existsSync(join(source, 'Nordicandia.exe'))) throw Error('Nordicandia.exe is missing from source');
mkdirSync(destination, { recursive: true });
cpSync(source, destination, { recursive: true });

// --- Disable BestHTTP's bundled-root TLS validation -------------------------
// The game's gRPC/HTTP stack (BestHTTP TLSSecurity + SecureTlsClient) validates the
// server certificate against its own packaged X509 root database, NOT the OS store, so
// a private-CA/localhost certificate is rejected mid-handshake. Overwriting the first
// byte of SecureTlsClient::NotifyServerCertificate with `ret` (0xC3) makes it a no-op.
// The address below is for Nordicandia 1.9.3 desktop (GameAssembly.dll, image base
// 0x180000000). The caller verifies the expected opcode before writing.
const TLS_PATCH_VA = 0x18109acb0; // SecureTlsClient::NotifyServerCertificate
function applyTlsBypass(dllPath) {
  const dll = readFileSync(dllPath);
  const pe = dll.readUInt32LE(0x3c);
  if (dll.toString('ascii', pe, pe + 4) !== 'PE\0\0') throw Error('GameAssembly.dll is not a PE file');
  const sectionCount = dll.readUInt16LE(pe + 6);
  const optionalSize = dll.readUInt16LE(pe + 20);
  const optional = pe + 24;
  const magic = dll.readUInt16LE(optional);
  const imageBase = magic === 0x20b ? Number(dll.readBigUInt64LE(optional + 24)) : dll.readUInt32LE(optional + 28);
  const sections = optional + optionalSize;
  const rva = TLS_PATCH_VA - imageBase;
  for (let i = 0; i < sectionCount; i++) {
    const s = sections + i * 40;
    const va = dll.readUInt32LE(s + 12), vsize = dll.readUInt32LE(s + 8), raw = dll.readUInt32LE(s + 20);
    if (rva < va || rva >= va + Math.max(vsize, dll.readUInt32LE(s + 16))) continue;
    const fileOffset = raw + (rva - va);
    if (dll[fileOffset] !== 0x48) throw Error(`Unexpected opcode at TLS patch site: 0x${dll[fileOffset].toString(16)}`);
    dll[fileOffset] = 0xc3; // ret
    writeFileSync(dllPath, dll);
    return fileOffset;
  }
  throw Error('TLS patch site not found in GameAssembly.dll');
}
const tlsPatchOffset = applyTlsBypass(join(destination, 'GameAssembly.dll'));

const relative = 'Nordicandia_Data/il2cpp_data/Metadata/global-metadata.dat';
const metadata = readFileSync(join(source, relative));
const before = createHash('sha256').update(metadata).digest('hex');
if (metadata.readUInt32LE(0) !== 0xfab11baf) throw Error('Unknown IL2CPP metadata format');
const table = metadata.readUInt32LE(8), size = metadata.readUInt32LE(12);
const strings = metadata.readUInt32LE(16);
const targets = new Set(['prod.nordicandia.net', 'staging.nordicandia.net']);
const patched = [];
for (let offset = table; offset < table + size; offset += 8) {
  const length = metadata.readUInt32LE(offset), start = strings + metadata.readUInt32LE(offset + 4);
  const value = metadata.toString('utf8', start, start + length);
  if (!targets.has(value)) continue;
  const replacement = Buffer.from('127.0.0.1');
  metadata.fill(0, start, start + length);
  replacement.copy(metadata, start);
  metadata.writeUInt32LE(replacement.length, offset);
  patched.push(value);
}
if (patched.length !== 2 || new Set(patched).size !== 2) throw Error('Expected exactly two server host literals');
writeFileSync(join(destination, relative), metadata);
writeFileSync(join(destination, 'private-build.json'), JSON.stringify({
  source, sourceMetadataSha256: before, metadataVersion: metadata.readUInt32LE(4),
  patchedHosts: patched, backend: 'https://127.0.0.1:443',
  tlsValidationBypass: { function: 'SecureTlsClient::NotifyServerCertificate', va: `0x${TLS_PATCH_VA.toString(16)}`, fileOffset: `0x${tlsPatchOffset.toString(16)}` },
  status: 'Development build; backend compatibility requires verification'
}, null, 2));
console.log(`Prepared desktop copy: ${destination}`);
