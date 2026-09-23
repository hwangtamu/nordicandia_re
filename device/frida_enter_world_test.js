'use strict';
function main(){
  var base=null,t=0;
  while(base===null){try{base=Process.getModuleByName('libil2cpp.so').base;}catch(e){} if(base===null){t++;if(t>1200){return;}Thread.sleep(0.05);}}
  function L(o){return base.add(o);}
  function bytesOf(h){var b=[];for(var i=0;i<h.length;i+=2)b.push(parseInt(h.substr(i,2),16));return b;}
  function patch(va,hx,l){var a=base.add(va);var b=bytesOf(hx);Memory.protect(a,b.length,'rwx');a.writeByteArray(b);console.log('patch '+va.toString(16)+' '+l);}
  patch(0x026FB754,"1f2003d5","NOP assert");
  patch(0x0257D29C,"1f2003d5","force branch to 0x257E610 (offline block)");
  function h(a,n){try{Interceptor.attach(L(a),{onEnter:function(){console.log('>> '+n);}});}catch(e){}}
  h(0x02577A98,'_OnPlayClicked_b__0_d.MoveNext (world entry)');
  h(0x02C07B3C,'Player.EnterGame');
  h(0x02C0AF10,'Player._EnterGame_d__58.MoveNext');
  h(0x0257E610,'offline block 0x257E610');
  h(0x026FC034,'b__5_d SUCCESS');
  console.log('[+] ready');
}
setTimeout(main,0);