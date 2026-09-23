#!/usr/bin/env python3
"""Patch the original Nordicandia 1.9.3 ARM64 libil2cpp for device online login.

Pass the extracted original library and a separate output path. Repackage/sign
with patch_xapk.py afterward. Addresses are virtual addresses, not file offsets.

Patches applied:
  * force the Device login provider (instead of Offline),
  * call NetClient.Initialize during SignInWithDevice,
  * initialize the default MagicOnion GrpcChannelProvider on demand,
  * trust the private server's self-signed TLS certificate,
  * accept the HTTP/1.1 gRPC response produced by Envoy's grpc_http1_bridge
    (skip grpc-dotnet's HTTP/2 version check and missing grpc-status trailer),
  * un-hide the Game Mode "Season" card and allow Next when it is selected.

The HTTP/1.1 acceptance patches require the server to expose a gRPC bridge
(Envoy ``grpc_http1_bridge``) so the Mono HTTP/1.1 handler can reach Kestrel h2.
"""
import argparse
import hashlib
import struct
from pathlib import Path


def patch(data):
    if hashlib.sha256(data).hexdigest() != "529bd257af0cd6e953fb50f51a69857f42f04eb70acab0586f8413d1f97afd1d":
        raise ValueError("Expected the original 1.9.3 ARM64 library; refusing unknown/already-patched input")
    result = bytearray(data)
    segments = []
    phoff = struct.unpack_from("<Q", data, 32)[0]
    entsize, count = struct.unpack_from("<HH", data, 54)
    for i in range(count):
        typ, flags, off, va, _, size, _, _ = struct.unpack_from("<IIQQQQQQ", data, phoff + i * entsize)
        if typ == 1 and flags & 1:
            segments.append((va, off, size))

    def replace(va, old, new):
        off = next(off + va - start for start, off, size in segments if start <= va < start + size)
        assert result[off:off + len(old)] == old, f"Unexpected instructions at {va:#x}"
        assert len(old) == len(new)
        result[off:off + len(old)] = new

    # Device instead of Offline for startup, Play, Create, and socket reconnect.
    for va, reg in [(0x25bffd4, 1), (0x257e454, 0), (0x2632ab4, 0), (0x2e12e8c, 0)]:
        replace(va, struct.pack("<I", 0x2a1f03e0 | reg), struct.pack("<I", 0x52800020 | reg))

    # Inside SignInWithDevice.MoveNext's existing exception-handling region:
    # replace the uninitialized-client throw guard with Initialize(this).
    # x20 is the NetClient instance, preserved by Initialize (AAPCS64).
    # Initialize itself returns immediately when already initialized.
    call_va, target = 0x2df5914, 0x2da1f0c
    bl = 0x94000000 | (((target - call_va) // 4) & 0x3ffffff)
    replace(0x2df5910, bytes.fromhex("88424039280b0034"), struct.pack("<II", 0xaa1403e0, bl))

    # Accept the self-signed certificate of the private server.
    # Mono.Net.Security.MobileTlsContext.ValidateCertificate -> mov w0,#1 ; ret
    replace(0x04326344, bytes.fromhex("fe0f1ff8e80300aa"), bytes.fromhex("20008052c0035fd6"))

    # --- gRPC over the HTTP/1.1 transport ---
    # The Unity Mono HTTP handler (MonoWebRequestHandler) only speaks HTTP/1.1,
    # so the server's h2 gRPC is bridged back to HTTP/1.1 by Envoy's
    # grpc_http1_bridge. grpc-dotnet then hard-rejects both the downgraded
    # protocol and the missing grpc-status trailer; bypass both checks.
    # 1) Grpc.Net.Client.Internal.GrpcCall.ValidateHeaders:
    #    branch around the "Response protocol downgraded to HTTP/<n>" error.
    #    tbz w0,#0,0x3e58cb4  ->  b 0x3e58cb4
    replace(0x3E58C28, bytes.fromhex("60040036"), bytes.fromhex("23000014"))
    # 2) Grpc.Net.Client.Internal.GrpcCall._RunCall:
    #    HTTP/1.1 cannot carry the grpc-status trailer; when the status lookup
    #    is empty force the success path instead of "No grpc-status found".
    #    cbz w22,0x3d9773c  ->  b 0x3d9773c
    replace(0x3D974E4, bytes.fromhex("d6120034"), bytes.fromhex("96000014"))

    # --- Season mode UI ---
    # The retail client never fetches season info (NetClient.GetSeasonInfo and
    # OnlineData.set_SeasonInfo have no call sites in this build), yet
    # WindowSelectGameMode.Start still defaults _SelectedGameMode to Season
    # because the Season frame's serialized MainToggle.isOn is true. The frame is
    # hidden and Next is disabled for Season/Challenge, so the player can never
    # choose Season. Re-show the card and allow Next for Season, so a season
    # character can be created through the normal New-character flow.
    # 1) WindowSelectGameMode.Awake / Start: SetActive(SeasonFrame, false) -> true
    #    mov w1, wzr  ->  mov w1, #1
    replace(0x26334B4, bytes.fromhex("e1031f2a"), bytes.fromhex("21008052"))
    replace(0x263386C, bytes.fromhex("e1031f2a"), bytes.fromhex("21008052"))
    # 2) WindowSelectGameMode.UpdateButtonStatus: Next.interactable is
    #    HasValue && !IsSeason && !IsChallenge; drop the IsSeason term.
    #    orr w9, w20, w0  ->  mov w9, w20
    replace(0x2633D5C, bytes.fromhex("8902002a"), bytes.fromhex("e903142a"))

    # MagicOnion.Unity.GrpcChannelProvider.get_Default is never assigned on
    # this build because the GrpcChannelProviderHost scene object is absent
    # when online login is forced from a cold start. Redirect get_Default into
    # a stub (overwriting an unused BestHTTP sample function) that constructs
    # the default GrpcNetClient provider on demand.
    replace(0x3E750E0, bytes.fromhex("00b900b0"), bytes.fromhex("c766d717"))
    replace(
        0x344EBFC,
        bytes.fromhex("ffc302d1fd7b05a9fc6f06a9fa6707a9f85f08a9f65709a9f44f0aa9153b01d0f40301aaf30300aa"),
        bytes.fromhex("fd7bbfa9e0031faa8ea62894880240f9085d40f9000140f9fd7bc1a8f44f41a9fe0742f8c0035fd6"),
    )
    return bytes(result)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.source.resolve() == args.output.resolve():
        parser.error("Use a separate output path to preserve the original")
    args.output.write_bytes(patch(args.source.read_bytes()))
    print(hashlib.sha256(args.output.read_bytes()).hexdigest(), args.output)
