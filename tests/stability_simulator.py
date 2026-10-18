#!/usr/bin/env python3
"""Full-secret reverse events, credential reuse, failure isolation and soak.
Independent Python crypto and hashlib peers. No real HomePod is contacted.
"""
import asyncio,json,sys,os,time
from receiver_simulator import Peer

async def main():
    dotnet,assembly=sys.argv[1:3];reports=[]
    for mode in ['events','cached-reconnect','forget-reconnect','expired-cache','event-eof','bad-event-tag','oversized-event','soak']:
        if len(sys.argv)>3 and mode not in sys.argv[3:]:continue
        peers=[Peer('ptp-native-normal','127.0.0.2'),Peer('ptp-native-normal','127.0.0.3')]
        primary,secondary=peers
        secondary.requires_password=True;secondary.pairing_password='demo-secondary-secret';secondary.digest_required=True
        secondary.reject_second_digest_challenge=mode=='expired-cache'
        for peer in peers:
            peer.cohort=peers;peer.event_mode='events';peer.keep_frames=False
        if mode in ['event-eof','bad-event-tag','oversized-event']:secondary.event_mode=mode
        seconds=int(os.environ.get('WINTOPOD_SOAK_SECONDS','660')) if mode=='soak' else 2
        started=time.monotonic()
        try:
            ports=[await p.start() for p in peers]
            process=await asyncio.create_subprocess_exec(dotnet,assembly,'--test-stability',*map(str,ports),mode,str(seconds),stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE)
            out,err=await asyncio.wait_for(process.communicate(),seconds+45)
            assert process.returncode==0,(mode,out.decode(),err.decode(),[p.errors for p in peers])
            assert all(not p.errors for p in peers),(mode,[p.errors for p in peers])
            assert len(primary.event_secret)==64 and len(secondary.event_secret)==32,'Events used wrong secret length'
            if mode in ['event-eof','bad-event-tag','oversized-event']:
                assert all('TEARDOWN /session' not in p.methods for p in peers),'Control after event loss'
                assert secondary.digest_challenges==1,'Automatic reconnection after loss'
            else:
                assert all(p.event_acks>=3 for p in peers)
                assert all(p.audio_packets>30 for p in peers)
            connections=3 if mode=='expired-cache' else 2 if mode in ['cached-reconnect','forget-reconnect'] else 1
            assert secondary.digest_challenges==connections and secondary.methods.count('POST /pair-setup')==3
            assert secondary.methods.count('POST /pair-verify')==2*connections,'Unexpected re-pair or auto retry'
            if mode=='soak':
                assert all(p.audio_packets>seconds*100 for p in peers),'Long stream silently stalled'
                assert primary.audio_packets==secondary.audio_packets
                assert all(p.event_acks>seconds for p in peers)
            reports.append({'mode':mode,'passed':True,'seconds':round(time.monotonic()-started,2),'event_acks':[p.event_acks for p in peers],'member_packets':[p.audio_packets for p in peers]})
            print(mode+': passed',file=sys.stderr)
        finally:
            for peer in peers:await peer.close()
            await asyncio.sleep(.1)
    print(json.dumps({'real_homepod':False,'stability_simulations':reports},indent=2))

if __name__=='__main__':asyncio.run(main())
