#!/usr/bin/env python3
"""Independent loopback peer for the .NET sender: srptools + cryptography + plistlib.
No Apple receiver or copied AirPlay engine. Dependencies are test-only.
Usage: python receiver_simulator.py /path/to/dotnet /path/to/WinToPod.Checks.dll
"""
import asyncio
import hashlib
import hmac
import json
import plistlib
import struct
import sys
from urllib.request import parse_http_list, parse_keqv_list
import time
import uuid
from pathlib import Path

from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from srptools import SRPContext, SRPServerSession
from srptools.constants import PRIME_3072, PRIME_3072_GEN


def tlv(values):
    result = bytearray()
    for key, value in values:
        if not value:
            result.extend((key, 0))
        for offset in range(0, len(value), 255):
            part = value[offset:offset + 255]
            result.extend((key, len(part)))
            result.extend(part)
    return bytes(result)


def untlv(data):
    result = {}
    offset = 0
    while offset < len(data):
        tag, size = data[offset:offset + 2]
        offset += 2
        assert offset + size <= len(data)
        result[tag] = result.get(tag, b'') + data[offset:offset + size]
        offset += size
    return result


def control_key(secret, info):
    return HKDF(algorithm=hashes.SHA512(), length=32, salt=b'Control-Salt', info=info).derive(secret)


def pairing_key(secret, salt, info):
    return HKDF(algorithm=hashes.SHA512(), length=32, salt=salt.encode(), info=info.encode()).derive(secret)


def raw_public(key):
    return key.public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)


def pairing_encrypt(key, label, plain):
    return ChaCha20Poly1305(key).encrypt(b'\0'*4 + label.encode(), plain, b'')


def pairing_decrypt(key, label, encrypted):
    return ChaCha20Poly1305(key).decrypt(b'\0'*4 + label.encode(), encrypted, b'')


class Wire:
    def __init__(self, reader, writer):
        self.reader, self.writer = reader, writer
        self.encrypt = self.decrypt = None
        self.input_nonce = self.output_nonce = 0
        self.buffer = bytearray()

    async def fill(self):
        if self.decrypt is None:
            self.buffer.extend(await self.reader.readexactly(1))
        else:
            head = await self.reader.readexactly(2)
            size, = struct.unpack('<H', head)
            assert size <= 1024
            block = await self.reader.readexactly(size + 16)
            nonce = b'\0' * 4 + struct.pack('<Q', self.input_nonce)
            self.input_nonce += 1
            self.buffer.extend(self.decrypt.decrypt(nonce, block, head))

    async def request(self):
        while b'\r\n\r\n' not in self.buffer:
            await self.fill()
        at = self.buffer.index(b'\r\n\r\n')
        lines = bytes(self.buffer[:at]).decode().split('\r\n')
        del self.buffer[:at + 4]
        method, path, version = lines[0].split(' ')
        assert version == 'RTSP/1.0'
        headers = dict(line.split(': ', 1) for line in lines[1:])
        size = int(headers.get('Content-Length', '0'))
        while len(self.buffer) < size:
            await self.fill()
        body = bytes(self.buffer[:size])
        del self.buffer[:size]
        return method, path, headers, body

    async def response(self, headers, body=b'', extra=None, status='200 OK'):
        result = {'CSeq': headers['CSeq'], 'Content-Length': str(len(body))}
        result.update(extra or {})
        payload = ('RTSP/1.0 ' + status + '\r\n' + ''.join(f'{k}: {v}\r\n' for k, v in result.items()) + '\r\n').encode() + body
        if self.encrypt is None:
            self.writer.write(payload)
        else:
            for offset in range(0, len(payload), 1024):
                block = payload[offset:offset + 1024]
                head = struct.pack('<H', len(block))
                nonce = b'\0' * 4 + struct.pack('<Q', self.output_nonce)
                self.output_nonce += 1
                self.writer.write(head + self.encrypt.encrypt(nonce, block, head))
        await self.writer.drain()


