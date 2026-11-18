# NEXT-ANDROID-02: единый набор батиметрии для FisherLogger (Windows) и FisherCompanion (Android).
# Источник — те же данные, что у карт NEXT-135: высоты TerrainData (heights_*.npy) и уровень воды сцены
# (SceneData.water, world_pos.y). Глубина = уровень воды − высота дна (мировые единицы Unity = метры).
# Оригинальные карты и данные не изменяются; результат пишется в отдельную папку (повторная запись запрещена).
import hashlib, json, math, os, sys, gzip
import numpy as np
from PIL import Image, ImageDraw

N135 = r'D:\TheFisherProject\Workbench\tests\NEXT-135\out'
REG = r'D:\TheFisherProject\Data\assets\maps\map_registry.json'
OUT = sys.argv[1] if len(sys.argv) > 1 else r'D:\TheFisherProject\Data\assets\bathymetry'
GRID = 1024        # сетка глубин для запросов «глубина в точке»
OVR = 2048         # размер прозрачного слоя = размер карты реестра
NODATA = 65535

LOCS = [  # все 36 водоёмов location_map / coverage_table NEXT-135
    1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 100, 101, 102, 180, 185]

def sha(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''): h.update(b)
    return h.hexdigest()

def sample(H, X, Z, x0, z0, sx, sz):
    R = H.shape[0]
    fx = np.clip((X - x0) / sx * (R - 1), 0, R - 1.000001); fz = np.clip((Z - z0) / sz * (R - 1), 0, R - 1.000001)
    i = np.minimum(fx.astype(int), R - 2); j = np.minimum(fz.astype(int), R - 2); tx = fx - i; tz = fz - j
    return H[j, i] * (1 - tx) * (1 - tz) + H[j, i + 1] * tx * (1 - tz) + H[j + 1, i] * (1 - tx) * tz + H[j + 1, i + 1] * tx * tz

def depth_grid(H, t, wl, frame, n):
    j, i = np.mgrid[0:n, 0:n].astype(np.float64)
    X = frame['min_x'] + (i + 0.5) / n * (frame['max_x'] - frame['min_x'])
    Z = frame['max_z'] - (j + 0.5) / n * (frame['max_z'] - frame['min_z'])      # строка 0 = север (как изображение карты)
    h = sample(H, X.ravel(), Z.ravel(), t['position'][0], t['position'][2], t['size'][0], t['size'][2]).reshape(n, n)
    return wl - h                                                                    # > 0 — под водой

def carto_mask(d, frame, py0, wl, n):
    """Маска слоя карты (carto-меши, серые области/постройки) — тем же правилом, что render_validate.py (NEXT-135)."""
    p = os.path.join(d, 'carto_tris.npz')
    img = Image.new('L', (n, n), 0)
    if os.path.exists(p):
        mz = np.load(p); dr = ImageDraw.Draw(img)
        for k in mz.files:
            for tri in mz[k]:
                if tri[:, 1].max() <= py0 + wl + 0.3 or tri[:, 1].min() >= py0 + 149.9: continue
                dr.polygon([((x - frame['min_x']) / (frame['max_x'] - frame['min_x']) * n, (frame['max_z'] - z) / (frame['max_z'] - frame['min_z']) * n) for x, y, z in tri], fill=255)
    return np.asarray(img) > 127

def eroded(mask, r):
    """True там, где весь квадрат (2r+1)² вокруг пикселя — вода (отсекает одиночные ямы рельефа для подписей)."""
    m = mask.astype(np.int32); n = m.shape[0]
    c = np.zeros((n + 1, n + 1), np.int64); c[1:, 1:] = m.cumsum(0).cumsum(1)
    y0 = np.clip(np.arange(n) - r, 0, n); y1 = np.clip(np.arange(n) + r + 1, 0, n)
    s = c[y1][:, y1] - c[y0][:, y1] - c[y1][:, y0] + c[y0][:, y0]
    full = (y1 - y0)[:, None] * (y1 - y0)[None, :]
    return mask & (s == full)

