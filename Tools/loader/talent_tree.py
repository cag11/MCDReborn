"""
The talent tree: every node, where it sits, what it links to and what it does - written out as
MCDSaveEdit/Logic/Talents.json, which MCD Reborn carries (it registers the effects and tells the
plugin) and build_talents.py reads (it draws the tree in the game).

    python talent_tree.py

A web in the manner of Path of Exile's passive tree. A start in the middle; six sections round it,
each a spoke of small nodes to a notable, a fork into two paths each ending in a notable, and a
keystone at the far end reached from either. An inner and an outer ring join each section to its
neighbours, so a path can cross over.

What a node does is one of the game's own armour properties:

  * scaled   - a copy of the property whose one number (at +0x128 on every source here) the plugin
               sets to `factor` of the way from doing nothing to what the game's own does, exactly
               as a gem grade is made. Small nodes and most notables.
  * raw      - the game's own property, as it is. Keystones and the mechanics with no number to
               scale (Pet Bat, Ghost Form roll, Refreshing Brew...).

The game's own numbers for the scaled sources were read out of the running game (the plugin logs
them when it scales a gem); they are here only to write the tooltips.
"""

import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', '..', 'MCDSaveEdit', 'Logic', 'Talents.json')

#source: (the game's own number, what "does nothing" is, how the line reads with a percent)
SOURCES = {
    'MeleeDamageBoost':      (1.30, 1.0, '+{}% melee damage'),
    'MeleeAttackSpeedBoost': (1.25, 1.0, '+{}% melee attack speed'),
    'RangedDamageBoost':     (1.30, 1.0, '+{}% ranged damage'),
    'ItemDamageBoost':       (1.50, 1.0, '+{}% artifact damage'),
    'ItemCooldownDecrease':  (0.60, 1.0, '-{}% artifact cooldown'),
    'SoulGatheringBoost':    (1.50, 1.0, '+{}% souls gathered'),
    'DodgeSpeedIncrease':    (1.50, 1.0, '+{}% roll speed'),
    'MoveSpeedAura':         (1.15, 1.0, '+{}% movement speed aura'),
    'AllyDamageBoost':       (1.20, 1.0, '+{}% weapon damage aura for allies'),
    'DamageAbsorption':      (0.90, 1.0, '{}% damage reduction'),
    'LifeStealAura':         (0.06, 0.0, '{}% life steal aura'),
    'HealingAura':           (0.25, 0.0, '+{}% healing'),
    'MissChance':            (0.30, 0.0, '{}% chance to negate damage'),
    'TeleportChance':        (0.05, 0.0, '{}% chance to teleport away when hit'),
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

SMALL, NOTABLE, KEYSTONE = 0.12, 0.40, 1.0

#Each section: name, angle (degrees, 0 = right, 90 = down), spoke, notable, left path + notable,
#right path + notable, keystone. A small or notable is (source, factor) or ('raw', key) with a name.
SECTIONS = [
    ('Warrior', -90,
     ['MeleeDamageBoost', 'MeleeAttackSpeedBoost', 'MeleeDamageBoost'],
     ('Brute Force', 'MeleeDamageBoost', 0.40),
     (['MeleeAttackSpeedBoost', 'MeleeAttackSpeedBoost'], ('Flurry', 'MeleeAttackSpeedBoost', 0.50)),
     (['LifeStealAura', 'LifeStealAura'], ('Bloodthirst', 'LifeStealAura', 0.70)),
     ('Unstoppable', 'Heavyweight')),
    ('Ranger', -30,
     ['RangedDamageBoost', 'RangedDamageBoost', 'RangedDamageBoost'],
     ('Deadeye', 'RangedDamageBoost', 0.40),
     (['RangedDamageBoost', 'RangedDamageBoost'], ('Full Quiver', 'raw', 'IncreasedArrowBundleSize')),
     (['DodgeSpeedIncrease', 'DodgeSpeedIncrease'], ('Skirmisher', 'DodgeSpeedIncrease', 0.50)),
     ('Ghost Step', 'DodgeGhostForm')),
    ('Mystic', 30,
     ['ItemDamageBoost', 'ItemCooldownDecrease', 'ItemDamageBoost'],
     ('Arcane Power', 'ItemDamageBoost', 0.40),
     (['ItemCooldownDecrease', 'ItemCooldownDecrease'], ('Quickened', 'ItemCooldownDecrease', 0.40)),
     (['SoulGatheringBoost', 'SoulGatheringBoost'], ('Soul Harvest', 'SoulGatheringBoost', 0.50)),
     ('Refreshing Brew', 'ItemCooldownReset')),
    ('Guardian', 90,
     ['DamageAbsorption', 'DamageAbsorption', 'DamageAbsorption'],
     ('Iron Hide', 'DamageAbsorption', 0.60),
     (['MissChance', 'MissChance'], ('Evasion', 'MissChance', 0.40)),
     (['DamageAbsorption', 'DamageAbsorption'], ('Warded', 'raw', 'EnvironmentalProtection')),
     ('Emerald Aegis', 'EmeraldShield')),
    ('Rogue', 150,
     ['DodgeSpeedIncrease', 'MoveSpeedAura', 'DodgeSpeedIncrease'],
     ('Fleet Footed', 'MoveSpeedAura', 0.60),
     (['TeleportChance', 'TeleportChance'], ('Blink', 'TeleportChance', 0.80)),
     (['MissChance', 'MissChance'], ('Untouchable', 'raw', 'DodgeInvulnerability')),
     ('Shadow Step', 'InstantTransmission')),
    ('Shepherd', 210,
     ['AllyDamageBoost', 'HealingAura', 'AllyDamageBoost'],
     ('Rallying Cry', 'AllyDamageBoost', 0.50),
     (['HealingAura', 'HealingAura'], ('Field Medic', 'raw', 'AreaHeal')),
     (['MoveSpeedAura', 'MoveSpeedAura'], ('Swarm', 'raw', 'Beekeeper')),
     ('Night Companion', 'PetBat')),
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

#How far out each ring sits, in the tree's own units; drawn as an ellipse wider than tall.
R_SPOKE = [95, 160, 225]
R_NOTABLE = 300
R_FORK = [360, 420]
R_FORK_NOTABLE = 485
R_KEYSTONE = 565
FORK_SPREAD = 12.0
STRETCH_X, STRETCH_Y = 1.50, 0.80


def at(r, degrees):
    a = math.radians(degrees)
    return round(r * math.cos(a) * STRETCH_X, 1), round(r * math.sin(a) * STRETCH_Y, 1)


def effect_text(source, factor):
    real, neutral, words = SOURCES[source]
    value = neutral + (real - neutral) * factor
    percent = abs(value - neutral) * 100.0 if neutral == 1.0 else value * 100.0
    shown = ('%.1f' % percent).rstrip('0').rstrip('.')
    return words.format(shown)


def build():
    nodes = []
    edges = []

    def add(name, kind, x, y, section, effect):
        node = {'index': len(nodes), 'name': name, 'kind': kind, 'x': x, 'y': y, 'section': section}
        node.update(effect)
        nodes.append(node)
        return node['index']

    def scaled(source, factor):
        return {'source': source, 'factor': factor, 'text': effect_text(source, factor)}

    def raw(key):
        number, words = RAW[key]
        return {'raw': key, 'rawId': number, 'text': words}

    def link(a, b):
        if a != b and (a, b) not in edges and (b, a) not in edges:
            edges.append((a, b))

    start = add('Start', 'start', 0.0, 0.0, '', {'text': 'Where every path begins.'})

    spokes, inner_ends, left_ends, right_ends = [], [], [], []
    for name, angle, spoke, notable, left, right, keystone in SECTIONS:
        previous = start
        for r, source in zip(R_SPOKE, spoke):
            x, y = at(r, angle)
            here = add(SMALL_NAMES[source], 'small', x, y, name, scaled(source, SMALL))
            link(previous, here)
            previous = here
        inner_ends.append(previous)

        x, y = at(R_NOTABLE, angle)
        n_name, n_source, n_factor = notable
        centre = add(n_name, 'notable', x, y, name, scaled(n_source, n_factor))
        link(previous, centre)

        ends = []
        for side, (path, (p_name, p_source, p_value)) in ((-1, left), (1, right)):
            previous = centre
            for r, source in zip(R_FORK, path):
                x, y = at(r, angle + side * FORK_SPREAD)
                here = add(SMALL_NAMES[source], 'small', x, y, name, scaled(source, SMALL))
                link(previous, here)
                previous = here
            x, y = at(R_FORK_NOTABLE, angle + side * FORK_SPREAD)
            effect = raw(p_value) if p_source == 'raw' else scaled(p_source, p_value)
            end = add(p_name, 'notable', x, y, name, effect)
            link(previous, end)
            ends.append(end)
        left_ends.append(ends[0])
        right_ends.append(ends[1])

        x, y = at(R_KEYSTONE, angle)
        k_name, k_key = keystone
        top = add(k_name, 'keystone', x, y, name, raw(k_key))
        link(ends[0], top)
        link(ends[1], top)
        spokes.append(angle)

    #The rings: between one section and the next (clockwise), two small nodes each, one of each
    #section's first spoke source - so a ring node leans towards the section it is nearer.
    count = len(SECTIONS)
    for i in range(count):
        j = (i + 1) % count
        a0, a1 = SECTIONS[i][1], SECTIONS[j][1]
        if a1 < a0:
            a1 += 360
        source_a, source_b = SECTIONS[i][2][0], SECTIONS[j][2][0]

        #inner, at the spoke's last small node
        previous = inner_ends[i]
        for step, source in ((1, source_a), (2, source_b)):
            x, y = at(R_SPOKE[-1], a0 + (a1 - a0) * step / 3.0)
            here = add(SMALL_NAMES[source], 'small', x, y, 'Ring', scaled(source, SMALL))
            link(previous, here)
            previous = here
        link(previous, inner_ends[j])

        #outer, from one section's right fork notable to the next section's left one
        previous = right_ends[i]
        f0, f1 = a0 + FORK_SPREAD, a1 - FORK_SPREAD
        for step, source in ((1, source_a), (2, source_b)):
            x, y = at(R_FORK_NOTABLE, f0 + (f1 - f0) * step / 3.0)
            here = add(SMALL_NAMES[source], 'small', x, y, 'Ring', scaled(source, SMALL))
            link(previous, here)
            previous = here
        link(previous, left_ends[j])

    #Every node but the start is stored as one bit of a talent currency, 30 to a currency.
    for node in nodes:
        if node['kind'] == 'start':
            continue
        bit = node['index'] - 1
        node['store'] = bit // 30
        node['bit'] = bit % 30
        #One property per source, not per node: the plugin sums the factors of the nodes taken for it.
        node['id'] = 'MCDR_Talent' + node['source'] if 'source' in node else None

    return {
        'points': {'perLevels': 4, 'cap': 75},
        'respec': 500,
        'stores': (len(nodes) - 1 + 29) // 30,
        'nodes': nodes,
        'edges': [list(e) for e in edges],
    }


def main():
    tree = build()
    with open(OUT, 'w', encoding='utf-8', newline='\n') as out:
        json.dump(tree, out, indent=1)
    kinds = {}
    for n in tree['nodes']:
        kinds[n['kind']] = kinds.get(n['kind'], 0) + 1
    sources = sorted(set(n['source'] for n in tree['nodes'] if 'source' in n))
    print('%d nodes %s, %d edges, %d talent properties, %d stores -> %s'
          % (len(tree['nodes']), kinds, len(tree['edges']), len(sources), tree['stores'], os.path.normpath(OUT)))


if __name__ == '__main__':
    main()
