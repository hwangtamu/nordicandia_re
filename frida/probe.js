'use strict';
var m=Process.findModuleByName('libil2cpp.so'); var base=m.base;
console.log('[frida] base',base);
function il2str(s){try{var len=s.add(0x10).readS32();if(len<0||len>4000)return '<len '+len+'>';return s.add(0x14).readUtf16String(len);}catch(e){return '<err>';}}
try{
  var getCurrent=new NativeFunction(base.add(0x2DA1E28),'pointer',[]);
  var c=getCurrent();
  console.log('[probe] NetClient.Current =', c);
  if(!c.isNull()){ try{console.log('  IsInitialized =', c.add(0x10).readU8());}catch(e){console.log('  read err',e);} }
}catch(e){ console.log('[probe] getCurrent threw:', e); }
Interceptor.attach(base.add(0x2daea68),{onEnter:function(a){console.log('[frida] NetClient..cctor RUNS');}});
Interceptor.attach(base.add(0x2dae914),{onEnter:function(a){console.log('[frida] NetClient..ctor RUNS');}});
Interceptor.attach(base.add(0x2dae83c),{onEnter:function(a){console.log('[frida] NetClient.Destroy RUNS this=',a[0]);}});
// error branch in login MoveNext
Interceptor.attach(base.add(0x26f6708),{onEnter:function(a){console.log('[frida] >>> LOGIN ERROR BRANCH (Client not initialized) at 0x26f6708');}});
Interceptor.attach(base.add(0x2da21b8),{onEnter:function(a){console.log('[frida] >>> SignInWithDevice this=',a[0],'arg1=',a[1].toInt32());}});
console.log('[frida] probe installed');
