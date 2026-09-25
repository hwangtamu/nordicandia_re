/* Restore the online connector omitted from Android 1.9.3.
 * Keep the existing async state machine, but await InternalWebSocket.Connect via
 * UniTask.WaitUntil(Task.IsCompleted). Never report success before WS is open.
 * All calls execute on Unity's main thread. Addresses are linked, ASLR-safe VAs.
 */
typedef void *ptr;
typedef unsigned long u64;
typedef unsigned int u32;
typedef unsigned char u8;
typedef struct { u64 a, b; } Guid;
typedef struct { ptr source; u64 token; } UniTask;
extern ptr il2cpp_domain_get(void);
extern ptr il2cpp_domain_get_assemblies(ptr, u64 *);
extern ptr il2cpp_assembly_get_image(ptr);
extern ptr il2cpp_class_from_name(ptr, const char *, const char *);
extern ptr il2cpp_class_get_method_from_name(ptr, const char *, int);
extern ptr il2cpp_method_get_param(ptr, u32);
extern ptr il2cpp_class_from_type(ptr);
extern void il2cpp_runtime_class_init(ptr);
extern ptr il2cpp_object_new(ptr);
extern ptr il2cpp_runtime_invoke(ptr, ptr, ptr *, ptr *);
extern ptr il2cpp_string_new(const char *);
extern ptr il2cpp_class_get_field_from_name(ptr, const char *);
extern void il2cpp_field_static_set_value(ptr, ptr);
extern u32 il2cpp_gchandle_new(ptr, int);
extern void il2cpp_gchandle_free(u32);
extern void Internal_ctor(ptr, ptr, Guid, int, int, int, int, int, ptr);
extern ptr Internal_connect(ptr, int, ptr);
extern int Internal_is_connected(ptr, ptr);
extern ptr Internal_close(ptr, ptr, int, ptr);
extern int Task_is_completed(ptr, ptr);
extern UniTask WaitUntil(ptr, int, ptr, ptr);
extern UniTask NetSocket_update_fixed(float, ptr);
extern UniTask NetSocket_update(double, ptr);
extern ptr il2cpp_resolve_icall(const char *);
extern void Forget(UniTask, ptr);

ptr g_socket_class, g_internal_class, g_task_class, g_uni_class;
ptr g_socket, g_connect_task, g_wait_method;
UniTask g_wait;
u32 g_socket_root, g_wait_root;
int g_ready, g_error, g_connected;
u64 g_attempts, g_ticks;
Guid g_character;
#ifndef WS_HOST
#define WS_HOST "prod.038c3288.nip.io"
#endif
static ptr find(const char *ns, const char *name) {
    u64 n=0; ptr *a=il2cpp_domain_get_assemblies(il2cpp_domain_get(), &n);
    for(u64 i=0;i<n;i++) { ptr k=il2cpp_class_from_name(il2cpp_assembly_get_image(a[i]),ns,name); if(k)return k; }
    return 0;
}
static ptr method(ptr k,const char*n,int argc) { return k?il2cpp_class_get_method_from_name(k,n,argc):0; }
static ptr call(ptr k,const char*n,int argc,ptr self,ptr *args) {
    ptr m=method(k,n,argc),ex=0; if(!m){g_error=1;return 0;}
    ptr r=il2cpp_runtime_invoke(m,self,args,&ex); if(ex){g_error=2;return 0;}return r;
}
static int init(void) {
    if(g_ready)return 1;
    g_socket_class=find("Client.Net","NetSocket");
    g_internal_class=find("Client.Net","InternalWebSocket");
    g_task_class=find("System.Threading.Tasks","Task");
    g_uni_class=find("Cysharp.Threading.Tasks","UniTask");
    g_wait_method=method(g_uni_class,"WaitUntil",3);
    if(!g_socket_class||!g_internal_class||!g_task_class||!g_wait_method){g_error=3;return 0;}
    il2cpp_runtime_class_init(g_socket_class);
    il2cpp_runtime_class_init(g_internal_class);
    g_ready=1;return 1;
}
/* IL2CPP delegate constructors take a MethodInfo as their IntPtr argument,
 * NOT its method pointer. This also provides the correct static/instance thunk. */