class Udp(asyncio.DatagramProtocol):
    def __init__(self, owner, kind):
        self.owner, self.kind = owner, kind

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        try:
            if self.kind == 'data':
                assert self.owner.confirmed or self.owner.cohort and self.owner.cohort[0].confirmed, 'audio before volume confirmation'
                if self.owner.ptp:
                    assert data[8:12] == b'\0' * 4, 'PTP stream SSRC must be zero'
                nonce = b'\0' * 4 + data[-8:]
                plain = ChaCha20Poly1305(self.owner.audio_key).decrypt(nonce, data[12:-8], data[4:12])
                assert (plain[0] >> 5) == 1
                self.owner.audio_packets += 1
                if self.owner.cohort:
                    if self.owner.allow_single_fallback:
                        assert self.owner.peers_set and self.owner.confirmed
                        assert not self.owner.cohort[1].peers_set and not self.owner.cohort[1].confirmed, 'Fallback configured failed member'
                    else:
                        assert all(p.peers_set for p in self.owner.cohort), 'audio before all members configured'
                    if self.owner.keep_frames or len(self.owner.frames)<64:
                        self.owner.frames.append((data[4:8], plain))
                if self.owner.audio_packets == 1:
                    self.owner.original = data
                    seq = struct.unpack('>H', data[2:4])[0]
                    request = b'\x80\xd5\x00\x01' + struct.pack('>HH', seq, 1)
                    self.owner.control.transport.sendto(request, ('127.0.0.1', self.owner.sender_control))
            elif self.kind == 'control' and len(data) > 4 and data[1] == 0xd6:
                assert data[4:] == self.owner.original
                self.owner.resends += 1
            elif self.kind == 'timing':
                assert len(data) == 32 and data[1] == 0xd3 and data[8:16] == self.owner.timing_origin
                self.owner.timing_replies += 1
            elif self.kind == 'control' and len(data) == 28 and data[1] == 0xd7:
                assert self.owner.ptp and self.owner.peers_set
                assert struct.unpack('>Q', data[20:28])[0] == (0x1122334455667788 if self.owner.follows else self.owner.sender_clock)
                a, b = struct.unpack('>I', data[4:8])[0], struct.unpack('>I', data[16:20])[0]
                assert (b - a) & 0xffffffff == 77175
                assert struct.unpack('>Q', data[8:16])[0] > 0
                self.owner.ptp_audio_syncs += 1
                if self.owner.cohort:
                    self.owner.anchors.append(data)
        except Exception as error:
            self.owner.errors.append(repr(error))


class PtpUdp(asyncio.DatagramProtocol):
    def __init__(self, owner, general, secondary=False):
        self.owner, self.general, self.secondary = owner, general, secondary

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        p = self.owner
        try:
            assert addr == ('127.0.0.1', 31320 if self.general else 31319)
            assert len(data) >= 34 and data[0] >> 4 == 1 and data[1] & 15 == 2
            assert int.from_bytes(data[2:4], 'big') == len(data)
            kind = data[0] & 15
            clock = int.from_bytes(data[20:28], 'big')
            if p.sender_clock is not None:
                assert clock == p.sender_clock
            if self.secondary:
                p.secondary_timing += 1
                return
            if kind == 11:
                assert len(data) == 76 and data[47:53] == bytes.fromhex('800621436a80')
                assert data[53:61] == data[68:76] == data[20:28]
                p.ptp_announces += 1
            elif kind == 0:
                assert data[6] & 2 and len(data) == 44
                p.ptp_sender_syncs += 1
            elif kind == 8:
                assert len(data) == 96 and data[44:54] == bytes.fromhex('0003001c0080c2000001')
                assert data[76:86] == bytes.fromhex('00030010000d93000004') and data[86:94] == data[20:28]
                p.ptp_followups += 1
            elif kind in [3, 9, 10]:
                assert len(data) == 54 and data[44:54] == bytes.fromhex('11223344556677880001')
                assert int.from_bytes(data[40:44], 'big') < 1_000_000_000
                p.ptp_responses += 1
            elif kind == 12:
                assert data[44:48] == bytes.fromhex('00050008') and data[48] == 0xb0
                p.ptp_grants += 1
            else:
                raise AssertionError(('unexpected PTP type', kind))
        except Exception as error:
            p.errors.append(repr(error))


def ptp_header(kind, size, sequence, flags=0x0408):
    packet = bytearray(size)
    struct.pack_into('>BBHBBHqI', packet, 0, 0x10 | kind, 2, size, 0, 0, flags, 0, 0)
    struct.pack_into('>QHHBB', packet, 20, 0x1122334455667788, 1, sequence & 0xffff, 0, 0xfd)
    return packet


def ptp_timestamp(packet, offset, ns):
    seconds, fraction = divmod(ns, 1_000_000_000)
    struct.pack_into('>HI', packet, offset, seconds >> 32, seconds & 0xffffffff)
    struct.pack_into('>I', packet, offset + 6, fraction)