def nice_interval(maxd):
    for s in (0.5, 1, 2, 5, 10, 20, 25, 50, 100, 200):
        if maxd / s <= 6: return float(s)
    return 500.0

BASE_RGB = (14, 52, 96)            # одна холодная тональность; глубже — плотнее
LINE_RGB = (8, 28, 54)

def build(loc, reg_entry, names, out_entries):
    b = 'b%d' % loc
    d = os.path.join(N135, 'bundles', b)
    e = {'location_id': loc, 'name': names.get(loc, ''), 'bundle': b}
    if not os.path.isdir(d):
        e.update(status='NO_SOURCE', underwater_relief='нет данных', absolute_depth_m='нет',
                 note='бандл сцены не найден в данных NEXT-135 — рельефа нет'); return e
    info = json.load(open(os.path.join(d, 'info.json'), encoding='utf-8'))
    T = info.get('terrains', []); sd = (info.get('scenedata') or [None])[0]
    wl = sd['water']['world_pos'][1] if sd and isinstance(sd.get('water'), dict) and 'world_pos' in sd['water'] else None
    e['n_terrains'] = len(T); e['water_level_world_y'] = wl
    if len(T) != 1:
        e.update(status='NOT_IMPLEMENTED_MULTI_TERRAIN', underwater_relief='есть (%d террейна)' % len(T), absolute_depth_m='не определено',
                 note='несколько террейнов: активный террейн и рамка карты не определены (как в NEXT-135) — батиметрия не строится'); return e
    t = T[0]
    hp = os.path.join(d, t['heights_file'])
    e['source'] = {'terrain_data': t['terrain_data'], 'td_pid': t['td_pid'], 'heights_file': t['heights_file'], 'heights_sha256': sha(hp),
                   'terrain_position': t['position'], 'terrain_size': t['size'], 'resolution': t['resolution'],
                   'height_formula': 'Y = position.y + raw / 32766 * size.y'}
    if wl is None:
        e.update(status='NO_WATER_LEVEL', underwater_relief='?', absolute_depth_m='нет', note='в сцене нет объекта воды'); return e
    raw = np.load(hp).astype(np.float64)
    H = t['position'][1] + raw / 32766.0 * t['size'][1]
    if reg_entry is None:
        e.update(status='NOT_PUBLISHED_NO_VERIFIED_MAP', underwater_relief='есть', absolute_depth_m='вычислимо (не публикуется)',
                 note='рельеф есть, но карты этого водоёма нет в реестре (исключена в NEXT-135) — совмещение не подтверждено, слой не публикуется',
                 max_depth_m=round(float((wl - H).max()), 2)); return e
    frame = {k: reg_entry[k] for k in ('min_x', 'max_x', 'min_z', 'max_z')}
    # рамка реестра должна совпадать с рамкой террейна (иначе совмещение не гарантировано)
    tf = {'min_x': t['position'][0], 'max_x': t['position'][0] + t['size'][0], 'min_z': t['position'][2], 'max_z': t['position'][2] + t['size'][0]}
    e['frame_matches_terrain'] = all(abs(frame[k] - tf[k]) < 1e-3 for k in frame)
    if not e['frame_matches_terrain']:
        e.update(status='NOT_IMPLEMENTED_FRAME_MISMATCH', note='рамка реестра не совпадает с террейном'); return e
    if abs(reg_entry.get('water_level', wl) - wl) > 1e-6:
        e.update(status='NOT_IMPLEMENTED_WL_MISMATCH', note='уровень воды реестра не совпадает со сценой'); return e

    D = depth_grid(H, t, wl, frame, GRID)
    maskG = carto_mask(d, frame, t['position'][1], wl, GRID)
    water = (D > 0) & ~maskG
    e['frame'] = frame
    e['map_file'] = reg_entry['file']; e['map_sha256'] = reg_entry['sha256']; e['map_confidence'] = reg_entry['confidence']
    if not water.any():
        e.update(status='NO_UNDERWATER_RELIEF', underwater_relief='нет (рельеф выше уровня воды)', absolute_depth_m='нет'); return e
    maxd = float(D[water].max())
    iv = nice_interval(maxd)
    # --- сетка глубин (см), строка 0 = север
    cm = np.where(water, np.clip(np.round(D * 100), 0, NODATA - 1), NODATA).astype('<u2')
    gname = 'depth_%03d_%d.u16.gz' % (loc, GRID)
    gp = os.path.join(OUT, gname)
    if os.path.exists(gp): raise SystemExit('exists ' + gp)
    with open(gp, 'wb') as raw_f, gzip.GzipFile(fileobj=raw_f, mode='wb', compresslevel=9, mtime=0) as f: f.write(cm.tobytes())
    # --- прозрачный слой 2048: полосы + изобаты
    D2 = depth_grid(H, t, wl, frame, OVR); mask2 = carto_mask(d, frame, t['position'][1], wl, OVR); w2 = (D2 > 0) & ~mask2
    e['masked_water_fraction'] = round(float(((D2 > 0) & mask2).mean()), 4)
    band = np.where(w2, np.floor(D2 / iv).astype(int), -1)
    maxd = float(D2[w2].max()) if w2.any() else maxd
    nb = int(band.max()) + 1
    rgba = np.zeros((OVR, OVR, 4), np.uint8)
    for k in range(nb):
        m = band == k
        rgba[m, :3] = BASE_RGB; rgba[m, 3] = int(round(255 * min(0.12 + 0.11 * k, 0.70)))
    # изобаты: граница между полосами k-1 и k (k >= 1); береговая линия (k=0) не рисуется — она есть на карте
    edge = np.zeros((OVR, OVR), bool); lvl_of_edge = np.full((OVR, OVR), -1, int)
    for dj, di in ((0, 1), (1, 0)):
        a = band; bshift = np.roll(band, -dj, 0) if dj else np.roll(band, -di, 1)
        hi = np.maximum(a, bshift); lo = np.minimum(a, bshift)
        m = (a != bshift) & (lo >= 0) & (hi >= 1)
        edge |= m; lvl_of_edge = np.where(m, np.maximum(lvl_of_edge, hi), lvl_of_edge)
    edge[-1, :] = edge[:, -1] = False
    rgba[edge, :3] = LINE_RGB; rgba[edge, 3] = 230
    oname = 'bathy_%03d_%d.png' % (loc, OVR)
    op = os.path.join(OUT, oname)
    if os.path.exists(op): raise SystemExit('exists ' + op)
    Image.fromarray(rgba, 'RGBA').save(op, optimize=True)
    # --- подписи изобат: не более 3 на уровень, не ближе 1/6 карты друг к другу, всего ≤ 14
    labels = []; mind = OVR / 6.0
    rng = np.random.default_rng(loc)
    for k in range(1, nb):
        ys, xs = np.nonzero((lvl_of_edge == k) & eroded(w2, 6))
        if len(xs) == 0: continue
        idx = rng.permutation(len(xs))[:4000]; placed = 0
        for q in idx:
            x, y = xs[q], ys[q]
            if x < 40 or y < 40 or x > OVR - 40 or y > OVR - 40: continue
            if all((x - l['px']) ** 2 + (y - l['py']) ** 2 >= mind ** 2 for l in labels):
                labels.append({'px': int(x), 'py': int(y), 'depth_m': k * iv}); placed += 1
                if placed >= 3 or len(labels) >= 14: break
        if len(labels) >= 14: break
    core = eroded(w2, 6)
    if not core.any(): core = w2
    jj, ii = np.unravel_index(np.argmax(np.where(core, D2, -1)), D2.shape)
    def px2w(px, py):
        return (frame['min_x'] + (px + 0.5) / OVR * (frame['max_x'] - frame['min_x']), frame['max_z'] - (py + 0.5) / OVR * (frame['max_z'] - frame['min_z']))
    out_labels = []
    for l in labels:
        x, z = px2w(l['px'], l['py']); out_labels.append({'x': round(x, 2), 'z': round(z, 2), 'depth_m': l['depth_m'], 'kind': 'isobath'})
    mx, mz = px2w(ii, jj); out_labels.append({'x': round(mx, 2), 'z': round(mz, 2), 'depth_m': round(float(D2[jj, ii]), 1), 'kind': 'max'})
    e.update(status='IMPLEMENTED', underwater_relief='есть', absolute_depth_m='да (м)',
             depth_unit='m', depth_formula='depth = water_level_world_y − terrain_world_y', water_level_world_y=wl,
             max_depth_m=round(maxd, 2), mean_depth_m=round(float(D[water].mean()), 2), water_fraction=round(float(water.mean()), 4),
             coverage='100% рамки карты (рамка = террейн), кроме областей, закрытых слоем карты игры (carto-меши); объекты сцены (камни, коряги, сваи) в рельеф не входят',
             contour_interval_m=iv, bands=[{'from_m': k * iv, 'to_m': (k + 1) * iv, 'rgb': list(BASE_RGB), 'alpha': round(min(0.12 + 0.11 * k, 0.70), 2)} for k in range(nb)],
             grid={'file': gname, 'width': GRID, 'height': GRID, 'dtype': 'uint16 LE, centimetres, row 0 = north (max_z)', 'nodata': NODATA,
                   'sha256': sha(gp), 'cell_center': 'x = min_x + (i+0.5)/W*(max_x-min_x), z = max_z - (j+0.5)/H*(max_z-min_z)'},
             overlay={'file': oname, 'width': OVR, 'height': OVR, 'sha256': sha(op),
                      'transform': 'same as map registry: px = (X-min_x)/(max_x-min_x)*W, py = (max_z-Z)/(max_z-min_z)*H'},
             labels=out_labels,
             depth_confidence='CONFIRMED (формула высоты: NEXT-135, |dY| точек сцены; масштаб: depth_cm записанных забросов = уровень воды − Y наживки)',
             alignment_confidence=reg_entry['confidence'])
    return e

