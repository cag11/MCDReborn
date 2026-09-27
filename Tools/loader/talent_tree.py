"""
The talent tree: every node, where it sits, what it links to and what it does - written out as
MCDSaveEdit/Logic/Talents.json, which MCD Reborn carries (it registers the effects and tells the
plugin) and build_talents.py reads (it draws the tree in the game).

    python talent_tree.py

Laid out the way Path of Exile's passive tree is (its published export was measured for this, not
copied): nodes sit in GROUPS, each on ORBITS round the group's centre - radii 0/82/162/335/493 with
1/6/16/16/40 slots there, scaled down here - and nodes on one orbit of one group are joined by arcs.
Most groups are a notable with two to five small nodes on the second orbit; single "travel" nodes
link groups; keystones stand alone at the far ends. Most nodes have two or three links.

The shape: a start in the middle and an inner ring round it; six regions (Warrior, Ranger, Mystic,
Guardian, Rogue, Shepherd), each the same plan turned to its own angle and filled with its own
effects - a wheel, two side clusters that bridge to the neighbouring regions, a fork into two arc
clusters, a great wheel on the axis, two outer arc clusters each leading to a keystone, and small
branches off the travel paths - and an outer ring round everything.

What a node does is one or more of the game's own armour properties:

  * scaled   - a copy of the property whose one number (at +0x128 on every source here) the plugin
               sets from the factors of the nodes taken for it, from doing nothing (0) to what the
               game's own does (1) and on past it.
  * raw      - the game's own property, as it is. Keystones: the mechanics with no number to scale.

Each source has a CAP: what every node of it in the whole tree adds up to. Factors are scaled to
meet it, so no build, however it is spent, reaches a number that breaks the game (a 100% chance
not to be hit, a cooldown of nothing).
"""

import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', '..', 'MCDSaveEdit', 'Logic', 'Talents.json')

#source: (the game's own number, what "does nothing" is, how the line reads, cap on the whole tree's sum)
SOURCES = {
    'MeleeDamageBoost':      (1.30, 1.0, '+{}% melee damage', 8.0),
    'MeleeAttackSpeedBoost': (1.25, 1.0, '+{}% melee attack speed', 4.0),
    'RangedDamageBoost':     (1.30, 1.0, '+{}% ranged damage', 8.0),
    'ItemDamageBoost':       (1.50, 1.0, '+{}% artifact damage', 5.0),
    'ItemCooldownDecrease':  (0.60, 1.0, '-{}% artifact cooldown', 1.5),
    'SoulGatheringBoost':    (1.50, 1.0, '+{}% souls gathered', 4.0),
    'DodgeSpeedIncrease':    (1.50, 1.0, '+{}% roll speed', 3.0),
    'MoveSpeedAura':         (1.15, 1.0, '+{}% movement speed aura', 3.0),
    'AllyDamageBoost':       (1.20, 1.0, '+{}% weapon damage aura for allies', 6.0),
    'DamageAbsorption':      (0.90, 1.0, '{}% damage reduction', 3.0),
    'LifeStealAura':         (0.06, 0.0, '{}% life steal aura', 4.0),
    'HealingAura':           (0.25, 0.0, '+{}% healing', 3.0),
    'MissChance':            (0.30, 0.0, '{}% chance to negate damage', 1.5),
    'TeleportChance':        (0.05, 0.0, '{}% chance to teleport away when hit', 4.0),
}

#The game's own properties used as they are, and what they do in the game's words.
RAW = {
    'Heavyweight':              (27, 'Resist knockback'),
    'IncreasedArrowBundleSize': (8, 'More arrows in every arrow bundle'),
    'DodgeGhostForm':           (24, 'Briefly gain Ghost Form when rolling'),
    'ItemCooldownReset':        (30, 'Reset artifact cooldowns on potion use'),
    'EnvironmentalProtection':  (28, 'Environmental damage resistance'),
    'EmeraldShield':            (29, 'Brief damage immunity when you collect an emerald'),
    'DodgeInvulnerability':     (21, 'Brief invulnerability when rolling'),
    'InstantTransmission':      (38, 'Roll to teleport'),
    'AreaHeal':                 (15, 'Health potions heal nearby allies'),
    'Beekeeper':                (25, 'Chance to summon a bee when hit'),
    'PetBat':                   (14, 'Gives you a pet bat'),
    'DodgeRoot':                (26, 'Rolling traps and poisons nearby mobs'),
}

