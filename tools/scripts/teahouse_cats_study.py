# Studies the cats' behaviour from the Kitty Tea House helper's journal: pairs of (settled board before, current piece,
# board after the move). Read-only; no game access.
import json, glob, collections

rows = []
for p in sorted(glob.glob(r'C:\ProgramData\TOS-companion\journal\2026-10-*.jsonl')):
    for l in open(p, encoding='utf-8'):
        if '"teahouse' in l:
            try:
                rows.append(json.loads(l))
            except Exception:
                pass

def cells(b): return [c for row in b.split('/') for c in row]
def rc(i): return divmod(i, 6)
def nbs(i):
    r, c = rc(i); out = []
    for dr, dc in ((-1, 0), (1, 0), (0, -1), (0, 1)):
        rr, cc = r + dr, c + dc
        if 0 <= rr < 6 and 0 <= cc < 6 and rr * 6 + cc != 0:
            out.append(rr * 6 + cc)
    return out

pairs = []
before = cur = None
for r in rows:
    f = r['fields']; t = r['utc']
    if f['event'] == 'teahouse.advice':
        b = f.get('board')
        if b and b != '<redacted>' and 'u' not in b:
            before, cur = b, f['current']
        else:
            before = None
    elif f['event'] == 'teahouse.move':
        a = f.get('board')
        if before and a and a != '<redacted>' and 'u' not in a:
            pairs.append(dict(t=t, before=before, cur=cur, after=a, placed=f.get('placed'), square=f.get('square'), ok=f.get('rulesMatch')))
        before = None

late = [p for p in pairs if p['t'] >= '2026-10-29T20:36']  # builds with the tile/preview fixes (fewer misreads)
print(f'move pairs: {len(pairs)} in all, {len(late)} from the later builds')

def study(pairs, label):
    print(f'\n=== {label}: {len(pairs)} moves ===')
    wc = collections.Counter(); dirs = collections.Counter(); trap = collections.Counter(); group_case = collections.Counter()
    oc = collections.Counter(); odist = collections.Counter(); onear = []
    for p in pairs:
        B = cells(p['before']); A = cells(p['after'])
        if p['cur'] in ('WhiteCat', 'OrangeCat', 'Cutlery'):
            continue  # a new cat or a removal muddles the cat count
        bw = [i for i in range(1, 36) if B[i] == 'w']; aw = [i for i in range(1, 36) if A[i] == 'w']
        bo = [i for i in range(1, 36) if B[i] == 'o']; ao = [i for i in range(1, 36) if A[i] == 'o']
        # the board the cats moved on: the after board with the cats taken back to where they stood
        P = A[:]
        for i in range(1, 36):
            if P[i] in 'wo':
                P[i] = '.'
        for i in bw: P[i] = 'w'
        for i in bo: P[i] = 'o'
        # white cats, one at a time when unambiguous
        claimed = set()
        for c in bw:
            free = [n for n in nbs(c) if P[n] == '.']
            dest = [n for n in nbs(c) if A[n] == 'w' and B[n] != 'w' and n not in claimed]
            freeGroup = None
            if A[c] == 't':
                outcome = 'tea'
            elif A[c] == 'w' and not dest:
                outcome = 'stayed'
            elif len(dest) == 1 and A[c] != 'w':
                outcome = 'moved'; claimed.add(dest[0])
                r0, c0 = rc(c); r1, c1 = rc(dest[0])
                dirs[('up' if r1 < r0 else 'down' if r1 > r0 else 'left' if c1 < c0 else 'right', len(free))] += 1
            elif A[c] == 's':
                outcome = 'sundae here'
            elif A[c] == '.' and not dest:
                outcome = 'gone (far or misread)'
            else:
                outcome = 'ambiguous'
            wc[(min(len(free), 2), outcome)] += 1
            if len(free) == 0:
                # boxed in alone, or next to a cat that could move?
                cat_nb = [n for n in nbs(c) if P[n] == 'w']
                mobile = any(P[m] == '.' for n in cat_nb for m in nbs(n))
                group_case[('next to a cat with room' if mobile else 'boxed in fully', outcome)] += 1
        # orange cats (one on the board: clean to follow)
        if len(bo) == 1 and len(ao) == 1:
            s0, s1 = bo[0], ao[0]
            if s0 == s1:
                oc['stayed'] += 1
            else:
                oc['jumped'] += 1
                r0, c0 = rc(s0); r1, c1 = rc(s1); odist[abs(r0 - r1) + abs(c0 - c1)] += 1
                empt = [i for i in range(1, 36) if P[i] == '.']
                onear.append((len([i for i in empt if abs(rc(i)[0] - r0) + abs(rc(i)[1] - c0) == 1]) / max(1, len(empt)), abs(r0 - r1) + abs(c0 - c1) == 1))
        elif len(bo) == 1 and len(ao) == 0 and A[bo[0]] == 't':
            oc['became tea'] += 1
    print('White Cats by empty neighbours (0, 1, 2+) and what happened:')
    for k in sorted(wc): print('  ', k, wc[k])
    print('Boxed-in White Cats:')
    for k in sorted(group_case): print('  ', k, group_case[k])
    if dirs:
        print('White Cat steps by direction (and how many choices it had):', dict(sorted(dirs.items())))
    print('Orange Cats (one on the board):', dict(oc), '| jump distances', dict(sorted(odist.items())))
    if onear:
        exp = sum(x for x, _ in onear) / len(onear); obs = sum(1 for _, y in onear if y) / len(onear)
        print(f'  landed next to its old square {obs:.0%} (random jumps would give {exp:.0%}) over {len(onear)} jumps')

study(pairs, 'all builds')
study(late, 'later builds only')

# several teas at once: where the Sundae lands
print('\n=== teas and Sundaes from boxed-in cats ===')
for p in pairs:
    B = cells(p['before']); A = cells(p['after'])
    newS = [i for i in range(1, 36) if A[i] == 's' and B[i] != 's']
    lostW = [i for i in range(1, 36) if B[i] == 'w' and A[i] != 'w']
    if newS and len(lostW) >= 2 and p['cur'] not in ('Oven',):
        print(p['t'][11:19], 'cur', p['cur'], 'cats gone from', lostW, 'new Sundae at', newS)
