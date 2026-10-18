#!/usr/bin/env python3
"""Independent hashlib Digest verifier over real encrypted RTSP and audio.
All peers are loopback fixtures; this is not a real HomePod compatibility claim.
"""
import asyncio
import json
import sys
from receiver_simulator import Peer


async def main():
    dotnet, assembly = sys.argv[1:3]
    reports = []
    modes = ['paired', 'qop-auth', 'sha256-auth', 'md5-sess', 'sha256-sess', 'cache-reconnect',
             'single-paired', 'single-transient', 'reject-proof', 'fallback-accept', 'fallback-decline',
             'cancel-playback', 'malformed', 'unsupported-qop', 'late-refusal']
    for mode in modes:
        if len(sys.argv) > 3 and mode not in sys.argv[3:]:
            continue
        peers = [Peer('ptp-native-normal', '127.0.0.2'), Peer('ptp-native-normal', '127.0.0.3')]
        primary, secondary = peers
        secondary.requires_password = mode not in ['single-transient', 'cancel-playback']
        secondary.pairing_password = 'demo-secondary-secret' if secondary.requires_password else None
        secondary.digest_required = True
        if mode == 'qop-auth': secondary.digest_qop = 'auth-int, auth'
        if mode.startswith('sha256'): secondary.digest_algorithm = 'SHA-256-SESS' if mode.endswith('sess') else 'SHA-256'; secondary.digest_qop = 'auth'
        if mode == 'md5-sess': secondary.digest_algorithm = 'MD5-SESS'; secondary.digest_qop = 'auth'
        secondary.digest_reject = mode in ['reject-proof', 'fallback-accept', 'fallback-decline']
        secondary.digest_malformed = mode == 'malformed'
        if mode == 'unsupported-qop': secondary.digest_qop = 'auth-int'
        secondary.digest_late_reject = mode == 'late-refusal'
        if not mode.startswith('single-'):
            for peer in peers: peer.cohort = peers
        primary.allow_prepared_fallback = mode == 'fallback-accept'
        try:
            ports = [await p.start() for p in peers]
            process = await asyncio.create_subprocess_exec(dotnet, assembly, '--test-digest', *map(str, ports), mode,
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            out, err = await asyncio.wait_for(process.communicate(), 35)
            assert process.returncode == 0, (mode, out.decode(), err.decode(), [p.errors for p in peers])
            assert all(not p.errors for p in peers), (mode, [p.errors for p in peers])
            assert secondary.digest_challenges == (2 if mode == 'cache-reconnect' else 1), 'Repeated Digest challenge or new connection after refusal'
            assert secondary.methods.count('POST /pair-setup') == (2 if mode in ['single-transient', 'cancel-playback'] else 3), 'Digest triggered SRP/re-pair'
            assert secondary.methods.count('POST /pair-verify') == (0 if mode in ['single-transient', 'cancel-playback'] else 4 if mode == 'cache-reconnect' else 2), 'Digest triggered pair-verify retry'
            if mode in ['reject-proof', 'fallback-accept', 'fallback-decline']:
                assert secondary.digest_proofs == 1 and secondary.audio_packets == 0
                assert 'SET_PARAMETER /session' not in secondary.methods and 'RECORD /session' not in secondary.methods
                assert primary.methods.count('TEARDOWN /session') == (2 if mode == 'fallback-accept' else 1), 'Prepared leader session was not released'
                if mode == 'fallback-accept':
                    assert primary.methods.count('POST /pair-setup') == 4 and primary.audio_packets > 30 and primary.setup_payload['isMultiSelectAirPlay'] is False
                else: assert primary.audio_packets == 0 and 'SET_PARAMETER /session' not in primary.methods
            elif mode in ['cancel-playback', 'malformed', 'unsupported-qop']:
                assert secondary.digest_proofs == 0 and all(p.audio_packets == 0 for p in peers)
                assert primary.methods.count('TEARDOWN /session') == 1
            elif mode == 'late-refusal':
                assert all(p.audio_packets > 30 and 'TEARDOWN /session' not in p.methods for p in peers)
                assert secondary.feedback == 0 and secondary.digest_replied_requests.count('POST') == 1
                assert primary.volume == 20
            else:
                assert secondary.audio_packets > 30 and secondary.resends > 0
                assert {'SETUP', 'RECORD', 'SETPEERS', 'TEARDOWN'}.issubset(secondary.digest_replied_requests), 'Digest not applied to subsequent methods'
                if not mode.startswith('single-'):
                    shared = min(len(p.frames) for p in peers)
                    assert shared > 30 and primary.frames[:shared] == secondary.frames[:shared]
                assert primary.digest_proofs == 0
            reports.append({'mode': 'playback-digest-' + mode, 'passed': True, 'challenges': secondary.digest_challenges,
                            'proofs_verified': secondary.digest_proofs, 'member_packets': [p.audio_packets for p in peers]})
            print(mode + ': passed', file=sys.stderr)
        finally:
            for peer in peers: await peer.close()
            await asyncio.sleep(.1)
    print(json.dumps({'real_homepod': False, 'playback_digest_simulations': reports}, indent=2))


if __name__ == '__main__': asyncio.run(main())