class Peer:
    def __init__(self, mode, host=None):
        self.mode = mode
        self.confirmed = False
        self.audio_packets = self.resends = self.timing_replies = self.feedback = 0
        self.audio_key = b''
        self.original = b''
        self.sender_control = 0
        self.timing_origin = bytes.fromhex('0102030405060708')
        self.errors, self.methods, self.transports = [], [], []
        self.connections = set()
        self.ptp = mode.startswith('ptp-')
        self.follows = mode in ['ptp-single', 'ptp-probes-only']
        self.sender_clock = None
        self.ptp_active = self.peers_set = False
        self.ptp_announces = self.ptp_sender_syncs = self.ptp_followups = self.ptp_responses = self.ptp_grants = self.ptp_audio_syncs = self.secondary_timing = 0
        self.ptp_task = None
        self.host = host or ('127.0.0.2' if self.ptp else '127.0.0.1')
        self.cohort = None
        self.frames, self.anchors = [], []
        self.setup_payload = None
        self.volume = 20
        self.requested_volume_db = -24.0
        self.mute_readback = None
        self.volume_reads = 0
        self.authenticated = False
        self.pairing_client_ids = []
        self.allow_single_fallback = False
        self.allow_prepared_fallback = False
        self.pairing_password = None
        self.requires_password = False
        self.pairings = {}
        self.controller_keys = []
        self.accessory_id = ('fixture-' + self.host).encode()
        self.accessory_key = Ed25519PrivateKey.from_private_bytes(hashlib.sha256(self.accessory_id).digest())
        self.bad_m6_signature = self.bad_verify_signature = self.wrong_verify_id = self.reject_verify = False
        self.reject_cached_verify = False
        self.reject_m6 = self.verify_without_delay = False
        self.digest_required = self.digest_reject = self.digest_late_reject = False
        self.digest_algorithm = 'MD5'
        self.digest_qop = None
        self.digest_malformed = False
        self.digest_password = 'demo-secondary-secret'
        self.digest_challenges = self.digest_proofs = 0
        self.digest_replied_requests = []
        self.event_secret = None
        self.event_mode = None
        self.event_acks = 0
        self.keep_frames = True
        self.rtsp_connections = 0
        self.reject_second_digest_challenge = False

    async def start(self):
        loop = asyncio.get_running_loop()
        for kind in ['data', 'control', 'timing']:
            transport, protocol = await loop.create_datagram_endpoint(lambda: Udp(self, kind), local_addr=(self.host, 0))
            self.transports.append(transport)
            setattr(self, kind, protocol)
        self.event = await asyncio.start_server(self.event_connection, self.host, 0)
        self.server = await asyncio.start_server(self.handle, self.host, 0)
        if self.ptp:
            for general in [False, True]:
                transport, protocol = await loop.create_datagram_endpoint(lambda g=general: PtpUdp(self, g), local_addr=(self.host, 31320 if general else 31319))
                self.transports.append(transport)
                setattr(self, 'ptp_general' if general else 'ptp_events', protocol)
                if self.mode == 'ptp-pair':
                    t, _ = await loop.create_datagram_endpoint(lambda g=general: PtpUdp(self, g, True), local_addr=('127.0.0.3', 31320 if general else 31319))
                    self.transports.append(t)
            if self.mode == 'ptp-port-busy':
                t, _ = await loop.create_datagram_endpoint(asyncio.DatagramProtocol, local_addr=('127.0.0.1', 31319))
                self.transports.append(t)
            self.ptp_task = asyncio.create_task(self.ptp_loop())
        return self.server.sockets[0].getsockname()[1]

    async def ptp_loop(self):
        sequence = 0
        granted = False
        while True:
            await asyncio.sleep(0.125)
            if not self.ptp_active or self.mode in ['ptp-no-clock', 'ptp-port-busy']:
                continue
            if self.mode == 'ptp-clock-loss' and self.audio_packets > 100:
                continue
            if self.mode == 'ptp-native-no-clock' or self.mode == 'ptp-native-clock-loss' and self.audio_packets > 100:
                continue
            if self.follows and self.mode != 'ptp-probes-only':
                if sequence % 8 == 0:
                    announce = ptp_header(11, 64, sequence)
                    struct.pack_into('>Q', announce, 53, 0x1122334455667788)
                    self.ptp_general.transport.sendto(announce, ('127.0.0.1', 31320))
                sync = ptp_header(0, 44, sequence, 0x0608)
                follow = ptp_header(8, 44, sequence)
                ptp_timestamp(follow, 34, time.monotonic_ns())
                self.ptp_events.transport.sendto(sync, ('127.0.0.1', 31319))
                self.ptp_general.transport.sendto(follow, ('127.0.0.1', 31320))
            else:
                request = ptp_header(1 if sequence % 2 == 0 else 2, 44 if sequence % 2 == 0 else 54, sequence)
                self.ptp_events.transport.sendto(request, ('127.0.0.1', 31319))
                if not granted and self.sender_clock is not None and not self.follows:
                    signaling = ptp_header(12, 54, sequence)
                    struct.pack_into('>QH', signaling, 34, self.sender_clock, 1)
                    signaling[44:54] = bytes.fromhex('00040006b0000000001e')
                    self.ptp_general.transport.sendto(signaling, ('127.0.0.1', 31320))
                    granted = True
            sequence = (sequence + 1) & 0xffff

    async def event_connection(self, reader, writer):
        self.connections.add(writer)
        try:
            if self.event_mode is None:
                await reader.read()
                return
            assert self.event_secret is not None
            wire = Wire(reader, writer)
            wire.encrypt = ChaCha20Poly1305(pairing_key(self.event_secret, 'Events-Salt', 'Events-Write-Encryption-Key'))
            wire.decrypt = ChaCha20Poly1305(pairing_key(self.event_secret, 'Events-Salt', 'Events-Read-Encryption-Key'))
            sequence = 1
            while True:
                if self.event_mode == 'event-eof' and self.audio_packets > 30:
                    return
                body = plistlib.dumps({'type': 'updateInfo', 'private': 'never-log-event-content', 'blob': b'x'*1300}, fmt=plistlib.FMT_BINARY)
                request = f'POST /command RTSP/1.0\r\nCSeq: {sequence}\r\nContent-Length: {len(body) if self.event_mode != "oversized-event" else 2097153}\r\n\r\n'.encode() + (body if self.event_mode != 'oversized-event' else b'')
                encrypted = bytearray()
                for at in range(0,len(request),1024):
                    block=request[at:at+1024]; head=struct.pack('<H',len(block)); nonce=b'\0'*4+struct.pack('<Q',wire.output_nonce);wire.output_nonce+=1
                    encrypted.extend(head+wire.encrypt.encrypt(nonce,block,head))
                if self.event_mode == 'bad-event-tag': encrypted[-1]^=1
                # Split encrypted lengths, ciphertext and tags across TCP writes.
                writer.write(encrypted[:1]);await writer.drain();writer.write(encrypted[1:19]);await writer.drain();writer.write(encrypted[19:]);await writer.drain()
                if self.event_mode in ['oversized-event','bad-event-tag']:
                    await reader.read();return
                while b'\r\n\r\n' not in wire.buffer: await asyncio.wait_for(wire.fill(),2)
                at=wire.buffer.index(b'\r\n\r\n');header=bytes(wire.buffer[:at]).decode();del wire.buffer[:at+4]
                lines=header.split('\r\n');assert lines[0]=='RTSP/1.0 200 OK'
                headers=dict(line.split(': ',1) for line in lines[1:])
                assert headers=={'Server':'WinToPod','CSeq':str(sequence)}, 'Non-minimal event response or CSeq mismatch'
                self.event_acks+=1;sequence+=1
                await asyncio.sleep(.5)
        except (asyncio.IncompleteReadError,ConnectionResetError,BrokenPipeError):
            pass
        except Exception as error:
            self.errors.append(repr(error))
        finally:
            writer.close()
            self.connections.discard(writer)

    async def handle(self, reader, writer):
        self.rtsp_connections += 1
        self.connections.add(writer)
        wire = Wire(reader, writer)
        salt = bytes.fromhex('00112233445566778899aabbccddeeff')
        password = self.pairing_password or ('demo-secret' if self.mode in ['password-supplied', 'wrong-password'] else '3939')
        context = SRPContext('Pair-Setup', password, prime=PRIME_3072, generator=PRIME_3072_GEN, hash_func=hashlib.sha512)
        verifier = context.get_common_password_verifier(context.get_common_password_hash(salt))
        server = SRPServerSession(context, format(verifier, 'x'), private='22' * 32)
        session_setup = False; pairing_started = False
        normal_secret = verify_secret = verify_key = None
        client_ephemeral = server_ephemeral = None
        digest_nc = 0
        challenged_request = None
        digest_realm, digest_nonce, digest_opaque = 'AirPlay, living-room', 'never-log-digest-nonce-' + str(self.rtsp_connections), 'never-log-opaque'
        try:
            while True:
                method, path, headers, body = await wire.request()
                self.methods.append(method + ' ' + ('/session' if path.startswith('rtsp:') else path))
                # A strict compatibility peer: the previous custom UA/MIME must
                # fail this regression. This models published receiver evidence;
                # it is not proof of Apple firmware compatibility.
                assert headers.get('User-Agent') == 'AirPlay/999.0.0', 'unsupported sender identification'
                playback_request = path.startswith('rtsp:') or path == '/feedback'
                if playback_request and self.digest_required:
                    assert wire.encrypt is not None, 'Digest sent outside encrypted authenticated control'
                    authorization = headers.get('Authorization')
                    if authorization is None:
                        assert method == 'SETUP' and not session_setup, 'Missing preauthorization on a later request'
                        self.digest_challenges += 1
                        challenged_request = (method, path, body, headers['CSeq'])
                        challenge = f'Digest realm="{digest_realm}", nonce="{digest_nonce}", opaque="{digest_opaque}"'
                        if self.digest_algorithm != 'MD5': challenge += ', algorithm=' + self.digest_algorithm
                        if self.digest_qop is not None: challenge += ', qop="' + self.digest_qop + '"'
                        if self.digest_malformed: challenge += ', nonce="duplicate"'
                        await wire.response(headers, status='401 Unauthorized', extra={'WWW-Authenticate': challenge})
                        continue
                    assert authorization.startswith('Digest ')
                    auth = parse_keqv_list(parse_http_list(authorization[7:]))
                    assert auth['username'] == '' and auth['realm'] == digest_realm and auth['nonce'] == digest_nonce and auth['uri'] == path
                    assert auth['opaque'] == digest_opaque
                    algorithm = self.digest_algorithm.upper()
                    hash_fn = hashlib.sha256 if algorithm.startswith('SHA-256') else hashlib.md5
                    hx = lambda text: hash_fn(text.encode()).hexdigest()
                    ha1 = hx(':' + digest_realm + ':' + self.digest_password)
                    if algorithm.endswith('-SESS'): ha1 = hx(ha1 + ':' + digest_nonce + ':' + auth['cnonce'])
                    if self.digest_qop is None:
                        assert 'qop' not in auth and 'nc' not in auth
                        expected = hx(ha1 + ':' + digest_nonce + ':' + hx(method + ':' + path))
                    else:
                        digest_nc += 1
                        assert auth['qop'] == 'auth' and auth['nc'] == f'{digest_nc:08x}' and len(auth['cnonce']) == 32
                        expected = hx(ha1 + ':' + digest_nonce + ':' + auth['nc'] + ':' + auth['cnonce'] + ':auth:' + hx(method + ':' + path))
                    assert auth['response'] == expected, 'Digest proof rejected by independent hashlib computation'
                    if challenged_request is not None:
                        assert (method, path, body) == challenged_request[:3] and headers['CSeq'] != challenged_request[3], 'Challenge changed request or reused CSeq'
                        challenged_request = None
                    self.digest_proofs += 1; self.digest_replied_requests.append(method)
                    if self.digest_reject or self.reject_second_digest_challenge and self.digest_challenges == 2 or self.digest_late_reject and path == '/feedback':
                        await wire.response(headers, status='401 Unauthorized', extra={'WWW-Authenticate': f'Digest realm="{digest_realm}", nonce="different-never-log"'})
                        continue
                elif playback_request:
                    assert 'Authorization' not in headers, 'Another receiver received a playback credential'
                if path == '/info':
                    if self.allow_prepared_fallback and 'TEARDOWN /session' in self.methods:
                        self.allow_single_fallback = True
                    self.sender_clock = None; self.ptp_active = self.peers_set = self.confirmed = self.authenticated = False
                    if self.mode == 'info-denied':
                        await wire.response(headers, status='403 Forbidden', extra={'Server': 'AirTunes/980.77.2'})
                        continue
                    info = {'name': 'SimulatedHomePod', 'model': 'AudioAccessory5,1'}
                    if self.ptp:
                        info['features'] = 0x3C354BD04A7FCA00
                    if self.mode in ['password-required', 'password-supplied', 'wrong-password']:
                        info['statusFlags'] = 0x84
                    if self.cohort:
                        info['statusFlags'] = (0xbac04 if self.host == '127.0.0.2' else 0xb8c04) | (0x80 if self.requires_password else 0)
                    await wire.response(headers, plistlib.dumps(info, fmt=plistlib.FMT_BINARY))
                elif path == '/pair-pin-start':
                    assert self.cohort and self.host == '127.0.0.3', 'Known-working single/leader authentication changed'
                    assert headers['X-Apple-HKP'] == '4' and not body
                    assert self.methods.count('POST /pair-pin-start') == 1, 'Repeated pairing start'
                    pairing_started = True
                    await wire.response(headers, status='403 Forbidden' if self.mode == 'ptp-native-start-rejected' else '200 OK')
                elif path == '/pair-setup':
                    assert headers.get('Content-Type') == 'application/octet-stream', 'wrong AirPlay pairing MIME'
                    assert headers.get('X-Apple-Client-Name') == 'WinToPod', 'missing sender name'
                    self.pairing_client_ids.append(str(uuid.UUID(headers['X-Apple-Client-ID'])))
                    if self.mode in ['access-denied', 'info-denied']:
                        await wire.response(headers, status='403 Forbidden', extra={'Server': 'AirTunes/980.77.2'})
                        continue
                    if self.mode == 'pairing-denied-tlv':
                        await wire.response(headers, tlv([(6, b'\x02'), (7, b'\x06'), (8, b'\x3c'), (3, b'never-log-key')]),
                                            status='403 Forbidden', extra={'Content-Type': 'application/octet-stream', 'Server': 'private-home never-log'})
                        continue
                    if self.mode in ['password-challenge', 'password-challenge-supplied']:
                        await wire.response(headers, status='401 Unauthorized', extra={'WWW-Authenticate': 'Digest realm="AirPlay", nonce="test-only"'})
                        continue
                    request = untlv(body)
                    normal = headers['X-Apple-HKP'] == '3'
                    if self.requires_password or self.mode in ['password-supplied', 'wrong-password']:
                        assert normal, 'Password sent through the rejected HKP4 transient route'
                    if request[6] == b'\x01':
                        assert request[0] == b'\0'
                        assert (19 not in request) if normal else request[19] == b'\x10'
                        if self.mode == 'ptp-native-setup-rejected':
                            await wire.response(headers, status='403 Forbidden')
                            continue
                        await wire.response(headers, tlv([(6, b'\x02'), (2, salt), (3, bytes.fromhex(server.public))]))
                    elif request[6] == b'\x03':
                        if self.mode == 'ptp-native-reject-auth':
                            await wire.response(headers, tlv([(6, b'\x04'), (7, b'\x02'), (8, b'\x04')]))
                            continue
                        server.process(request[3].hex(), salt.hex())
                        if self.mode in ['wrong-password', 'ptp-native-wrong-password', 'ptp-native-wrong-password-no-delay']:
                            assert not hmac.compare_digest(request[4].hex().encode(), server.key_proof)
                            refusal = [(6, b'\x04'), (7, b'\x02')]
                            if self.mode == 'ptp-native-wrong-password':
                                refusal.append((8, b'\x04'))
                            await wire.response(headers, tlv(refusal))
                            continue
                        assert hmac.compare_digest(request[4].hex().encode(), server.key_proof)
                        await wire.response(headers, tlv([(6, b'\x04'), (4, bytes.fromhex(server.key_proof_hash.decode()))]))
                        secret = bytes.fromhex(server.key.decode())
                        if normal:
                            normal_secret = secret
                            continue
                        self.audio_key = secret[:32]
                        self.event_secret = secret
                        wire.decrypt = ChaCha20Poly1305(control_key(secret, b'Control-Write-Encryption-Key'))
                        wire.encrypt = ChaCha20Poly1305(control_key(secret, b'Control-Read-Encryption-Key'))
                        self.authenticated = True
                    else:
                        assert normal and request[6] == b'\x05' and normal_secret is not None
                        if self.reject_m6:
                            await wire.response(headers, tlv([(6, b'\x06'), (7, b'\x02')]))
                            continue
                        key = pairing_key(normal_secret, 'Pair-Setup-Encrypt-Salt', 'Pair-Setup-Encrypt-Info')
                        controller = untlv(pairing_decrypt(key, 'PS-Msg05', request[5]))
                        controller_id, controller_public = controller[1], controller[3]
                        controller_x = pairing_key(normal_secret, 'Pair-Setup-Controller-Sign-Salt', 'Pair-Setup-Controller-Sign-Info')
                        Ed25519PublicKey.from_public_bytes(controller_public).verify(controller[10], controller_x + controller_id + controller_public)
                        assert controller_id.decode() == headers['X-Apple-Client-ID']
                        self.pairings[controller_id] = controller_public
                        self.controller_keys.append(controller_public)
                        public = raw_public(self.accessory_key.public_key())
                        accessory_x = pairing_key(normal_secret, 'Pair-Setup-Accessory-Sign-Salt', 'Pair-Setup-Accessory-Sign-Info')
                        signature = self.accessory_key.sign(accessory_x + self.accessory_id + public)
                        if self.bad_m6_signature:
                            signature = bytes([signature[0] ^ 1]) + signature[1:]
                        payload = tlv([(1, self.accessory_id), (3, public), (10, signature)])
                        encrypted = pairing_encrypt(key, 'PS-Msg06', payload)
                        await wire.response(headers, tlv([(6, b'\x06'), (5, encrypted)]))
                elif path == '/pair-verify':
                    assert headers['X-Apple-HKP'] == '3' and headers['Content-Type'] == 'application/octet-stream'
                    assert normal_secret is None, 'Normal pairing must hand off to a separate verification connection'
                    self.pairing_client_ids.append(str(uuid.UUID(headers['X-Apple-Client-ID'])))
                    request = untlv(body)
                    if request[6] == b'\x01':
                        if self.reject_verify or self.reject_cached_verify and self.methods.count('POST /pair-verify') > 2:
                            refusal = [(6, b'\x02'), (7, b'\x02')]
                            if not self.verify_without_delay:
                                refusal.append((8, b'\x04'))
                            await wire.response(headers, tlv(refusal))
                            continue
                        client_ephemeral = request[3]
                        private = X25519PrivateKey.generate()
                        server_ephemeral = raw_public(private.public_key())
                        verify_secret = private.exchange(X25519PublicKey.from_public_bytes(client_ephemeral))
                        verify_key = pairing_key(verify_secret, 'Pair-Verify-Encrypt-Salt', 'Pair-Verify-Encrypt-Info')
                        identifier = b'another-accessory' if self.wrong_verify_id else self.accessory_id
                        signature = self.accessory_key.sign(server_ephemeral + identifier + client_ephemeral)
                        if self.bad_verify_signature:
                            signature = bytes([signature[0] ^ 1]) + signature[1:]
                        encrypted = pairing_encrypt(verify_key, 'PV-Msg02', tlv([(1, identifier), (10, signature)]))
                        await wire.response(headers, tlv([(6, b'\x02'), (3, server_ephemeral), (5, encrypted)]))
                    else:
                        assert request[6] == b'\x03' and verify_key is not None
                        controller = untlv(pairing_decrypt(verify_key, 'PV-Msg03', request[5]))
                        identifier = controller[1]
                        Ed25519PublicKey.from_public_bytes(self.pairings[identifier]).verify(controller[10], client_ephemeral + identifier + server_ephemeral)
                        assert identifier.decode() == headers['X-Apple-Client-ID']
                        await wire.response(headers, tlv([(6, b'\x04')]))
                        self.audio_key = verify_secret
                        self.event_secret = verify_secret
                        wire.decrypt = ChaCha20Poly1305(control_key(verify_secret, b'Control-Write-Encryption-Key'))
                        wire.encrypt = ChaCha20Poly1305(control_key(verify_secret, b'Control-Read-Encryption-Key'))
                        self.authenticated = True
                elif method == 'SETUP':
                    if self.cohort and not self.allow_single_fallback:
                        assert all(p.authenticated for p in self.cohort), 'SETUP before every member authenticated'
                    value = plistlib.loads(body)
                    if self.allow_single_fallback:
                        assert self.authenticated and (not self.cohort[1].authenticated or self.allow_prepared_fallback)
                        if 'streams' not in value:
                            assert value['isMultiSelectAirPlay'] is False, 'Fallback must use known single-member route'
                    if 'streams' not in value:
                        assert value['timingProtocol'] == ('PTP' if self.ptp else 'NTP')
                        if self.ptp:
                            self.setup_payload = value
                            assert 'timingPort' not in value
                            peer = value['timingPeerInfo']
                            assert peer == value['timingPeerList'][0] and peer['Addresses'] == ['127.0.0.1']
                            assert value['name'] == 'WinToPod' and value['model'] == 'WinToPod'
                            self.sender_clock = peer['ClockID'] & ((1 << 64) - 1)
                            assert int(headers['DACP-ID'], 16) == self.sender_clock
                            identity = bytes.fromhex(headers['DACP-ID'])
                            assert identity[0] & 3 == 2
                            assert value['deviceID'].replace(':', '') == headers['DACP-ID'][:12]
                            self.ptp_active = True
                        session_setup = True
                        response = {'eventPort': self.event.sockets[0].getsockname()[1], 'timingPort': self.timing.transport.get_extra_info('sockname')[1]}
                        await wire.response(headers, plistlib.dumps(response, fmt=plistlib.FMT_BINARY), {'Session': 'simulated-1'})
                        if not self.ptp:
                            query = b'\x80\xd2\x00\x07' + b'\0' * 20 + self.timing_origin
                            self.timing.transport.sendto(query, ('127.0.0.1', value['timingPort']))
                    else:
                        stream = value['streams'][0]
                        assert session_setup and stream['type'] == 96 and stream['ct'] == 2 and stream['spf'] == 352
                        assert stream['shk'] == self.audio_key and stream['audioFormat'] == 0x40000
                        self.sender_control = stream['controlPort']
                        response = {'streams': [{'controlPort': self.control.transport.get_extra_info('sockname')[1], 'dataPort': self.data.transport.get_extra_info('sockname')[1]}]}
                        await wire.response(headers, plistlib.dumps(response, fmt=plistlib.FMT_BINARY), status='453 Rejected' if self.mode == 'ptp-native-reject-stream' else '200 OK')
                elif method == 'RECORD':
                    assert session_setup
                    await wire.response(headers)
                elif method == 'SETPEERS':
                    assert self.ptp
                    peers = plistlib.loads(body)
                    assert peers == (['127.0.0.2', '127.0.0.3', '127.0.0.1'] if self.mode == 'ptp-pair' or self.cohort else [self.host, '127.0.0.1'])
                    self.peers_set = True
                    await wire.response(headers, status='453 Rejected' if self.mode == 'ptp-reject-peers' else '200 OK')
                elif method == 'SET_PARAMETER':
                    if self.cohort or self.mode.startswith('mute-single-'):
                        if self.cohort: assert self.host == '127.0.0.2', 'volume must go only to native leader'
                        self.requested_volume_db = float(body.decode().split(':')[1])
                        self.volume = 0 if self.requested_volume_db < -30 else round((self.requested_volume_db + 30) / .3)
                        if (self.mode.startswith('ptp-native-mute-') or self.mode.startswith('mute-single-')) and self.requested_volume_db < -30:
                            self.mute_readback = {'floor': -30, 'stale': -24, 'zero': 0, 'exact': -144}[self.mode.rsplit('-', 1)[1]]
                        else:
                            self.mute_readback = None
                    else:
                        assert body.startswith(b'volume: -24.')
                    await wire.response(headers)
                elif method == 'GET_PARAMETER':
                    self.confirmed = self.mode not in ['reject-volume', 'ptp-volume-reject', 'ptp-native-volume-reject']
                    self.volume_reads += 1
                    if self.cohort or self.mode.startswith('mute-single-'):
                        if self.cohort: assert self.host == '127.0.0.2'
                        if self.mode == 'ptp-native-volume-change' and self.volume_reads == 2:
                            self.volume = 43
                        await wire.response(headers, (f'volume: {self.mute_readback if self.mute_readback is not None else self.requested_volume_db if self.requested_volume_db < -30 else -30 + self.volume * .3:.6f}\r\n' if self.confirmed else 'volume: 0.000000\r\n').encode())
                    else:
                        await wire.response(headers, b'volume: -24.000000\r\n' if self.confirmed else b'volume: 0.000000\r\n')
                elif path == '/feedback':
                    self.feedback += 1
                    await wire.response(headers, status='453 Session Interrupted' if self.mode in ['interrupt', 'ptp-native-interrupt'] else '200 OK')
                elif method == 'TEARDOWN':
                    await wire.response(headers)
                    break
                else:
                    raise AssertionError((method, path))
        except asyncio.IncompleteReadError:
            pass
        except Exception as error:
            self.errors.append(repr(error))
        finally:
            writer.close()
            self.connections.discard(writer)

    async def close(self):
        if self.ptp_task is not None:
            self.ptp_task.cancel()
            try:
                await self.ptp_task
            except asyncio.CancelledError:
                pass
        self.server.close()
        self.event.close()
        await self.server.wait_closed()
        await self.event.wait_closed()
        for writer in list(self.connections):
            writer.close()
        for transport in self.transports:
            transport.close()