static ptr delegate_for(ptr add_method, ptr target, ptr callback) {
    if(!add_method||!callback){g_error=4;return 0;}
    ptr k=il2cpp_class_from_type(il2cpp_method_get_param(add_method,0));
    ptr ctor=method(k,".ctor",2); if(!ctor){g_error=4;return 0;}
    ptr d=il2cpp_object_new(k);
    ((void(*)(ptr,ptr,ptr,ptr))(*(ptr*)ctor))(d,target,callback,ctor);
    return d;
}
static void bind(const char *event,const char *callback,int argc) {
    ptr add=method(g_internal_class,event,1);
    ptr d=delegate_for(add,0,method(g_socket_class,callback,argc));
    if(d)((void(*)(ptr,ptr,ptr))(*(ptr*)add))(g_socket,d,add);
}
void ws_prepare(Guid id,int show,int fast,ptr unused) {
    (void)show;(void)unused; g_attempts++;g_error=0;g_wait=(UniTask){0,0};
    if(!init())return;
    if(g_socket && g_character.a==id.a && g_character.b==id.b && Internal_is_connected(g_socket,0))return;
    if(g_socket)Internal_close(g_socket,il2cpp_string_new("Changing character"),5,0);
    if(g_socket_root){il2cpp_gchandle_free(g_socket_root);g_socket_root=0;}
    g_character=id;g_connected=0;
    g_socket=il2cpp_object_new(g_internal_class);
    g_socket_root=il2cpp_gchandle_new(g_socket,0);
    Internal_ctor(g_socket,il2cpp_string_new(WS_HOST),id,443,1,30,1,fast?1:5,0);
    /* For reference fields this API takes the object itself, not &object. */
    il2cpp_field_static_set_value(il2cpp_class_get_field_from_name(g_socket_class,"_Socket"),g_socket);
    bind("add_Connected","Socket_Connected",0);
    bind("add_Closed","Socket_Closed",2);
    int yes=1;ptr a[]={&yes};call(g_socket_class,"set_IsConnecting",1,0,a);
    g_connect_task=Internal_connect(g_socket,20,0);
    ptr predicate=delegate_for(g_wait_method,g_connect_task,method(g_task_class,"get_IsCompleted",0));
    if(!predicate){g_error=4;return;}
    /* Update timing (8), default CancellationToken. Connect owns its timeout.
     * The MoveNext awaiter (sp+0x20) keeps the source alive, so no extra
     * gchandle is needed - and freeing one here raced the GC and crashed in
     * il2cpp_gchandle_get_target. */
    g_wait=WaitUntil(predicate,8,0,g_wait_method);
}
int ws_finish(void) {
    int no=0;ptr a[]={&no};
    if(g_ready)call(g_socket_class,"set_IsConnecting",1,0,a);
    g_connected=!g_error && g_socket && Internal_is_connected(g_socket,0);
    return g_connected;
}
/* Retail UnityGame.FixedUpdate's state machine is an empty completed method.
 * Restore the existing game progress pump; it contains its own rate limiting,
 * in-game checks, semaphores and acknowledged delta accounting. */
void ws_fixed_update(ptr unused,ptr mi) {
    (void)unused;(void)mi;
    static float (*fixed_delta)(void);
    if(!fixed_delta)fixed_delta=il2cpp_resolve_icall("UnityEngine.Time::get_fixedDeltaTime()");
    if(!fixed_delta)return;
    float dt=fixed_delta();
    g_ticks++;
    Forget(NetSocket_update_fixed(dt,0),0);
    Forget(NetSocket_update((double)dt,0),0);
}
__attribute__((naked)) void ws_connect_trampoline(void) {
    __asm__ volatile("stp x29,x30,[sp,#-16]!\nbl ws_prepare\nldp x29,x30,[sp],#16\nsub sp,sp,#0x60\nb CONNECT_RESUME\n");
}
__attribute__((naked)) void ws_wait_trampoline(void) {
    __asm__ volatile("adrp x8,g_wait\nadd x8,x8,:lo12:g_wait\nldr q0,[x8]\nstr q0,[sp,#0x20]\nb WAIT_RESUME\n");
}
__attribute__((naked)) void ws_result_trampoline(void) {
    /* ValueTuple<bool,bool,bool> = (connected, playOffline, maintenance).
     * These are result values, not awaiter flags. Returning (false,true,false)
     * diverts the caller to SignInNew(LocalDevice) and loses online progress. */
    __asm__ volatile("stp x0,x30,[sp,#-16]!\n"
                     "bl ws_finish\n"
                     "mov w9,w0\n"
                     "ldp x0,x30,[sp],#16\n"
                     "strb wzr,[sp,#0x22]\n"
                     "strh wzr,[sp,#0x20]\n"
                     "strb w9,[sp,#0x3c]\n"
                     "strb wzr,[sp,#0x38]\n"
                     "strb wzr,[sp,#0x34]\n"
                     "adrp x8,TUPLE_MI\n"
                     "add x8,x8,:lo12:TUPLE_MI\n"
                     "ldr x8,[x8]\n"
                     "ldr x4,[x8]\n"
                     "b RESULT_RESUME\n");
}
