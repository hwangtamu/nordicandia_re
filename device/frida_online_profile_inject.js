'use strict';
function main(){
  var base=null,t=0;
  while(base===null){try{base=Process.getModuleByName('libil2cpp.so').base;}catch(e){} if(base===null){t++;if(t>1200){return;}Thread.sleep(0.05);}}
  function L(o){return base.add(o);}
  function bytesOf(h){var b=[];for(var i=0;i<h.length;i+=2)b.push(parseInt(h.substr(i,2),16));return b;}
  function patch(va,hx){var a=base.add(va);var b=bytesOf(hx);Memory.protect(a,b.length,'rwx');a.writeByteArray(b);console.log('patched '+va.toString(16));}
  function readIl2cppStr(p){try{if(p.isNull())return null;var len=p.add(0x10).readS32();if(len<0||len>512)return '<len '+len+'>';return p.add(0x14).readUtf16String(len);}catch(e){return '<err>';}}

  patch(0x026FB754,"1f2003d5"); // NOP "Cannot synchronize online account"

  var pa=null;
  Interceptor.attach(L(0x02C0AEA4),{onEnter:function(a){ pa=a[0]; }}); // PlayerAccount.get_IsOnline
  var getAccountId=new NativeFunction(L(0x02C0C648),'pointer',['pointer']);
  var importFn=new NativeFunction(L(0x02E552F8),'pointer',['pointer','uint64']); // static ImportFromDeployedSurfaceAsync(string,DateTime)

  Interceptor.attach(L(0x02E54FEC),{onEnter:function(args){
    try{
      var accId = pa!==null ? getAccountId(pa) : null;
      console.log('>> TryImportAsync accountId='+readIl2cppStr(accId));
      var ms = Date.now();
      var ticks = (BigInt(ms) + BigInt(62135596800000)) * BigInt(10000);
      var dt = uint64('0x'+ticks.toString(16));
      var r = importFn(accId, dt);
      console.log('   ImportFromDeployedSurfaceAsync -> x0='+r);
    }catch(e){console.log('ERR '+e);}
  }});
  Interceptor.attach(L(0x02E552F8),{onEnter:function(a){console.log('   [in] ImportFromDeployedSurface arg1='+readIl2cppStr(a[1]));}});
  Interceptor.attach(L(0x02E51FAC),{onEnter:function(){console.log('   [in] PullAsync');}});
  Interceptor.attach(L(0x02E556AC),{onEnter:function(){console.log('   [in] WriteOnlineProfile');}});
  Interceptor.attach(L(0x02E30494),{onEnter:function(){console.log('   [in] ImportOnlineProfile');}});
  Interceptor.attach(L(0x025D4308),{onEnter:function(a){console.log('   [in] SaveAccountToLocalDevice_MessagePack acct='+readIl2cppStr(a[1]));}});
  Interceptor.attach(L(0x025D4AB8),{onEnter:function(a){console.log('   [in] SaveCharacterToLocalDevice_MessagePack acct='+readIl2cppStr(a[1]));}});
  console.log('[+] ready');
}
setTimeout(main,0);