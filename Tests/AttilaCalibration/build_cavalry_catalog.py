"""Offline catalog refresh; inputs must be the reviewed, load-order-resolved table/asset snapshot.
No game/runtime writes. Generated hashes bind structural graphs, not numeric combat buffs.
Usage: python build_cavalry_catalog.py EFFECTIVE_DB ASSET_ROOT BASE_ASSET_ROOT OUTPUT_CS RECEIPT_JSON
"""
import csv, hashlib, json, pathlib, re, sys, xml.etree.ElementTree as ET

db, assets, base_assets, output, receipt = map(pathlib.Path, sys.argv[1:])
def table(name, key):
    with (db / (name + '.tsv')).open(encoding='utf-8-sig', newline='') as stream:
        rows = list(csv.DictReader(stream, delimiter='\t'))[1:]
    return {r[key]: r for r in rows}
main = table('main_units_tables', 'unit'); land = table('land_units_tables', 'key')
mounts = table('mounts_tables', 'key'); entities = table('battle_entities_tables', 'key')
missiles = table('missile_weapons_tables', 'key'); projectiles = table('projectiles_tables', 'key')
with (db / 'unit_variants_tables.tsv').open(encoding='utf-8-sig', newline='') as stream:
    variants = list(csv.DictReader(stream, delimiter='\t'))[1:]
by_land = {}
for variant in variants: by_land.setdefault(variant['unit'], []).append(variant)
animations = {}
for root in [base_assets, assets]:
    for path in sorted((root / 'animations/animation_tables').glob('*.txt')):
        text = path.read_text(encoding='utf-8-sig', errors='strict')
        for match in re.finditer(r'animation_table\s+(\S+)\s*\{([^}]+)\}', text):
            animations[match[1]] = (match[2], str(path), hashlib.sha256(path.read_bytes()).hexdigest())
variant_files = {p.stem.lower(): p for p in (assets / 'variantmeshes/variantmeshdefinitions').glob('*.variantmeshdefinition')}
def horse_variant(name):
    path = variant_files.get(name.lower())
    if not path: return None
    try: tree = ET.parse(path)
    except ET.ParseError: return None
    models = [n.attrib['model'].replace('\\', '/').lower() for n in tree.iter() if 'model' in n.attrib]
    # A concrete horse body mesh is required; a horse-related filename alone is insufficient.
    if not any('/horse/bodyparts/' in m for m in models): return None
    if any('/elephant/' in m or '/camel/' in m for m in models): return None
    return {'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(), 'models': sorted(set(models))}
def fingerprint(m, l, mount, rider, horse):
    parts = ['earl-single-rider-v1']
    def add(name, row, fields):
        parts.extend(name + ':' + field + '=' + row.get(field, '') for field in fields.split())
    add('main', m, 'unit land_unit is_naval')
    add('land', l, 'category class man_animation man_entity mount engine animal chariot articulated_record armour attribute_group primary_missile_weapon')
    add('mount', mount, 'key animation entity variant')
    add('rider', rider, 'key type'); add('horse', horse, 'key type shape')
    if l.get('primary_missile_weapon'):
        missile = missiles[l['primary_missile_weapon']]
        add('missile', missile, 'key precursor default_projectile')
        add('projectile', projectiles[missile['default_projectile']], 'key category shot_type')
    for v in sorted(by_land.get(m['land_unit'], []), key=lambda v: v.get('faction', '')): add('visual', v, 'unit faction variant')
    return hashlib.sha256('\n'.join(parts).encode()).hexdigest().upper()
included = []; rejected = []
for key, m in sorted(main.items()):
    l = land.get(m.get('land_unit'), {}); mount = mounts.get(l.get('mount'), {})
    rider = entities.get(l.get('man_entity'), {}); horse = entities.get(mount.get('entity'), {})
    if not rider.get('key', '').startswith('earl_man_rider_'): continue
    reason = None; visual = horse_variant(mount.get('variant', ''))
    rider_anim = animations.get(l.get('man_animation')); horse_anim = animations.get(mount.get('animation'))
    if m.get('is_naval') == 'true' or l.get('category') != 'cavalry' or any(l.get(f) for f in ['engine','animal','chariot','articulated_record']): reason = 'special population'
    elif rider.get('type') != 'man' or horse.get('type') not in ['horse','giant']: reason = 'nonhorse entity'
    elif not (0 < int(l.get('num_mounts','0')) <= int(m.get('num_men','0'))): reason = 'invalid counts'
    elif not rider_anim or not horse_anim: reason = 'animation definition absent'
    elif not re.search(r'\bskeleton_type\s+rome_man_game\b', rider_anim[0]) or not re.search(r'\bskeleton_type\s+horse_r2\b', horse_anim[0]): reason = 'nonhorse animation skeleton'
    elif not re.search(r'\bmount_table\s+' + re.escape(mount['animation']) + r'\b', rider_anim[0]): reason = 'rider/mount animation pairing differs'
    elif not visual or not by_land.get(m['land_unit']): reason = 'horse body or rider variant absent'
    if reason: rejected.append({'unit':key,'reason':reason}); continue
    missile = missiles.get(l.get('primary_missile_weapon'), {})
    projectile = projectiles.get(missile.get('default_projectile'), {})
    ranged = missile.get('precursor') == 'false' and int(l.get('ammo','0')) > 0
    bow = ranged and projectile.get('category') in ['arrow','bolt']
    # These are descriptive reviewed asset classes, not invented numeric weight/armour ratings.
    armour = l.get('armour','')
    heavy = ('heavy' in armour or 'cataphract' in horse['key']) and l.get('class') in ['cav_shk','cav_mel','com','cav_mis']
    light = not heavy and l.get('class') in ['cav_shk','cav_mel','com','cav_mis'] and (not ranged or projectile.get('category') in ['javelin','axe'])
    roles = ','.join(x for x, yes in [('heavy',heavy),('light',light),('archer',bow)] if yes) or 'unclassified'
    included.append({'unit':key,'fingerprint':fingerprint(m,l,mount,rider,horse),'roles':roles,'sourceMen':int(m['num_men']),'sourceMounts':int(l['num_mounts']),
        'riderAnimationHash':hashlib.sha256(rider_anim[0].encode()).hexdigest().upper(),'mountAnimationHash':hashlib.sha256(horse_anim[0].encode()).hexdigest().upper(),
        'manAnimation':l['man_animation'],'mountAnimation':mount['animation'],'mountEntity':horse['key'],'mountEntityType':horse['type'],
        'riderAnimationSource':rider_anim[1:],'mountAnimationSource':horse_anim[1:],'horseVariant':visual})
lines = '\n'.join('|'.join([r['unit'],r['fingerprint'],r['roles'],r['horseVariant']['sha256'].upper(),r['riderAnimationHash'],r['mountAnimationHash']]) for r in included)
output.write_text('// Generated by Tests/AttilaCalibration/build_cavalry_catalog.py from reviewed active Earl graphs.\n'
    '// Offline graph evidence only; engine confirmation pending. Numeric combat values are not fingerprinted.\n'
    'namespace CrusaderWars.StatFidelity;\ninternal static partial class CavalryBodyAdapters\n{\n    private const string Catalog = """\n'+lines+'\n""";\n}\n',encoding='utf-8')
receipt.write_text(json.dumps({'engineValidated':False,'included':included,'rejected':rejected},indent=2),encoding='utf-8')
print('included',len(included),'rejected',len(rejected),'giant',sum(r['mountEntityType']=='giant'for r in included))