async def main():
    dotnet, assembly = sys.argv[1:3]
    results = []
    for mode in ['normal', 'reject-volume', 'interrupt', 'password-required', 'password-supplied', 'wrong-password', 'access-denied', 'password-challenge', 'password-challenge-supplied', 'info-denied', 'pairing-denied-tlv', 'ptp-pair', 'ptp-single', 'ptp-no-clock', 'ptp-probes-only', 'ptp-clock-loss', 'ptp-volume-reject', 'ptp-reject-peers', 'ptp-port-busy', 'mute-single-floor', 'mute-single-stale']:
        if len(sys.argv) > 3 and mode not in sys.argv[3:]:
            continue
        peer = Peer(mode)
        port = await peer.start()
        try:
            child = await asyncio.create_subprocess_exec(dotnet, assembly, '--test-session', str(port), mode, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            stdout, stderr = await asyncio.wait_for(child.communicate(), 25)
            await asyncio.sleep(0.1)
            assert child.returncode == 0, (mode, stdout.decode(), stderr.decode(), peer.errors)
            assert not peer.errors, peer.errors
            if mode in ['ptp-pair', 'ptp-single']:
                assert peer.audio_packets > 100 and peer.ptp_audio_syncs >= 1 and peer.resends >= 1
                assert peer.methods.count('SETUP /session') == 2 and peer.methods.count('POST /pair-setup') == 2
                if mode == 'ptp-pair':
                    assert peer.ptp_announces > 0 and peer.ptp_followups > 0 and peer.ptp_responses > 10 and peer.ptp_grants >= 1 and peer.secondary_timing > 10
                else:
                    assert peer.ptp_announces == peer.ptp_sender_syncs == 0
            elif mode in ['ptp-no-clock', 'ptp-probes-only', 'ptp-volume-reject', 'ptp-reject-peers', 'ptp-port-busy']:
                assert peer.audio_packets == 0 and 'TEARDOWN /session' not in peer.methods
                if mode != 'ptp-volume-reject':
                    assert 'SET_PARAMETER /session' not in peer.methods
                if mode == 'ptp-port-busy':
                    assert 'SETUP /session' not in peer.methods
            elif mode == 'ptp-clock-loss':
                assert peer.audio_packets > 30 and 'TEARDOWN /session' not in peer.methods, (mode, peer.audio_packets, peer.methods, stdout.decode())
            elif mode.startswith('mute-single-'):
                assert peer.audio_packets > 180 and peer.methods.count('POST /pair-setup') == 2
                assert peer.volume == 20 and peer.confirmed
            elif mode in ['normal', 'password-supplied']:
                assert peer.audio_packets > 100 and peer.resends >= 1 and peer.timing_replies >= 1 and peer.feedback >= 1
            elif mode == 'reject-volume':
                assert peer.audio_packets == 0
            elif mode == 'interrupt':
                assert peer.audio_packets > 30 and peer.feedback == 1 and 'TEARDOWN /session' not in peer.methods
            else:
                assert peer.audio_packets == 0 and 'TEARDOWN /session' not in peer.methods
                assert sum(method == 'POST /pair-setup' for method in peer.methods) == (0 if mode == 'password-required' else 2 if mode == 'wrong-password' else 1)
            results.append({'mode': mode, 'passed': True, 'audio_packets': peer.audio_packets, 'retransmits': peer.resends,
                            'timing_replies': peer.timing_replies, 'feedback': peer.feedback, 'client': stdout.decode().strip(),
                            'methods': peer.methods})
            if peer.ptp:
                results[-1]['ptp'] = {'announces': peer.ptp_announces, 'sender_syncs': peer.ptp_sender_syncs, 'followups': peer.ptp_followups,
                                      'delay_responses': peer.ptp_responses, 'grants': peer.ptp_grants, 'audio_syncs': peer.ptp_audio_syncs, 'secondary_timing': peer.secondary_timing}
            print(mode + ': passed', file=sys.stderr)
        finally:
            await peer.close()
    print(json.dumps({'real_homepod': False, 'independent_simulated_receiver': results}, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    asyncio.run(main())
