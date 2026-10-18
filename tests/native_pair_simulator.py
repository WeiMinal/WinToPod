#!/usr/bin/env python3
"""Two independently authenticated TCP/UDP receiver peers; no Apple hardware.
Asserts shared content/timeline/identity, leader-only volume and group failure.
"""
import asyncio
import json
import sys
from receiver_simulator import Peer


async def main():
    dotnet, assembly = sys.argv[1:3]
    results = []
    for mode in ['setup-rejected', 'fallback-setup-rejected', 'in-session-test', 'normal', 'no-clock', 'reject-stream', 'interrupt', 'clock-loss', 'volume-reject', 'volume-change', 'reject-auth',
                 'fallback-accept', 'fallback-decline', 'fallback-cancel', 'fallback-volume-reject', 'fallback-remembered',
                 'password-required', 'password-cancel', 'password-cancel-token', 'password-supplied', 'password-both', 'password-live-only',
                 'no-password', 'wrong-password', 'wrong-password-no-delay', 'fallback-wrong-password', 'fallback-wrong-password-no-delay',
                 'bad-m6-signature', 'bad-verify-signature', 'wrong-verify-id', 'reject-verify', 'cache-reconnect', 'cache-verify-rejected', 'reject-m6', 'reject-verify-no-delay', 'mute-floor', 'mute-stale', 'mute-zero', 'mute-exact']:
        if len(sys.argv) > 3 and mode not in sys.argv[3:]:
            continue
        leader_mode = 'ptp-native-' + mode if mode in ['volume-reject', 'volume-change'] or mode.startswith('mute-') else 'ptp-native-normal'
        if mode == 'fallback-volume-reject':
            leader_mode = 'ptp-native-volume-reject'
        secondary_mode = 'ptp-native-' + mode if mode in ['no-clock', 'reject-stream', 'interrupt', 'clock-loss', 'reject-auth'] else 'ptp-native-normal'
        if mode.startswith('fallback-'):
            secondary_mode = 'ptp-native-reject-auth'
        if mode in ['setup-rejected', 'fallback-setup-rejected']:
            secondary_mode = 'ptp-native-setup-rejected'
        if mode in ['wrong-password', 'fallback-wrong-password']:
            secondary_mode = 'ptp-native-wrong-password'
        if mode in ['wrong-password-no-delay', 'fallback-wrong-password-no-delay']:
            secondary_mode = 'ptp-native-wrong-password-no-delay'
        peers = [Peer(leader_mode, '127.0.0.2'), Peer(secondary_mode, '127.0.0.3')]
        peers[1].requires_password = mode != 'no-password'
        peers[1].pairing_password = 'demo-secondary-secret' if mode != 'no-password' else None
        peers[1].bad_m6_signature = mode == 'bad-m6-signature'
        peers[1].bad_verify_signature = mode == 'bad-verify-signature'
        peers[1].wrong_verify_id = mode == 'wrong-verify-id'
        peers[1].reject_verify = mode in ['reject-verify', 'reject-verify-no-delay']
        peers[1].reject_m6 = mode == 'reject-m6'
        peers[1].verify_without_delay = mode == 'reject-verify-no-delay'
        peers[1].reject_cached_verify = mode == 'cache-verify-rejected'
        if mode == 'password-both':
            peers[0].requires_password = True
            peers[0].pairing_password = 'demo-primary-secret'
        for peer in peers:
            peer.cohort = peers
        peers[0].allow_single_fallback = mode in ['fallback-accept', 'fallback-volume-reject', 'fallback-remembered', 'fallback-setup-rejected', 'fallback-wrong-password', 'fallback-wrong-password-no-delay']
        try:
            ports = [await peer.start() for peer in peers]
            process = await asyncio.create_subprocess_exec(dotnet, assembly, '--test-native-pair', *map(str, ports), mode,
                                                           stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            out, err = await asyncio.wait_for(process.communicate(), 35)
            assert process.returncode == 0, (mode, out.decode(), err.decode())
            assert all(not p.errors for p in peers), (mode, [p.errors for p in peers])
            primary, secondary = peers
            assert primary.methods.count('POST /pair-setup') == (3 if mode == 'password-both' else 4 if mode.startswith('cache-') else 2), 'Unexpected leader authentication retry'
            secondary_setup = 0 if mode in ['fallback-remembered', 'password-required', 'password-cancel', 'password-cancel-token'] else 1 if mode in ['setup-rejected', 'fallback-setup-rejected'] else 2 if mode in ['no-password', 'reject-auth', 'wrong-password', 'wrong-password-no-delay'] or mode.startswith('fallback-') else 3
            assert secondary.methods.count('POST /pair-setup') == secondary_setup, 'Unexpected password setup retry'
            assert all(p.methods.count('POST /pair-pin-start') == 0 for p in peers), 'Removed pairing-start guess was sent'
            secondary_verify = 0 if secondary_setup < 3 or mode in ['bad-m6-signature', 'reject-m6'] else 1 if mode in ['bad-verify-signature', 'wrong-verify-id', 'reject-verify', 'reject-verify-no-delay'] else 4 if mode == 'cache-reconnect' else 3 if mode == 'cache-verify-rejected' else 2
            assert secondary.methods.count('POST /pair-verify') == secondary_verify, 'Unexpected verify/re-pair retry'
            assert len(set(primary.pairing_client_ids + secondary.pairing_client_ids)) == 1, 'Sender identity differs between members or pairing steps'
            if mode == 'cache-verify-rejected':
                for peer in peers:
                    tail = peer.methods[len(peer.methods) - 1 - peer.methods[::-1].index('GET /info'):]
                    assert not any(m.startswith(('SETUP ', 'SET_PARAMETER ', 'TEARDOWN ')) for m in tail), 'Failed verification acquired playback'
                results.append({'mode':'native-pair-' + mode,'passed':True,'cached_pairing_retained':True,'no_password_repair':True})
                continue
            if mode in ['setup-rejected', 'reject-auth', 'fallback-decline', 'fallback-cancel', 'password-required', 'password-cancel', 'password-cancel-token', 'wrong-password', 'wrong-password-no-delay', 'bad-m6-signature', 'bad-verify-signature', 'wrong-verify-id', 'reject-verify', 'reject-m6', 'reject-verify-no-delay']:
                assert all(p.audio_packets == 0 and not p.ptp_active for p in peers)
                assert all(not any(m.startswith(('SETUP ', 'RECORD ', 'SETPEERS ', 'SET_PARAMETER ', 'TEARDOWN ')) for m in p.methods) for p in peers)
                results.append({'mode': 'native-pair-' + mode, 'passed': True, 'member_packets': [0, 0],
                                'before_setup': True, 'retry_delay_seconds': 4 if mode in ['reject-auth', 'fallback-decline', 'fallback-cancel', 'wrong-password'] else None})
                continue
            if mode in ['fallback-accept', 'fallback-volume-reject', 'fallback-remembered', 'fallback-setup-rejected', 'fallback-wrong-password', 'fallback-wrong-password-no-delay']:
                assert not any(m.startswith(('SETUP ', 'RECORD ', 'SETPEERS ', 'SET_PARAMETER ', 'TEARDOWN ')) for m in secondary.methods), 'Fallback controlled rejected member'
                assert secondary.audio_packets == 0 and not secondary.ptp_active
                assert primary.setup_payload['isMultiSelectAirPlay'] is False
                if mode == 'fallback-volume-reject':
                    assert primary.audio_packets == 0
                else:
                    assert primary.audio_packets > 30 and primary.resends > 0
                    # Independently inspect ALAC escape frame sample bits.
                    bits = ''.join(f'{b:08b}' for b in primary.frames[0][1])
                    at = 55
                    for _ in range(352):
                        assert bits[at:at+16] == bits[at+16:at+32], 'Fallback dropped one stereo channel'
                        at += 32
                results.append({'mode': 'native-pair-' + mode, 'passed': True,
                                'member_packets': [p.audio_packets for p in peers], 'secondary_not_retried': True})
                continue
            assert primary.audio_key != secondary.audio_key, 'Members must have independent session keys'
            if mode == 'password-both':
                assert primary.controller_keys == secondary.controller_keys, 'Same controller ID used different signing keys'
            assert primary.setup_payload['groupUUID'] == secondary.setup_payload['groupUUID']
            assert primary.sender_clock == secondary.sender_clock
            assert all(p.setup_payload['isMultiSelectAirPlay'] for p in peers)
            assert 'SET_PARAMETER /session' not in secondary.methods and 'GET_PARAMETER /session' not in secondary.methods
            if mode in ['no-clock', 'reject-stream', 'volume-reject']:
                assert all(p.audio_packets == 0 for p in peers), 'Partial group emitted audio'
                if mode != 'volume-reject':
                    assert 'SET_PARAMETER /session' not in primary.methods
            else:
                shared = min(len(p.frames) for p in peers)
                assert shared > 30
                assert primary.frames[:shared] == secondary.frames[:shared], 'Stereo frame or RTP timeline differs'
                assert primary.anchors == secondary.anchors, 'Clock mapping differs'
                assert all(p.resends > 0 for p in peers)
                if mode in ['interrupt', 'clock-loss']:
                    assert all('TEARDOWN /session' not in p.methods for p in peers), 'Control after loss'
                    assert primary.volume == 20, 'Volume changed after loss'
                if mode == 'volume-change':
                    assert primary.volume == 37
            if mode.startswith('mute-'):
                def muted_samples(frame):
                    bits = ''.join(f'{b:08b}' for b in frame[1])
                    return [int(bits[at:at+16], 2) for at in range(55, 55 + 352*32, 16)]
                assert all(v == 0 for frame in primary.frames[45:110] for v in muted_samples(frame)), 'Muted sender emitted nonzero PCM'
                assert any(v != 0 for frame in primary.frames[155:175] for v in muted_samples(frame)), 'Unmute did not restore PCM'
                assert primary.volume == 37 and 'TEARDOWN /session' in primary.methods
                assert len(primary.pairing_client_ids) == 2 and len(secondary.pairing_client_ids) == 5, 'Mute reopened pairing/session'
            if mode == 'in-session-test':
                def samples(frame):
                    bits = ''.join(f'{b:08b}' for b in frame[1])
                    return [(int(bits[at:at+16], 2), int(bits[at+16:at+32], 2)) for at in range(55, 55 + 352*32, 32)]
                # Frame 32 starts the test, 1.5 seconds per segment. Inspect the
                # decrypted receiver-side ALAC, after network encryption/RTP.
                for frame_at, left_on, right_on in [(45, True, False), (235, False, True), (425, True, True)]:
                    decoded = samples(primary.frames[frame_at])
                    assert any(l != 0 for l, r in decoded) == left_on
                    assert any(r != 0 for l, r in decoded) == right_on
                after = samples(primary.frames[620])
                assert all(l == 620 and r == ((-1000-620) & 65535) for l, r in after), 'Original live audio did not resume'
                assert len(primary.pairing_client_ids) == 2 and len(secondary.pairing_client_ids) == 5, 'Live test re-authenticated'
            results.append({'mode': 'native-pair-' + mode, 'passed': True,
                            'member_packets': [p.audio_packets for p in peers], 'distinct_keys': True,
                            'shared_stereo_and_anchor': mode not in ['no-clock', 'reject-stream', 'volume-reject']})
            print(mode + ': passed', file=sys.stderr)
        finally:
            for peer in peers:
                await peer.close()
            await asyncio.sleep(.1)
    print(json.dumps({'real_homepod': False, 'native_pair_simulations': results}, indent=2))


if __name__ == '__main__':
    asyncio.run(main())
