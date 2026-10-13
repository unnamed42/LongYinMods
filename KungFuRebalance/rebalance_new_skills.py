#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
为龙胤立志传 mod 的功法表补齐并再平衡 id 996~1050（游戏更新新增的功法）。

输入：
  KungfuSkillData.csv          mod 再平衡后的表（id 0~995）
  Vanilla_KungfuSkillData.csv  原版导出的表（id 0~1050，含未平衡的新功法）

输出：
  KungfuSkillData_v2.csv       996 行原样 + 55 行新功法平衡结果 = 1051 行
  rebalance_review.tsv         逐行「原值 → 新值 → 对标的老功法」，供人工审阅

方法：
  1) 在同「类别 × 等级」的老功法里找最相似的一条（NN），迁移它的改造
     比例 / 增量；
  2) 迁移结果会被夹回该分组的取值区间（防止个别手工离群值把新功法带歪）；
  3) 没有 NN 依据时退回组内众数规则。
"""
import csv
import math
from collections import Counter, defaultdict

BASE = "KungfuSkillData.csv"
VANILLA = "Vanilla_KungfuSkillData.csv"
OUT = "KungfuSkillData_v2.csv"
REVIEW = "rebalance_review.tsv"

COLS = ['id', '类别', '等级', '名字', '描述', '基础威力', '经验系数', '修炼效果',
        '运功效果', '威力系数', '修炼需求', '攻击范围', '伤害范围', '使用特效',
        '内力消耗', '所属势力', '使用目标', '攻击架势', '防御架势', '召唤ID',
        '使用次数', '动作', '子弹', '特效', '使用武器', '伤害顺序', '角色位移',
        'Trail', '隐藏']
I = {c: i for i, c in enumerate(COLS)}

MARTIAL = {'拳掌', '剑法', '刀法', '长兵', '奇门', '射术'}
NEILI = '内力'

# 根基/武学/生活属性的键名（在运功效果里每点价值 1.0）
BASE_ATTR = {'力道', '灵巧', '智力', '意志', '体质', '经脉', '内功', '轻功', '绝技',
             '拳掌', '剑法', '刀法', '长兵', '奇门', '射术', '毒术', '医术', '锻造',
             '采伐', '拉近'}

# 游戏 HeroSpeAddData.GetValue() 的权重表 —— 在活进程里逐键实测得到（<键>1 -> GetValue）。
# 未列出的键默认 100/点（特效类）；“XX潜力”固定 1/点。
# 这是游戏自己给「运功效果」算战力分的方式，也是校准内功/轻功/绝技的依据。
SPE_W = {
    '生命上限': 0.5, '内力上限': 0.25, '体力上限': 0.2,
    '护甲': 2.0, '减伤': 2.0, '自愈': 2.0, '无敌': 200.0, '反弹': 200.0,
    '护体真气': 50.0, '吸内': 50.0, '反伤吸内': 50.0, '药性': 50.0,
    '削内': 25.0, '反伤削内': 25.0,
    '移距': 5.0, '回体': 5.0, '回内': 666.667, '回血': 1000.0,
    '击退': 1.0, '装备负重': 1.0, '抗耐药性': 0.0, '内力': 0.0,
}
SPE_W_DEFAULT = 100.0


def spe_value(text):
    """按游戏口径把「运功效果」折成战力分"""
    total = 0.0
    for k, x in parse_pairs(text).items():
        if x is None:
            continue
        w = SPE_W.get(k)
        if w is None:
            w = 1.0 if k.endswith('潜力') or k in BASE_ATTR else SPE_W_DEFAULT
        total += w * x
    return total


# ---------------------------------------------------------------- CSV 原始行处理

def split_csv_line(line):
    out, buf, i, inq = [], [], 0, False
    while i < len(line):
        ch = line[i]
        if inq:
            if ch == '"':
                if i + 1 < len(line) and line[i + 1] == '"':
                    buf.append('"')
                    i += 2
                    continue
                inq = False
                i += 1
                continue
            buf.append(ch)
        else:
            if ch == '"':
                inq = True
            elif ch == ',':
                out.append(''.join(buf))
                buf = []
            else:
                buf.append(ch)
        i += 1
    out.append(''.join(buf))
    return out


def join_csv_line(fields):
    res = []
    for f in fields:
        f = '' if f is None else f
        res.append('"' + f.replace('"', '""') + '"' if (',' in f or '"' in f) else f)
    return ','.join(res)


def num(v):
    """按导出口径格式化数字：整数不带小数点"""
    if v is None:
        return ''
    if isinstance(v, float) and v.is_integer():
        return str(int(v))
    if isinstance(v, int):
        return str(v)
    return ('%.4f' % v).rstrip('0').rstrip('.')


def parse_pairs(text):
    """'力道3;拳掌2;内力0.2' -> {'力道':3.0,'拳掌':2.0,'内力':0.2}；保序"""
    d = {}
    if not text:
        return d
    for part in text.split(';'):
        part = part.strip()
        if not part:
            continue
        j = len(part)
        while j > 0 and (part[j - 1].isdigit() or part[j - 1] == '.'):
            j -= 1
        fraction = part[j:]
        d[part[:j]] = float(fraction) if fraction else None
    return d


def dump_pairs(d):
    return ';'.join('%s%s' % (k, num(v)) for k, v in d.items() if v is not None)


# ---------------------------------------------------------------- 读入

def load(path):
    with open(path, 'r', encoding='utf-8-sig', newline='') as fh:
        raw = fh.read()
    lines = raw.split('\r\n')
    if lines and lines[-1] == '':
        lines.pop()
    return lines[0], [split_csv_line(ln) for ln in lines[1:]]


header, mod_rows = load(BASE)
_, van_rows = load(VANILLA)

van_by_id = {int(r[I['id']]): r for r in van_rows}
mod_by_id = {int(r[I['id']]): r for r in mod_rows}
NEW_IDS = sorted(i for i in van_by_id if i not in mod_by_id)
OLD_IDS = sorted(mod_by_id)


def feat_of(co, yg):
    """相似度特征；两端口径必须一致"""
    if co:
        return {k: v for k, v in co.items() if k != NEILI and v is not None}
    return {k: v for k, v in yg.items()
            if v is not None and k not in ('生命上限', '内力上限')}


class Sk:
    """老功法：原版 + mod 后的成对视图"""

    def __init__(self, i):
        self.id = i
        self.vo = van_by_id[i]
        self.mo = mod_by_id[i]
        self.cat = self.vo[I['类别']]
        self.lv = int(self.vo[I['等级']])
        self.co_o = parse_pairs(self.vo[I['威力系数']])
        self.co_n = parse_pairs(self.mo[I['威力系数']])
        self.yg_o = parse_pairs(self.vo[I['运功效果']])
        self.yg_n = parse_pairs(self.mo[I['运功效果']])
        self.xl_o = parse_pairs(self.vo[I['修炼效果']])
        self.xl_n = parse_pairs(self.mo[I['修炼效果']])
        self.xq_o = parse_pairs(self.vo[I['修炼需求']])
        self.xq_n = parse_pairs(self.mo[I['修炼需求']])
        self.tx_o = parse_pairs(self.vo[I['使用特效']])
        self.tx_n = parse_pairs(self.mo[I['使用特效']])

    def feat(self):
        return feat_of(self.co_o, self.yg_o)

    def mana_mult(self):
        try:
            o = float(self.vo[I['内力消耗']] or 0)
            n = float(self.mo[I['内力消耗']] or 0)
        except ValueError:
            return None
        return (n / o) if o else None

    def pow_mult(self):
        try:
            o = float(self.vo[I['基础威力']] or 'nan')
            n = float(self.mo[I['基础威力']] or 'nan')
        except ValueError:
            return None
        if math.isnan(o) or math.isnan(n) or o == 0:
            return None
        return n / o

    def hp_mana_mult(self):
        """内功：生命上限+内力上限 的总量倍数"""
        po, pn = self.yg_o.get('生命上限'), self.yg_n.get('生命上限')
        mo, mn = self.yg_o.get('内力上限'), self.yg_n.get('内力上限')
        to = (po or 0) + (mo or 0)
        tn = (pn or 0) + (mn or 0)
        return (tn / to) if to and tn else None

    def added_effects(self):
        return {k: v for k, v in self.tx_n.items() if k not in self.tx_o}


OLD = [Sk(i) for i in OLD_IDS]
BY_GROUP = defaultdict(list)
for _s in OLD:
    BY_GROUP[(_s.cat, _s.lv)].append(_s)


def cosine(a, b):
    keys = set(a) | set(b)
    if not keys:
        return 0.0
    dot = sum(a.get(k, 0) * b.get(k, 0) for k in keys)
    na = math.sqrt(sum(v * v for v in a.values()))
    nb = math.sqrt(sum(v * v for v in b.values()))
    return dot / (na * nb) if na and nb else 0.0


def nearest(cat, lv, feat, co_new):
    best, bs = None, -1.0
    for s in BY_GROUP.get((cat, lv), []):
        sc = cosine(feat, s.feat())
        if NEILI in co_new and NEILI in s.co_o:
            sc += 0.25 * math.exp(-abs(co_new[NEILI] - s.co_o[NEILI]) * 5)
        if sc > bs:
            best, bs = s, sc
    return best, bs


# ---------------------------------------------------------------- 组内统计

def _bucket(level, neili, get):
    vals = []
    for s in OLD:
        if s.lv != level:
            continue
        on = s.co_o.get(NEILI)
        if (on is None) != (neili is None):
            continue
        if on is not None and abs(on - neili) > 1e-6:
            continue
        v = get(s)
        if v:
            vals.append(v)
    return sorted(vals)


def _mode(counter, default=None):
    return counter.most_common(1)[0][0] if counter else default


def _quantile(vals, q):
    if not vals:
        return None
    pos = q * (len(vals) - 1)
    lo = int(math.floor(pos))
    hi = min(lo + 1, len(vals) - 1)
    return vals[lo] + (vals[hi] - vals[lo]) * (pos - lo)


def mana_stats(level, neili):
    """(等级, 内力系数) -> (下四分位, 中位, 上四分位) 的内力消耗倍数。

    新功法可能出现组内没有的档位（如 0.5），此时退到「最接近的已有档位」。
    用分位数而不是 min/max，避免被个别手工离群值抬高。
    """
    v = _bucket(level, neili, lambda s: s.mana_mult())
    if not v:
        avail = {}
        for s in OLD:
            if s.lv != level or not s.mana_mult():
                continue
            avail.setdefault(s.co_o.get(NEILI) or 0.0, []).append(s.mana_mult())
        if avail:
            target = neili or 0.0
            near = min(avail, key=lambda k: abs(k - target))
            v = sorted(avail[near])
    if not v:
        return (1.0, 1.0, 1.0)
    return (_quantile(v, 0.25), _quantile(v, 0.5), _quantile(v, 0.75))

def pow_range(level):
    v = sorted(s.pow_mult() for s in OLD
               if s.lv == level and s.pow_mult())
    return (v[0], v[-1]) if v else None


def pow_center(level):
    v = sorted(s.pow_mult() for s in OLD
               if s.lv == level and s.pow_mult())
    return v[len(v) // 2] if v else 1.0


def coef_delta_mode(level, key, val):
    c = Counter()
    for s in OLD:
        if s.lv != level:
            continue
        o, n = s.co_o.get(key), s.co_n.get(key)
        if o == val and n is not None:
            c[round(n - o, 4)] += 1
    return _mode(c, 0.0)


def neili_map_mode(level, val):
    c = Counter()
    for s in OLD:
        if s.lv != level:
            continue
        o, n = s.co_o.get(NEILI), s.co_n.get(NEILI)
        if o == val and n is not None:
            c[n] += 1
    return _mode(c, val)


def xq_delta_mode(level, key):
    c = Counter()
    for s in OLD:
        if s.lv != level:
            continue
        o, n = s.xq_o.get(key), s.xq_n.get(key)
        if o is not None and n is not None:
            c[round(n - o, 1)] += 1
    return _mode(c, 0.0)


def yg_shape_mode(level):
    """外功新增运功效果的 (主武器属性值, 副属性值) 众数"""
    mains, sides = Counter(), Counter()
    for s in OLD:
        if s.lv != level or s.cat not in MARTIAL:
            continue
        if not s.yg_n:
            continue
        m = s.yg_n.get(s.cat)
        if m is not None:
            mains[m] += 1
        for k, v in s.yg_n.items():
            if k != s.cat and v is not None:
                sides[v] += 1
                break
    fallback = (8.0, 2.0) if level == 5 else (2.0, 2.0)
    return (_mode(mains, fallback[0]), _mode(sides, fallback[1]))


def hp_mana_center(level):
    v = sorted(s.hp_mana_mult() for s in OLD
               if s.lv == level and s.cat == '内功' and s.hp_mana_mult())
    return v[len(v) // 2] if v else 2.0


def clamp(x, rng):
    return min(max(x, rng[0]), rng[1]) if rng else x

# 命中加成按类别的基准值（L4/L5 外功新增「命中」的众数），仅作取值区间的兜底
HIT_BASE = {4: {'拳掌': 0.08, '剑法': 0.08, '刀法': 0.06, '长兵': 0.04,
                '奇门': 0.05, '射术': 0.04},
            5: {'拳掌': 0.15, '剑法': 0.15, '刀法': 0.10, '长兵': 0.12,
                '奇门': 0.10, '射术': 0.12}}

# 战力校准覆盖表（id -> 基础威力）。
# 来源：把本表灌进活进程，用游戏自己的 KungfuSkillLvData.GetSkillFightScore()
# 对标同「类别 x 等级」老功法的评分区间；只有越界的条目才在这里被钉住。
CALIB_BASE = {1020: 34, 1024: 29, 1028: 28, 1039: 68,
              1041: 74, 1044: 63, 1045: 62, 1046: 85}

def hit_range(level, cat):
    """该 (等级, 类别) 老功法新增「命中」的取值区间"""
    v = sorted(x for s in OLD if s.lv == level and s.cat == cat
               for x in [s.added_effects().get('命中')] if x is not None)
    if v:
        return (v[0], v[-1])
    base = HIT_BASE.get(level, {}).get(cat, 0.05)
    return (base * 0.5, base * 1.4)


def nearest_in_bucket(cat, lv, feat, neili):
    """限定在相同内力系数档位里找 NN（内力消耗的倍数只在该档位内有可比性）"""
    pool = []
    for s in BY_GROUP.get((cat, lv), []):
        on = s.co_o.get(NEILI)
        if (on is None) != (neili is None):
            continue
        if on is not None and abs(on - neili) > 1e-6:
            continue
        pool.append(s)
    if not pool:
        return None
    best, bs = None, -1.0
    for s in pool:
        sc = cosine(feat, s.feat())
        if sc > bs:
            best, bs = s, sc
    return best


# ================================================================ 主变换

review = []
out_rows = []

for rid in NEW_IDS:
    v = van_by_id[rid]
    f = list(v)
    cat = v[I['类别']]
    lv = int(v[I['等级']])
    name = v[I['名字']]
    co_o = parse_pairs(v[I['威力系数']])
    yg_o = parse_pairs(v[I['运功效果']])
    notes = []

    # ---------------- 等级 0：mod 只动了内功的运功效果（生命/内力 ×2）
    if lv == 0:
        if cat == '内功':
            yg = dict(yg_o)
            for k in ('生命上限', '内力上限'):
                if yg.get(k) is not None:
                    yg[k] *= 2
            f[I['运功效果']] = dump_pairs(yg)
            notes.append('内功运功效果 生命/内力 ×2')
        review.append((rid, name, cat, lv, '—', '；'.join(notes) or '不改动'))
        out_rows.append(f)
        continue

    # ---------------- L4 / L5
    nn, score = nearest(cat, lv, feat_of(co_o, yg_o), co_o)

    # 1) 威力系数：非内力走 NN 增量，内力走组内众数映射（NN 里偶有离群跳跃）
    if co_o:
        newco = {}
        for k, ov in co_o.items():
            if ov is None:
                newco[k] = None
            elif k == NEILI:
                newco[k] = neili_map_mode(lv, ov)
            elif k in nn.co_o and nn.co_n.get(k) is not None:
                newco[k] = ov + (nn.co_n[k] - nn.co_o[k])
            else:
                newco[k] = ov + coef_delta_mode(lv, k, ov)
        f[I['威力系数']] = dump_pairs(newco)
        notes.append('威力系数对标 ' + nn.vo[I['名字']])

    # 2) 基础威力：NN 比例，夹回组内区间
    if v[I['基础威力']]:
        o = float(v[I['基础威力']])
        m = clamp(nn.pow_mult() or pow_center(lv), pow_range(lv))
        f[I['基础威力']] = num(CALIB_BASE.get(rid, round(o * m)))

    # 3) 内力消耗：NN 比例，夹回该 (等级, 内力系数) 分组的区间
    if v[I['内力消耗']]:
        o = float(v[I['内力消耗']])
        nm = nearest_in_bucket(cat, lv, feat_of(co_o, yg_o), co_o.get(NEILI))
        lo, mid, hi = mana_stats(lv, co_o.get(NEILI))
        m = clamp((nm.mana_mult() if nm else None) or mid, (lo, hi))
        f[I['内力消耗']] = num(round(o * m))

    # 4) 使用特效：追加 NN 新增的效果；命中按类别基准；无内力系数则不给内劲
    add = dict(nn.added_effects())
    if NEILI not in co_o:
        add.pop('内劲', None)
    if add:
        tx = parse_pairs(v[I['使用特效']])
        for k, val in add.items():
            if k in tx:
                continue
            if k == '命中':
                val = clamp(val, hit_range(lv, cat))
            tx[k] = val
        f[I['使用特效']] = dump_pairs(tx)
        notes.append('特效追加 ' + ','.join(add.keys()))

    # 5) 运功效果
    if cat in MARTIAL and lv in (4, 5):
        main_val, side_val = yg_shape_mode(lv)
        side_pool = [(k, x) for k, x in co_o.items()
                     if k != NEILI and k != cat and x is not None]
        side_key = max(side_pool, key=lambda kv: kv[1])[0] if side_pool else None
        newyg = {}
        if side_key:
            newyg[side_key] = side_val
        newyg[cat] = main_val
        f[I['运功效果']] = dump_pairs(newyg)
        notes.append('运功效果新建')
    elif cat == '绝技' and lv == 5:
        # 作者对绝世绝技的运功效果：属性值统一 +2（12->14），另抄 NN 新增词条
        newyg = dict(yg_o)
        for key in list(newyg):
            if key in BASE_ATTR:
                newyg[key] += 2
        for kk, x in nn.yg_n.items():
            if kk not in nn.yg_o and kk not in newyg:
                newyg[kk] = x
        f[I['运功效果']] = dump_pairs(newyg)
        notes.append('绝技L5 运功属性 +2')
    elif cat == '内功':
        # 按 NN 的「生命+内力」总量倍数等比放大，保持原本的血/内比例（避免倒挂），
        # 再把 NN 新增的被动（体力上限/自愈/护体/负面抗性…）整块抄过来
        k = nn.hp_mana_mult() or hp_mana_center(lv)
        newyg = dict(yg_o)
        if yg_o.get('生命上限') is not None:
            newyg['生命上限'] = round(yg_o['生命上限'] * k)
        if yg_o.get('内力上限') is not None:
            newyg['内力上限'] = round(yg_o['内力上限'] * k)
        for kk, x in nn.yg_n.items():
            if kk not in nn.yg_o and kk not in newyg:
                newyg[kk] = x
        f[I['运功效果']] = dump_pairs(newyg)
        notes.append('内功运功效果对标 ' + nn.vo[I['名字']] + '（总量 ×%.2f）' % k)

    # 6) 修炼需求：NN 增量；NN 该属性没动时退回组内众数增量
    xq_o = parse_pairs(v[I['修炼需求']])
    if xq_o:
        newxq = {}
        for k, ov in xq_o.items():
            if ov is None:
                continue
            d = None
            if k in nn.xq_o and k in nn.xq_n:
                d = nn.xq_n[k] - nn.xq_o[k]
            if not d:
                d = xq_delta_mode(lv, k) or (20 if lv == 4 else 30)
            newxq[k] = ov + d
        f[I['修炼需求']] = dump_pairs(newxq)

    # 7) 修炼效果（内功 L4/L5 追加小被动）
    if cat == '内功':
        newxl = dict(parse_pairs(v[I['修炼效果']]))
        for kk, x in nn.xl_n.items():
            if kk not in nn.xl_o and kk not in newxl:
                newxl[kk] = x
        f[I['修炼效果']] = dump_pairs(newxl)

    review.append((rid, name, cat, lv, nn.vo[I['名字']],
                   '%.2f | ' % score + '；'.join(notes)))
    out_rows.append(f)

# ================================================================ 写文件

lines = [header] + [join_csv_line(r) for r in mod_rows] + \
        [join_csv_line(r) for r in out_rows]
with open(OUT, 'w', encoding='utf-8-sig', newline='') as fh:
    fh.write('\r\n'.join(lines) + '\r\n')

with open(REVIEW, 'w', encoding='utf-8', newline='') as fh:
    w = csv.writer(fh, delimiter='\t')
    w.writerow(['id', '名字', '类别', '等级', '对标老功法', '相似度/改动'])
    for r in review:
        w.writerow(r)

if __name__ == '__main__':
    print('新功法 %d 行 -> %s（总 %d 行）' % (len(out_rows), OUT, len(lines) - 1))
    print('外功运功效果 shape: L4=%s L5=%s' % (yg_shape_mode(4), yg_shape_mode(5)))
    print('内功总量倍数中位: L4=%.2f L5=%.2f' % (hp_mana_center(4), hp_mana_center(5)))
    # ---- 自检：新功法的「运功效果」战力分是否落在同类同等级老功法的区间内 ----
    all_rows = list(mod_rows) + list(out_rows)
    by = defaultdict(list)
    for r in all_rows:
        by[(r[I['类别']], int(r[I['等级']]))].append(r)
    print('--- 运功效果战力分自检（游戏 HeroSpeAddData.GetValue 口径）---')
    bad = 0
    for key in sorted(by, key=lambda x: (x[1], x[0])):
        rs = by[key]
        old = [spe_value(r[I['运功效果']]) for r in rs if int(r[I['id']]) < 996]
        new = [(int(r[I['id']]), r[I['名字']], spe_value(r[I['运功效果']]))
               for r in rs if int(r[I['id']]) >= 996]
        if not old or not new:
            continue
        old.sort()
        lo, hi = old[0], old[-1]
        flag = ''
        for _i, _n, v in new:
            if not (lo <= v <= hi):
                flag += '  <<%s %.0f 越界>>' % (_n, v)
                bad += 1
        print('  %-2s L%d 老[%d] %.0f~%.0f 中位%.0f | 新 %s%s'
              % (key[0], key[1], len(old), lo, hi, old[len(old) // 2],
                 ' '.join('%s %.0f' % (n, v) for _i, n, v in new), flag))
    print('  越界条数: %d' % bad)

    print('--- 对标结果 ---')
    for r in review:
        print('  %4d %2s L%d %-9s <- %-9s %s' % (r[0], r[2], r[3], r[1], r[4], r[5]))
