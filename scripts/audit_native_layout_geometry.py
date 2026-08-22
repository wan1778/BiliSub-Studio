#!/usr/bin/env python3
from dataclasses import dataclass
import sys

@dataclass
class R:
    name:str; x:int; y:int; w:int; h:int; group:str=''
    @property
    def r(self): return self.x+self.w
    @property
    def b(self): return self.y+self.h

def overlap(a,b):
    return min(a.r,b.r) > max(a.x,b.x) and min(a.b,b.b) > max(a.y,b.y)

def common(cw,ch):
    x=180; pw=max(720,cw-x-12)
    return x,pw,ch-36

def page_rects(page,cw,ch):
    x,pw,statusY=common(cw,ch)
    out=[R('title',x+8,10,pw-24,28,'header'),R('help',x+8,40,pw-24,34,'header')]
    if page=='subtitle':
        out += [R('url',x+8,108,pw-200,30),R('analyze',x+pw-180,108,160,30),R('meta',x+8,144,pw-28,28),
                R('track',x+8,204,pw//2-24,30),R('format',x+pw//2+8,204,pw//2-28,30),
                R('out',x+8,272,pw-280,30),R('pick',x+pw-260,272,80,30),R('open',x+pw-170,272,80,30),
                R('download',x+8,316,160,34),R('cancel',x+180,316,90,34),R('progress',x+284,323,pw-304,18),
                R('state',x+8,356,pw-28,28),R('log',x+8,390,pw-28,max(120,ch-48-390))]
    elif page=='video':
        q=pw//4
        out += [R('url',x+8,108,pw-200,30),R('analyze',x+pw-180,108,160,30),R('meta',x+8,144,pw-28,28),
                R('quality',x+8,204,q-20,30),R('mode',x+q+8,204,q-20,30),R('speed',x+2*q+8,204,q-20,30),R('container',x+3*q+8,204,q-28,30),
                R('out',x+8,272,pw-280,30),R('pick',x+pw-260,272,80,30),R('open',x+pw-170,272,80,30),
                R('download',x+8,316,150,34),R('cancel',x+170,316,100,34),R('progress',x+284,323,pw-304,18),
                R('state',x+8,356,pw-28,28),R('log',x+8,390,pw-28,max(120,ch-48-390))]
    elif page=='ocr':
        left=max(430,pw*52//100); rx=x+left+14; rw=pw-left-22
        pb=min(ch-190,555); pb=max(pb,360); py=pb+8; col=rw//4
        out += [R('path',x+8,104,left-260,28),R('pick',x+left-244,104,112,28),R('preset',x+left-124,104,112,28),
                R('preview',x+8,142,left-20,pb-142,'preview'),R('play',x+8,py,80,28),R('mute',x+96,py,120,28),R('fullscreen',x+224,py,120,28),R('timeline',x+8,py+36,max(180,left-176),30),R('time',x+left-160,py+38,148,26)]
        for i,n in enumerate(['top','bottom','left','right']):
            xx=rx+i*col; ww=(rw-3*col-8 if i==3 else col-8)
            out += [R(n+'Label',xx,104,ww,18,'roi'),R(n,xx,122,ww,28,'roi')]
        out += [R('mode',rx,182,rw//2-8,30),R('sensitivity',rx+rw//2,182,rw//2-8,30),R('device',rx,244,rw//2-8,30),R('parallel',rx+rw//2,244,rw//2-8,30),
                R('prepare',rx,282,158,30),R('test',rx+166,282,104,30),R('start',rx,320,158,30),R('pause',rx+166,320,110,30),R('restart',rx+284,320,max(122,rw-292),30),
                R('clear',rx,358,122,30),R('export',rx+130,358,122,30),R('out',rx,402,rw-184,28),R('pickOut',rx+rw-176,402,80,28),R('openOut',rx+rw-88,402,80,28),
                R('progress',rx,438,rw-8,16),R('status',rx,460,rw-8,38),R('metrics',rx,502,rw-8,84),R('cueSummary',rx,590,rw-8,24),R('cueList',rx,616,rw-8,max(70,ch-664))]
    elif page=='editor':
        left=max(430,pw*58//100); rx=x+left+14; rw=pw-left-22
        pb=min(ch-220,530); pb=max(pb,350); py=pb+8; col=rw//4
        out += [R('path',x+8,104,left-150,28),R('pick',x+left-132,104,120,28),R('preview',x+8,142,left-20,pb-142,'preview'),
                R('play',x+8,py,80,28),R('mute',x+96,py,120,28),R('fullscreen',x+224,py,120,28),R('time',x+354,py,max(120,left-374),28),R('timeline',x+8,py+36,left-20,30),
                R('subtitlePreset',x+8,py+72,120,28),R('watermarkPreset',x+136,py+72,130,28),R('delete',x+274,py+72,100,28),R('undo',x+382,py+72,90,28)]
        for i,n in enumerate(['x','y','w','h']):
            xx=rx+i*col; ww=(rw-3*col-8 if i==3 else col-8)
            out += [R(n+'Label',xx,104,ww,18,'region'),R(n,xx,122,ww,28,'region')]
        out += [R('effect',rx,182,rw//2-8,30),R('strength',rx+rw//2,182,rw//2-8,28),R('whole',rx,220,rw-8,28),
                R('startLabel',rx,270,rw//2-8,18),R('endLabel',rx+rw//2,270,rw//2-8,18),R('start',rx,290,rw//2-92,28),R('setStart',rx+rw//2-84,290,76,28),R('end',rx+rw//2,290,rw//2-92,28),R('setEnd',rx+rw-84,290,76,28),
                R('out',rx,350,rw-184,28),R('pickOut',rx+rw-176,350,80,28),R('openOut',rx+rw-88,350,80,28),R('name',rx,408,rw-8,28),R('regions',rx,466,rw-8,68),
                R('export',rx,542,140,34),R('cancel',rx+148,542,90,34),R('progress',rx+248,551,max(80,rw-256),16),R('status',rx,584,rw-8,38),R('log',rx,626,rw-8,max(60,ch-674))]
    elif page=='settings':
        qr=min(260,max(190,pw//3)); form=pw-qr-42
        if form<500: form=pw; qr=0
        out += [R('theme',x+8,104,170,30),R('root',x+8,166,form-20,22),R('drive',x+8,188,form-20,22),R('cookieState',x+8,210,form-20,22),
                R('defaultOut',x+8,260,form-190,28),R('defaultPick',x+form-174,260,76,28),R('defaultOpen',x+form-90,260,76,28),
                R('cookie',x+8,324,form-20,28),R('cookieSave',x+8,360,140,28),R('cookieDelete',x+156,360,130,28),R('qrBtn',x+294,360,120,28),R('qrState',x+8,392,form-20,24),
                R('autoUpdate',x+8,448,190,24),R('checkUpdate',x+8,474,140,28),R('applyUpdate',x+156,474,130,28),R('storage',x+8,534,form-20,24),
                R('cleanup',x+8,586,140,28),R('resetTools',x+156,586,130,28),R('removeOCR',x+294,586,130,28),R('close',x+432,586,min(230,max(140,form-446)),28),
                R('bugNote',x+8,648,form-20,62),R('bugSend',x+8,716,120,28)]
        if qr:
            out += [R('qr',x+pw-qr-12,102,qr,qr,'qr'),R('settingsStatus',x+pw-qr-12,374,qr,max(120,ch-422))]
        else:
            out += [R('settingsStatus',x+8,752,pw-28,max(70,ch-800))]
    return out,statusY

def audit():
    errs=[]
    sizes=[(1080,800),(1100,820),(1400,840),(1600,900),(1920,1080)]
    pages=['subtitle','video','ocr','editor','settings']
    for cw,ch in sizes:
        for p in pages:
            rs,statusY=page_rects(p,cw,ch)
            for r in rs:
                if r.w<=0 or r.h<=0: errs.append(f'{p}@{cw}x{ch}: {r.name} collapsed {r}')
                if r.x<180 or r.r>cw-4: errs.append(f'{p}@{cw}x{ch}: {r.name} horizontal overflow {r}')
                if r.y<0 or r.b>statusY-4: errs.append(f'{p}@{cw}x{ch}: {r.name} collides global status/bottom {r}, statusY={statusY}')
            # Different logical rows must not overlap; same group (ROI label/edit) is still checked naturally.
            for i,a in enumerate(rs):
                for b in rs[i+1:]:
                    # preview is a painted surface and intentionally contains no child controls.
                    if overlap(a,b):
                        errs.append(f'{p}@{cw}x{ch}: overlap {a.name} <-> {b.name}')
    return errs

errs=audit()
if errs:
    print('NATIVE LAYOUT GEOMETRY AUDIT: FAIL')
    for e in errs[:120]: print(' -',e)
    if len(errs)>120: print(f' ... {len(errs)-120} more')
    sys.exit(1)
print('NATIVE LAYOUT GEOMETRY AUDIT: PASS (5 workflows × 5 logical client sizes)')
