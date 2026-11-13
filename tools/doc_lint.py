#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""文档约定检查 —— 把「文档怎么组织」变成会失败的检查，而不是每次重申一遍。

    python3 tools/doc_lint.py            # 检查 + 打印各文档字符数增量
    python3 tools/doc_lint.py --quiet    # 只报错误（pre-commit hook 用）
    python3 tools/doc_lint.py --dups     # 额外列出跨文件重复的长行

**错误**（退出码 1）—— 都是客观的「导航已经坏了」：

  * 有 `docs/*.md` 没被 `AGENTS.md` 索引到（§1.1 的红线：搬走细节必须留入口）
  * 有 `.agents/skills/*/SKILL.md` 缺 frontmatter、`name` 与目录名不一致，或没进 §7.1 索引
  * 相对链接指向不存在的文件（悬空引用）

**警告**（不阻断）—— 倾向于「该用渐进披露了」：

  * 超过 400 行却没有 §0 判据速查（故意不要就在文件里写 `<!-- doc-lint: no-summary -->`）

另外**每次都打印每份文档的字符数增量**（相对 HEAD）。膨胀是设计出来的：
这条只是让「这个文件又长了 3000 字」在每次收尾时**可见**，而不是等到没人敢读。

⚠️ 不要往这个脚本里加「风格」检查（措辞、标题层级、句子长短）。风格判据会变成噪音，
噪音会被无视，被无视的检查连它本来能抓的东西也一起失效。
"""

import argparse
import glob
import os
import re
import subprocess
import sys

ROOT = None
HANDBOOK = 'AGENTS.md'
DOCS_DIR = 'docs'
TEMPLATE = '_template.md'
SKILLS_DIR = '.agents/skills'
SUMMARY_HEADING = '## 0.'
SUMMARY_OPT_OUT = re.compile(r'<!--\s*doc-lint:\s*no-summary\s*-->')
SUMMARY_MIN_LINES = 400
SKILL_NAME = re.compile(r'^[a-z0-9]+(?:-[a-z0-9]+)*$')
SKILL_DESC_MAX = 500

LINK = re.compile(r'\[[^\]]*\]\(([^)\s]+)\)')


def sh(*args):
    """跑一条 git 命令，失败返回 None。"""
    try:
        out = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, check=True)
        return out.stdout
    except Exception:
        return None


def read(path):
    with open(os.path.join(ROOT, path), encoding='utf-8') as fh:
        return fh.read()


def doc_files():
    names = sorted(os.path.basename(p) for p in glob.glob(os.path.join(ROOT, DOCS_DIR, '*.md')))
    return [n for n in names if n != TEMPLATE]


def skill_files():
    """返回 [(目录名, 相对路径)] —— .agents/skills 下的目录式 skill。"""
    base = os.path.join(ROOT, SKILLS_DIR)
    if not os.path.isdir(base):
        return []
    out = []
    for entry in sorted(os.listdir(base)):
        rel = '{}/{}/SKILL.md'.format(SKILLS_DIR, entry)
        if os.path.isfile(os.path.join(ROOT, rel)):
            out.append((entry, rel))
    return out


def parse_frontmatter(text):
    """极简 frontmatter：只认 `key: value` 单行（够用，不引 YAML 依赖）。"""
    if not text.startswith('---\n'):
        return None
    end = text.find('\n---', 4)
    if end == -1:
        return None
    data = {}
    for line in text[4:end].split('\n'):
        if ':' not in line or line.lstrip().startswith('#'):
            continue
        key, _, value = line.partition(':')
        data[key.strip()] = value.strip().strip('"').strip("'")
    return data


def rel_link_targets(path, text):
    """产出 (链接原文, 目标路径) —— 只关心同一仓库内的相对文件链接。"""
    for raw in LINK.findall(text):
        if raw.startswith(('http://', 'https://', 'mailto:', '#')):
            continue
        target = raw.split('#')[0]
        if not target:
            continue
        yield raw, os.path.normpath(os.path.join(os.path.dirname(path), target))


def head_chars(path):
    """该文件在 HEAD 的字符数；新增文件返回 0，不存在返回 None。"""
    out = sh('git', 'show', 'HEAD:' + path)
    return None if out is None else len(out)


def main():
    global ROOT

    parser = argparse.ArgumentParser(description='检查文档组织约定（索引 / 链接 / 摘要块 / 膨胀）')
    parser.add_argument('--quiet', action='store_true', help='只输出错误（给 pre-commit 用）')
    parser.add_argument('--dups', action='store_true', help='额外列出跨文件重复的长行')
    parser.add_argument('--no-delta', action='store_true', help='不做相对 HEAD 的字符数对比')
    args = parser.parse_args()

    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass

    top = sh('git', 'rev-parse', '--show-toplevel')
    ROOT = top.strip() if top else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    if not os.path.exists(os.path.join(ROOT, HANDBOOK)):
        print('找不到 {}，跳过检查（不在仓库根？）'.format(HANDBOOK))
        return 0

    errors = []
    warnings = []

    handbook = read(HANDBOOK)
    docs = doc_files()
    skills = skill_files()

    # ── 错误 1：每份 docs/*.md 都要在手册里有入口 ────────────────────────────
    for name in docs:
        if '{}/{}'.format(DOCS_DIR, name) not in handbook:
            errors.append(
                '{}/{} 没有任何入口 —— 在 {} 的索引里加一行（§1.1：「搬走细节必须留入口，'
                '否则不是精简，是藏起来」）'.format(DOCS_DIR, name, HANDBOOK))

    # ── 错误 1b：每个 skill 都要有合法 frontmatter 与手册入口 ────────────────
    for dirname, path in skills:
        text = read(path)
        fm = parse_frontmatter(text)
        if fm is None:
            errors.append('{} 缺少 YAML frontmatter（必须有 name 与 description）'.format(path))
            continue
        name = fm.get('name', '')
        desc = fm.get('description', '')
        if not name or not desc:
            errors.append('{} 的 frontmatter 必须同时有 name 与 description'.format(path))
        if name and not SKILL_NAME.match(name):
            errors.append('{} 的 name {!r} 不是 kebab-case'.format(path, name))
        if name and name != dirname:
            errors.append('{} 的 name {!r} 与目录名 {!r} 不一致'.format(path, name, dirname))
        if len(desc) > SKILL_DESC_MAX:
            warnings.append('{} 的 description 超过 {} 字符，模型目录里会被截断'.format(path, SKILL_DESC_MAX))
        if '{}/{}/SKILL.md'.format(SKILLS_DIR, dirname) not in handbook:
            errors.append(
                '{} 没有任何入口 —— 在 {} §7.1 技能索引里加一行'.format(path, HANDBOOK))

    # ── 错误 2：悬空相对链接 ────────────────────────────────────────────────
    sources = [HANDBOOK] + ['{}/{}'.format(DOCS_DIR, n) for n in docs] + [p for _, p in skills]
    for path in sources:
        for raw, target in rel_link_targets(path, read(path)):
            if not os.path.exists(os.path.join(ROOT, target)):
                errors.append('{} -> {!r} 指向不存在的 {}'.format(path, raw, target))

    # ── 警告：大文档没有 §0 判据速查 ────────────────────────────────────────
    for name in docs:
        path = '{}/{}'.format(DOCS_DIR, name)
        text = read(path)
        if text.count('\n') + 1 < SUMMARY_MIN_LINES:
            continue
        if SUMMARY_HEADING in text or SUMMARY_OPT_OUT.search(text):
            continue
        warnings.append(
            '{} 超过 {} 行却没有 「{}」 摘要块 —— 读者只好从头滚；'
            '不想加就在文件里写 <!-- doc-lint: no-summary -->'.format(path, SUMMARY_MIN_LINES, SUMMARY_HEADING))

    # ── 警告：pre-commit 护栏没上膛（护具自己报告自己是否戴着）──────────────
    hooked = sh('git', 'config', '--get', 'core.hooksPath')
    if hooked is None or hooked.strip() != '.githooks':
        warnings.append('pre-commit hook 未启用 —— 跑 `git config core.hooksPath .githooks`，'
                        '否则本检查不会自动挡住坏提交（只在你手动跑时生效）')

    # ── 可选：跨文件重复的长行 ──────────────────────────────────────────────
    if args.dups:
        seen = {}
        for path in sources:
            for line in read(path).split('\n'):
                s = line.strip()
                if len(s) >= 60 and not s.startswith(('|', '```', '>')):
                    seen.setdefault(s, set()).add(path)
        shared = {k: v for k, v in seen.items() if len(v) > 1}
        if shared:
            print('== 跨文件重复的长行（>=60 字符）==')
            for line, where in list(shared.items())[:15]:
                print('  [{}] {}'.format(', '.join(sorted(where)), line[:80]))
            print('  ... 共 {} 组 / {} 字符\n'.format(len(shared), sum(len(k) for k in shared)))

    # ── 字符数表（含相对 HEAD 的增量）────────────────────────────────────────
    if not args.quiet:
        total = 0
        grew = []
        print('== 文档体量（增量 = 工作树 vs HEAD）==')
        rows = ([(HANDBOOK, None)]
                + [('{}/{}'.format(DOCS_DIR, n), n) for n in docs]
                + [(p, None) for _, p in skills])
        for path, _ in rows:
            here = len(read(path))
            total += here
            if args.no_delta:
                delta = None
            else:
                was = head_chars(path)
                delta = None if was is None else here - was
            mark = ''
            if delta:
                mark = '  {}{:+d}'.format('← ' if abs(delta) >= 1500 else '', delta)
                if abs(delta) >= 1500:
                    grew.append((path, delta))
            print('  {:8d} 字符  {}{}'.format(here, path, mark))
        print('  {:8d} 字符  合计'.format(total))
        if grew:
            print('\n  ⚠️ 本次有文档显著变长（>=1500 字符）：')
            for path, delta in grew:
                print('     {:+6d}  {} —— 先问「这些是判据还是推导？推出去了吗？」'.format(delta, path))
        print()

    for w in warnings:
        print('[WARN] ' + w)
    for e in errors:
        print('[ERROR] ' + e)

    if errors:
        print('\n{} 个错误 —— 文档导航已经坏了。'.format(len(errors)))
        return 1
    if not args.quiet:
        print('文档约定检查通过（{} 份文档 / {} 个 skill）。'.format(len(docs) + 1, len(skills)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
