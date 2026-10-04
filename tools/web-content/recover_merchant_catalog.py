#!/usr/bin/env python3
"""Recover the 24 LocalMerchantCatalogItem constructor arguments in Android's offline catalog.
This is an offline baseline, not a production-server price dump. Pointer values are tracked
through GOT literal relocations; volatile registers are discarded after calls.
"""
import sys,json,re,hashlib
from pathlib import Path
import disasm_powers as n
from resolve_il2cpp_strings import build_got_map
from capstone import *
d=n.SO.read_bytes(); assert hashlib.sha256(d).hexdigest()=="529bd257af0cd6e953fb50f51a69857f42f04eb70acab0586f8413d1f97afd1d"
segs=n.load_segments(d); got=build_got_map();regs={};out=[]
def reg(r):return 'x'+r[1:] if r.startswith('w') else r
def val(s):
 if s in ['xzr','wzr']:return 0
 if s.startswith('#'):return int(s[1:],0)
 return regs.get(reg(s))
md=Cs(CS_ARCH_ARM64,CS_MODE_LITTLE_ENDIAN)
for i in md.disasm(d[n.va_to_off(segs,0x41cde04):n.va_to_off(segs,0x41cf184)],0x41cde04):
 op=i.op_str.split(', '); k=reg(op[0]); v=None
 if i.mnemonic in ['adrp','mov']: v=val(op[1]);regs[k]=v
 elif i.mnemonic=='movk':
  shift=int(op[2].split('#')[1]) if len(op)>2 else 0;old=regs.get(k,0)
  regs[k]=(old&~(65535<<shift))|(val(op[1])<<shift) if isinstance(old,int) else None
 elif i.mnemonic=='ldr':
  m=re.search(r'\[(\w+)(?:, #(0x[\da-f]+))?\]',i.op_str)
  base=val(m[1]) if m else None;off=int(m[2] or '0',16) if m else 0
  if isinstance(base,int) and base+off in got:v=('literal',got[base+off])
  elif isinstance(base,tuple) and base[0]=='literal' and off==0:v=base[1]
  regs[k]=v
 elif i.mnemonic=='bl':
  if i.op_str=='#0x41cdb60':out.append(dict(address=hex(i.address),name=regs.get('x1'),stacks=regs.get('x4'),currency=regs.get('x5'),price=regs.get('x6')))
  for r in range(19):regs.pop('x'+str(r),None)
assert len(out)==24 and all(isinstance(x['name'],str) and x['currency'] in ('SL','OP') and x['stacks']==1 and x['price']>0 for x in out)
assert len({(x['name'], x['currency']) for x in out}) == 24
for path in [n.ROOT/'tools/web-content/generated/offline_merchant_prices.json', n.ROOT/'server/Nordicandia.Server/GameData/offline_merchant_prices.json']:
 path.write_text(json.dumps(out,indent=2)+'\n')
print('Recovered 24 offline merchant prices (12 products × 2 currencies).')
