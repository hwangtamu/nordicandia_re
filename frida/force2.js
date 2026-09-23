'use strict';
var m=Process.findModuleByName('libil2cpp.so'); var base=m.base;
console.log('[frida] base',base);
function il2str(s){try{var len=s.add(0x10).readS32();if(len<0||len>4000)return '<len '+len+'>';return s.add(0x14).readUtf16String(len);}catch(e){return '<err>';}}
var O={cctor:0x2daea68,ctor:0x2dae914,Init:0x2da1f0c,SignInWithDevice:0x2da21b8,Raise:0x2318168,
       Login:0x26f2434,SignInNew:0x2701390,ErrBranch:0x26f6708,GetCurrent:0x2DA1E28,OnLoggedInOnline:0x26f2158};
function hook(off,name,cb){ try{Interceptor.attach(base.add(off),cb); console.log('[hook]',name);}catch(e){console.log('[hook fail]',name,e);} }
hook(O.cctor,'NetClient..cctor',{onEnter:function(a){console.log('>>> NetClient..cctor');}});
hook(O.ctor,'NetClient..ctor',{onEnter:function(a){console.log('>>> NetClient..ctor this=',a[0]);}});
hook(O.Init,'NetClient.Initialize',{onEnter:function(a){console.log('>>> NetClient.Initialize this=',a[0]);}});
hook(O.SignInWithDevice,'NetClient.SignInWithDevice',{onEnter:function(a){console.log('>>> SignInWithDevice this=',a[0],'b=',a[1].toInt32());}});
hook(O.Raise,'il2cpp_raise_exception',{onEnter:function(a){try{var msg=a[0].add(0x10).readPointer();console.log('>>> EXC',msg.isNull()?'<null>':il2str(msg));var bt=Thread.backtrace(this.context,Backtracer.ACCURATE).map(function(x){var s=DebugSymbol.fromAddress(x);return (s.name||s.moduleName||'?')+'@'+x;});console.log('    bt: '+bt.join(' <- '));}catch(e){}}});
hook(O.ErrBranch,'login error-branch',{onEnter:function(a){console.log('>>> LOGIN ERROR BRANCH (Current null)');}});
hook(O.OnLoggedInOnline,'OnLoggedInOnline',{onEnter:function(a){console.log('>>> OnLoggedInOnline type=',a[1].toInt32());}});
hook(O.Login,'Login(force Device)',{onEnter:function(a){console.log('>>> Login this=',a[0],'type=',a[1].toInt32(),'->1'); a[1]=ptr(1);}});
hook(O.SignInNew,'UnityGame.SignInNew(force Device)',{onEnter:function(a){console.log('>>> UnityGame.SignInNew type=',a[0].toInt32(),'->1'); a[0]=ptr(1);}});
console.log('[frida] FORCE2 ready');