#Weights before the caps: a small node's one effect, a notable's first and second.
W_SMALL, W_NOTABLE, W_SECOND = 1.0, 3.5, 1.5

#Each region: name, angle (degrees, 0 = right, 90 = down), its three sources (first leads), its
#two keystones (name, raw key).
REGIONS = [
    ('Warrior', -90, ['MeleeDamageBoost', 'MeleeAttackSpeedBoost', 'LifeStealAura'],
     [('Unstoppable', 'Heavyweight'), ('Earthbreaker', 'DodgeRoot')]),
    ('Ranger', -30, ['RangedDamageBoost', 'DodgeSpeedIncrease', 'MoveSpeedAura'],
     [('Full Quiver', 'IncreasedArrowBundleSize'), ('Ghost Step', 'DodgeGhostForm')]),
    ('Mystic', 30, ['ItemDamageBoost', 'ItemCooldownDecrease', 'SoulGatheringBoost'],
     [('Refreshing Brew', 'ItemCooldownReset'), ('Blink Walker', 'InstantTransmission')]),
    ('Guardian', 90, ['DamageAbsorption', 'HealingAura', 'MissChance'],
     [('Warded', 'EnvironmentalProtection'), ('Emerald Aegis', 'EmeraldShield')]),
    ('Rogue', 150, ['MeleeAttackSpeedBoost', 'TeleportChance', 'DodgeSpeedIncrease'],
     [('Untouchable', 'DodgeInvulnerability'), ('Night Companion', 'PetBat')]),
    ('Shepherd', 210, ['AllyDamageBoost', 'HealingAura', 'SoulGatheringBoost'],
     [('Field Medic', 'AreaHeal'), ('Swarm', 'Beekeeper')]),
]

#Small node names, by source - a PoE tree names its small nodes by what they give.
SMALL_NAMES = {
    'MeleeDamageBoost': 'Melee Damage', 'MeleeAttackSpeedBoost': 'Attack Speed',
    'RangedDamageBoost': 'Ranged Damage', 'ItemDamageBoost': 'Artifact Damage',
    'ItemCooldownDecrease': 'Artifact Cooldown', 'SoulGatheringBoost': 'Souls',
    'DodgeSpeedIncrease': 'Roll Speed', 'MoveSpeedAura': 'Swiftness', 'AllyDamageBoost': 'Leadership',
    'DamageAbsorption': 'Toughness', 'LifeStealAura': 'Life Steal', 'HealingAura': 'Healing',
    'MissChance': 'Evasion', 'TeleportChance': 'Blink',
}

#Notable names, by their first source, used in turn.
NOTABLE_NAMES = {
    'MeleeDamageBoost': ['Brute Force', 'Heavy Blows', 'Crushing Might', 'Iron Fist', 'Savage Edge', 'Warlord',
                         'Cleaver', 'Bone Breaker', 'Battle Hardened', 'Blade Master'],
    'MeleeAttackSpeedBoost': ['Flurry', 'Whirlwind', 'Quick Hands', 'Relentless', 'Frenzy', 'Blur of Steel',
                              'Swift Strikes', 'Tempest', 'Twin Fangs', 'Rapid Assault'],
    'RangedDamageBoost': ['Deadeye', 'Marksman', 'Piercing Aim', 'Hunter', 'Longshot', 'Eagle Eye',
                          'Sharpshooter', 'Volley', 'True Flight', 'Heartseeker'],
    'ItemDamageBoost': ['Arcane Power', 'Spellbinder', 'Overcharge', 'Runic Might', 'Conduit', 'Mastermind',
                        'Relic Keeper', 'Wild Magic', 'Arcane Surge', 'Enchanter'],
    'ItemCooldownDecrease': ['Quickened', 'Timeless', 'Clockwork', 'Hastened Mind', 'Sands of Time', 'Recharge',
                             'Ever Ready', 'Swift Casting', 'Momentum', 'Flow'],
    'SoulGatheringBoost': ['Soul Harvest', 'Reaper', 'Soul Eater', 'Spirit Well', 'Soul Binder', 'Hungering Soul',
                           'Wraith Touch', 'Soul Lantern', 'Gathering Dark', 'Soul Tide'],
    'DodgeSpeedIncrease': ['Skirmisher', 'Acrobat', 'Tumbler', 'Quick Roll', 'Nimble', 'Evasive Manoeuvre',
                           'Light Step', 'Slippery', 'Dancer', 'Windborne'],
    'MoveSpeedAura': ['Fleet Footed', 'Wanderer', 'Pathfinder', 'Tailwind', 'Trailblazer', 'Swift Company',
                      'Road Runner', 'Gale', 'Scout', 'Stride'],
    'AllyDamageBoost': ['Rallying Cry', 'Warbanner', 'Inspiration', 'Commander', 'Pack Leader', 'Battle Hymn',
                        'War Drums', 'Captain', 'Kinship', 'United Front'],
    'DamageAbsorption': ['Iron Hide', 'Bulwark', 'Stone Skin', 'Unbreakable', 'Fortress', 'Steadfast',
                         'Juggernaut', 'Iron Will', 'Anvil', 'Last Stand'],
    'LifeStealAura': ['Bloodthirst', 'Vampirism', 'Crimson Tide', 'Leech', 'Blood Pact', 'Sanguine',
                      'Life Drinker', 'Red Harvest', 'Blood Rite', 'Thirst'],
    'HealingAura': ['Mender', 'Lifegiver', 'Soothing Light', 'Renewal', 'Restoration', 'Vitality',
                    'Healing Hands', 'Sanctuary', 'Second Wind', 'Blessed'],
    'MissChance': ['Evasion', 'Phantom', 'Elusive', 'Shadow Dance', 'Mist Walker', 'Deflection',
                   'Unseen', 'Sidestep', 'Blur', 'Will o Wisp'],
    'TeleportChance': ['Blink', 'Phase Shift', 'Ender Step', 'Displacement', 'Warp', 'Vanishing Act',
                       'Rift Walker', 'Void Step', 'Flicker', 'Slipstream'],
}