def main():
    if os.path.exists(OUT) and os.listdir(OUT): raise SystemExit('output exists and is not empty: ' + OUT)
    os.makedirs(OUT, exist_ok=True)
    reg = json.load(open(REG, encoding='utf-8'))
    regmap = {m['location_id']: m for m in reg['maps']}
    names = {}
    for line in open(os.path.join(N135, 'coverage_table.md'), encoding='utf-8'):
        p = [c.strip() for c in line.split('|')]
        if len(p) > 3 and p[1].isdigit(): names[int(p[1])] = p[2]
    entries = []
    for loc in LOCS:
        e = build(loc, regmap.get(loc), names, entries); entries.append(e)
        print(loc, e['status'], e.get('max_depth_m'), e.get('contour_interval_m'), flush=True)
    doc = {'format': 1, 'generated_by': 'NEXT-ANDROID-02 build_bathymetry.py', 'source': 'NEXT-135 TerrainData heights + SceneData.water (game resources)',
           'depth_definition': 'depth (m) = SceneData.water.world_pos.y − terrain world height; 1 Unity unit = 1 m (checked against recorded depth_cm/cast_y)',
           'coordinate_system': reg.get('coordinate_system'), 'map_registry_sha256': sha(REG), 'waterbodies': entries}
    with open(os.path.join(OUT, 'bathymetry_registry.json'), 'w', encoding='utf-8') as f: json.dump(doc, f, ensure_ascii=False, indent=1)

if __name__ == '__main__':
    main()