#Path of Exile's orbits, scaled to this tree's units: radius and slots.
SCALE = 0.8
ORBIT_R = [0.0, 82 * SCALE, 162 * SCALE, 335 * SCALE, 493 * SCALE]
ORBIT_SLOTS = [1, 6, 16, 16, 40]

INNER_RING = 300.0          # 18 nodes round the start, 3 to a region
OUTER_RING = 2250.0         # 36 nodes round everything, 6 to a region
BOUNDARY = 1450.0           # the wheels between regions


class Tree(object):
    def __init__(me):
        me.nodes = []
        me.edges = []           # [a, b] straight, [a, b, cx, cy] an arc round (cx, cy)
        me.groups = []          # a cluster's centre, size and picture, drawn faint behind it
        me.used_names = {}

    def add(me, name, kind, x, y, region, effects):
        node = {'index': len(me.nodes), 'name': name, 'kind': kind, 'x': round(x, 1), 'y': round(y, 1),
                'region': region, 'effects': effects}
        me.nodes.append(node)
        return node['index']

    def link(me, a, b, centre=None):
        if a == b:
            return
        for e in me.edges:
            if (e[0], e[1]) in ((a, b), (b, a)):
                return
        me.edges.append([a, b] if centre is None else [a, b, round(centre[0], 1), round(centre[1], 1)])

    def pos(me, i):
        return me.nodes[i]['x'], me.nodes[i]['y']

    def notable_name(me, source):
        names = NOTABLE_NAMES[source]
        used = me.used_names.get(source, 0)
        me.used_names[source] = used + 1
        name = names[used % len(names)]
        return name if used < len(names) else '%s %s' % (name, ['II', 'III', 'IV'][min(2, used // len(names) - 1)])

    #--- what nodes do ------------------------------------------------------------------------------
    def small(me, x, y, region, source):
        return me.add(SMALL_NAMES[source], 'small', x, y, region, [{'source': source, 'weight': W_SMALL}])

    def notable(me, x, y, region, source, second):
        effects = [{'source': source, 'weight': W_NOTABLE}]
        if second and second != source:
            effects.append({'source': second, 'weight': W_SECOND})
        return me.add(me.notable_name(source), 'notable', x, y, region, effects)

    def keystone(me, x, y, region, name, key):
        return me.add(name, 'keystone', x, y, region, [{'raw': key, 'rawId': RAW[key][0]}])

    #--- shapes -------------------------------------------------------------------------------------
    def path(me, a, b, region, sources, count):
        """`count` small nodes on a straight line from a to b, linked a - ... - b."""
        (ax, ay), (bx, by) = me.pos(a), me.pos(b)
        previous = a
        for step in range(1, count + 1):
            t = step / float(count + 1)
            here = me.small(ax + (bx - ax) * t, ay + (by - ay) * t, region, sources[(step - 1) % len(sources)])
            me.link(previous, here)
            previous = here
        me.link(previous, b)

    def arc_cluster(me, frame, cu, cv, toward, region, sources, smalls, second, orbit=2, icon=None):
        """
        A group: `smalls` small nodes and then a notable along one orbit round (cu, cv), starting
        on the side facing `toward` (the local point it is reached from) and going round the way
        that leads outward, a slot and a half at a time. Returns (entry, notable).
        """
        cx, cy = frame(cu, cv)
        step = 360.0 / ORBIT_SLOTS[orbit] * 1.5
        r = ORBIT_R[orbit]
        facing = math.degrees(math.atan2(toward[1] - cv, toward[0] - cu))
        ends = [cu + r * math.cos(math.radians(facing + sign * smalls * step)) for sign in (1, -1)]
        sign = 1 if ends[0] >= ends[1] else -1
        made = []
        for k in range(smalls + 1):
            gx, gy = frame(cu + r * math.cos(math.radians(facing + sign * k * step)),
                           cv + r * math.sin(math.radians(facing + sign * k * step)))
            if k < smalls:
                made.append(me.small(gx, gy, region, sources[k % len(sources)]))
            else:
                made.append(me.notable(gx, gy, region, sources[0], second))
        for a, b in zip(made, made[1:]):
            me.link(a, b, (cx, cy))
        me.groups.append({'x': round(cx, 1), 'y': round(cy, 1), 'r': round(r * 1.9, 1), 'icon': icon or sources[0]})
        return made[0], made[-1]

    def wheel(me, frame, cu, cv, region, plan, orbit=2, icon=None):
        """
        A ring of nodes round (cu, cv), all linked round. `plan` is a list of (local angle, kind,
        source, second) going round; returns the nodes in plan order.
        """
        cx, cy = frame(cu, cv)
        r = ORBIT_R[orbit]
        made = []
        for angle, kind, source, second in plan:
            gx, gy = frame(cu + r * math.cos(math.radians(angle)), cv + r * math.sin(math.radians(angle)))
            made.append(me.notable(gx, gy, region, source, second) if kind == 'N' else me.small(gx, gy, region, source))
        for i in range(len(made)):
            me.link(made[i], made[(i + 1) % len(made)], (cx, cy))
        me.groups.append({'x': round(cx, 1), 'y': round(cy, 1), 'r': round(r * 1.9, 1), 'icon': icon or plan[0][2]})
        return made


def frame_for(angle):
    """Local (u outward, v sideways) to the tree's (x, y), for a region at `angle`."""
    a = math.radians(angle)
    c, s = math.cos(a), math.sin(a)
    return lambda u, v: (u * c - v * s, u * s + v * c)


def build():
    t = Tree()
    start = t.add('Start', 'start', 0.0, 0.0, '', [])
    t.nodes[start]['text'] = 'Where every path begins.'

    count = len(REGIONS)
    ring = []                   # the inner ring, 3 a region, in angle order
    outer = []                  # the outer ring, 6 a region
    sides = []                  # each region's (left side notable, right side notable)
    axis_entries = []
    clusters = []               # each region's fork notables and its last outer ring node

    for index, (region, angle, (p, q, r3), keystones) in enumerate(REGIONS):
        f = frame_for(angle)
        mixed = [p, q, r3]

        #--- the inner ring's three, the axis one joined to the start through one small node ---------
        mine = []
        for local in (-20.0, 0.0, 20.0):
            x, y = f(INNER_RING * math.cos(math.radians(local)), INNER_RING * math.sin(math.radians(local)))
            mine.append(t.small(x, y, region, [q, p, r3][int(local / 20.0) + 1]))
        ring.extend(mine)
        x, y = f(150.0, 0.0)
        spoke = t.small(x, y, region, p)
        t.link(start, spoke)
        t.link(spoke, mine[1])

        #--- the first wheel, on the axis --------------------------------------------------------------
        wheel = t.wheel(f, 690.0, 0.0, region, [
            (180.0, 's', p, None), (225.0, 's', q, None), (270.0, 'N', q, p), (315.0, 's', q, None),
            (0.0, 's', p, None), (45.0, 's', r3, None), (90.0, 'N', r3, p), (135.0, 's', r3, None),
        ])
        t.path(mine[1], wheel[0], region, [p, q], 2)
        left_notable, top, right_notable = wheel[2], wheel[4], wheel[6]

        #--- a branch off the first travel path: a small cluster to one side -----------------------------
        x, y = f(470.0, 250.0 if index % 2 else -250.0)
        side_v = 360.0                  # every region's the same way round, so no two meet
        branch_entry, branch_notable = t.arc_cluster(f, 470.0, side_v, (282.0, side_v * 0.29), region, [r3, q], 2, p)
        t.link(mine[2], branch_entry)

        #--- the side clusters, which bridge to the next region --------------------------------------------
        l_entry, l_notable = t.arc_cluster(f, 880.0, -360.0, (690.0, -130.0), region, [r3, p], 2, q)
        r_entry, r_notable = t.arc_cluster(f, 880.0, 360.0, (690.0, 130.0), region, [q, p], 2, r3)
        t.path(left_notable, l_entry, region, [r3], 2)
        t.path(right_notable, r_entry, region, [q], 2)
        sides.append((l_notable, r_notable))

        #--- the fork: two arc clusters ------------------------------------------------------------------
        c2_entry, c2_notable = t.arc_cluster(f, 1200.0, -330.0, (820.0, 0.0), region, [q, q, p, q], 4, p)
        c3_entry, c3_notable = t.arc_cluster(f, 1200.0, 330.0, (820.0, 0.0), region, [r3, r3, p, r3], 4, q)
        t.path(top, c2_entry, region, [q], 2)
        t.path(top, c3_entry, region, [r3], 2)

        #--- the great wheel on the axis: twelve round, three of them notables ------------------------
        great = t.wheel(f, 1480.0, 0.0, region, [
            (180.0, 's', p, None), (210.0, 's', q, None), (240.0, 'N', p, q), (270.0, 's', q, None),
            (300.0, 's', r3, None), (330.0, 's', p, None), (0.0, 'N', p, r3), (30.0, 's', p, None),
            (60.0, 's', r3, None), (90.0, 's', q, None), (120.0, 'N', r3, q), (150.0, 's', r3, None),
        ], orbit=3)
        t.path(top, great[0], region, [p], 3)
        #the fork's notables join the great wheel too, so the region is a web and not a tree
        t.link(c2_notable, great[3])
        t.link(c3_notable, great[9])

        #--- the outer arc clusters, and a keystone beyond each ------------------------------------------
        c4_entry, c4_notable = t.arc_cluster(f, 1760.0, -640.0, (1300.0, -500.0), region, [p, p, q], 3, r3)
        c5_entry, c5_notable = t.arc_cluster(f, 1760.0, 640.0, (1300.0, 500.0), region, [q, r3, q], 3, p)
        t.path(c2_notable, c4_entry, region, [p, q], 2)
        t.path(c3_notable, c5_entry, region, [q, r3], 2)
        for side, (notable_at, (k_name, k_key)) in enumerate(((c4_notable, keystones[0]), (c5_notable, keystones[1]))):
            x, y = f(1990.0, -560.0 if side == 0 else 560.0)
            key = t.keystone(x, y, region, k_name, k_key)
            t.path(notable_at, key, region, [p if side == 0 else q], 2)

        #--- a second branch, off the path out of the great wheel ---------------------------------------
        exit_top = great[6]
        x, y = f(2380.0 * math.cos(math.radians(0.0)), 0.0)
        outer_entry_at = len(outer)
        for local in (-25.0, -15.0, -5.0, 5.0, 15.0, 25.0):
            gx, gy = f(OUTER_RING * math.cos(math.radians(local)), OUTER_RING * math.sin(math.radians(local)))
            outer.append(t.small(gx, gy, region, [p, q, r3][int((local + 25.0) / 10.0) % 3]))
        axis_entries.append(outer[outer_entry_at + 2])
        b_entry, b_notable = t.arc_cluster(f, 2000.0, 0.0, (1748.0, 0.0), region, [q, r3, p], 3, q)
        t.path(exit_top, b_entry, region, [p], 1)
        t.link(b_notable, outer[outer_entry_at + 3 if index % 2 else outer_entry_at + 2])
        clusters.append((c2_notable, c3_notable, outer[outer_entry_at + 5]))

    #--- the rings, all the way round ---------------------------------------------------------------
    for i in range(len(ring)):
        t.link(ring[i], ring[(i + 1) % len(ring)], (0.0, 0.0))
    for i in range(len(outer)):
        t.link(outer[i], outer[(i + 1) % len(outer)], (0.0, 0.0))
    #--- between neighbouring regions: a bridge from side cluster to side cluster, and a wheel on the
    #boundary joined to both regions' fork notables and out to the outer ring -------------------------
    for i in range(count):
        j = (i + 1) % count
        region, angle, (p, q, r3), _ = REGIONS[i]
        _, _, (pj, qj, rj), _ = REGIONS[j]
        t.path(sides[i][1], sides[j][0], region, [r3, rj], 2)
        f = frame_for(angle + 30.0)
        wheel = t.wheel(f, BOUNDARY, 0.0, region, [
            (180.0, 's', q, None), (240.0, 's', pj, None), (300.0, 's', qj, None),
            (0.0, 'N', p, pj), (60.0, 's', r3, None), (120.0, 's', p, None),
        ])
        t.path(clusters[i][1], wheel[1], region, [q], 1)
        t.path(clusters[j][0], wheel[5], REGIONS[j][0], [qj], 1)
        t.path(wheel[3], clusters[i][2], region, [p, pj], 2)

    #--- the numbers: every source's weights scaled so the whole tree adds up to its cap -------------
    totals = {}
    for node in t.nodes:
        for e in node['effects']:
            if 'source' in e:
                totals[e['source']] = totals.get(e['source'], 0.0) + e['weight']
    for node in t.nodes:
        lines = []
        for e in node['effects']:
            if 'source' in e:
                e['factor'] = round(e.pop('weight') * SOURCES[e['source']][3] / totals[e['source']], 4)
                lines.append(effect_text(e['source'], e['factor']))
            else:
                lines.append(RAW[e['raw']][1])
        if lines:
            node['text'] = '\n'.join(lines)

    #Every node but the start is stored as one bit of a talent currency, 30 to a currency.
    for node in t.nodes:
        if node['kind'] == 'start':
            continue
        bit = node['index'] - 1
        node['store'] = bit // 30
        node['bit'] = bit % 30

    xs = [n['x'] for n in t.nodes]
    ys = [n['y'] for n in t.nodes]
    return {
        'points': {'perLevels': 1, 'cap': 100000},     # a point a hero level, however high (the user's call)
        'respec': 500,
        'stores': (len(t.nodes) - 1 + 29) // 30,
        'bounds': [min(xs), min(ys), max(xs), max(ys)],
        'regions': [{'name': r[0], 'angle': r[1]} for r in REGIONS],
        'nodes': t.nodes,
        'edges': t.edges,
        'groups': t.groups,
    }, totals


def effect_text(source, factor):
    real, neutral, words, _ = SOURCES[source]
    value = neutral + (real - neutral) * factor
    percent = abs(value - neutral) * 100.0 if neutral == 1.0 else value * 100.0
    shown = ('%.1f' % percent).rstrip('0').rstrip('.')
    return words.format(shown)


def check(tree):
    """Everything joined up, nothing on top of anything else."""
    n = len(tree['nodes'])
    linked = {i: set() for i in range(n)}
    for e in tree['edges']:
        linked[e[0]].add(e[1])
        linked[e[1]].add(e[0])
    seen, todo = {0}, [0]
    while todo:
        for j in linked[todo.pop()]:
            if j not in seen:
                seen.add(j)
                todo.append(j)
    if len(seen) != n:
        print('NOT JOINED: %d of %d reachable' % (len(seen), n))
    close = []
    nodes = tree['nodes']
    for i in range(n):
        for j in range(i + 1, n):
            d = math.hypot(nodes[i]['x'] - nodes[j]['x'], nodes[i]['y'] - nodes[j]['y'])
            if d < 44.0:
                close.append((round(d), nodes[i]['name'], nodes[i]['region'], (nodes[i]['x'], nodes[i]['y']),
                              nodes[j]['name'], nodes[j]['region'], (nodes[j]['x'], nodes[j]['y'])))
    if close:
        print('%d pair(s) closer than 44:' % len(close))
        for c in close[:12]:
            print('   ', c)


def main():
    tree, totals = build()
    check(tree)
    with open(OUT, 'w', encoding='utf-8', newline='\n') as out:
        json.dump(tree, out, indent=1)
    kinds = {}
    for n in tree['nodes']:
        kinds[n['kind']] = kinds.get(n['kind'], 0) + 1
    print('%d nodes %s, %d edges (%d arcs), %d groups, %d stores, bounds %s -> %s'
          % (len(tree['nodes']), kinds, len(tree['edges']), sum(1 for e in tree['edges'] if len(e) == 4),
             len(tree['groups']), tree['stores'], tree['bounds'], os.path.normpath(OUT)))


if __name__ == '__main__':
    main()
