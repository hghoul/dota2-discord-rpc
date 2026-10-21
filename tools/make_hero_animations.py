"""Анимированные иконки героев (WebP с прозрачностью) из официальных видео-рендеров Valve.

Источник: https://cdn.cloudflare.steamstatic.com/apps/dota2/videos/dota_react/heroes/renders/<герой>.webm
(1440x1440, VP9 с альфа-каналом, цикл ~2 с).

Результат — содержимое репозитория для хостинга на GitHub (раздаётся через jsDelivr):
    assets-repo/heroes/<герой>.webp
    assets-repo/manifest.json   — список героев, для которых есть анимация

Запуск (нужны ffmpeg и Pillow: winget install Gyan.FFmpeg; python -m pip install pillow):
    python tools/make_hero_animations.py                  # все герои из src/Dota2RPC/Resources/heroes.json
    python tools/make_hero_animations.py pudge axe        # только указанные
    python tools/make_hero_animations.py --force          # пересоздать уже готовые
"""
import argparse
import json
import shutil
import subprocess
import sys
import tempfile
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
VIDEO_URL = "https://cdn.cloudflare.steamstatic.com/apps/dota2/videos/dota_react/heroes/renders/{}.webm"


def run(args):
    proc = subprocess.run(args, stdin=subprocess.DEVNULL, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if proc.returncode != 0:
        raise RuntimeError(f"{args[0]}: {proc.stderr.strip()[-400:]}")


def download(name, cache: Path) -> Path:
    video = cache / f"{name}.webm"
    if not video.exists() or video.stat().st_size == 0:
        tmp = video.with_suffix(".part")
        with urllib.request.urlopen(VIDEO_URL.format(name), timeout=120) as r, open(tmp, "wb") as f:
            shutil.copyfileobj(r, f)
        tmp.replace(video)
    return video


def pixels(image):
    # get_flattened_data появился в Pillow 12 вместо устаревающего getdata.
    return list(image.get_flattened_data() if hasattr(image, "get_flattened_data") else image.getdata())


def body_box(frame):
    """Границы «массы» героя: строки и столбцы, где силуэта заметно много.

    Обычный getbbox тянется к кончику поднятого меча или крюка; здесь тонкие выступы
    (меньше 20% от самой плотной строки/столбца) не учитываются.
    """
    mask = frame.getchannel("A").point(lambda a: 255 if a > 128 else 0)
    w, h = mask.size
    rows = pixels(mask.resize((1, h), Image.BOX))   # средняя «заполненность» каждой строки
    cols = pixels(mask.resize((w, 1), Image.BOX))
    if not max(rows) or not max(cols):
        return None

    def span(values, share):
        limit = max(values) * share
        idx = [i for i, v in enumerate(values) if v >= limit]
        return idx[0], idx[-1] + 1

    t, b = span(rows, 0.2)
    l, r = span(cols, 0.12)
    return l, t, r, b


def make(name, args, cache: Path, out_dir: Path) -> str:
    out = out_dir / f"{name}.webp"
    if out.exists() and not args.force:
        return f"{name:<24} уже есть ({out.stat().st_size // 1024} КБ)"

    video = download(name, cache)
    with tempfile.TemporaryDirectory(prefix=f"d2anim-{name}-") as tmp:
        # Декодер libvpx-vp9 сохраняет альфа-канал (встроенный декодер ffmpeg его теряет).
        run(["ffmpeg", "-nostdin", "-v", "error", "-c:v", "libvpx-vp9", "-i", str(video),
             "-vf", f"fps={args.fps},scale={args.work}:{args.work}:flags=lanczos", "-pix_fmt", "rgba",
             str(Path(tmp) / "f%03d.png")])
        frames = [Image.open(p).convert("RGBA") for p in sorted(Path(tmp).glob("f*.png"))]
    if not frames:
        raise RuntimeError("нет кадров")

    # Кадрирование «портретом»: границы плотной части силуэта (альфа > 50%, без дыма и частиц),
    # медиана по кадрам, чтобы взмах оружия не раздувал рамку. Квадрат берёт голову и корпус:
    # не уже фигуры и не выше ~65% её роста, с небольшим отступом над головой.
    boxes = [b for b in (body_box(f) for f in frames[::3]) if b]
    if not boxes:
        raise RuntimeError("герой не найден на кадрах (пустой альфа-канал)")

    def median(values):
        values = sorted(values)
        return values[len(values) // 2]

    l, t = median(b[0] for b in boxes), median(b[1] for b in boxes)
    r, b = median(b[2] for b in boxes), median(b[3] for b in boxes)
    width, height = r - l, b - t
    side = int(min(args.work, max(width * 0.92, height * args.portrait, args.work * 0.3)))
    cx = (l + r) / 2
    x0 = int(max(0, min(args.work - side, cx - side / 2)))
    y0 = int(max(0, min(args.work - side, t - side * 0.06)))
    icons = [f.crop((x0, y0, x0 + side, y0 + side)).resize((args.size, args.size), Image.LANCZOS) for f in frames]

    # Длинным роликам (некоторые герои — до 12 с) разрешаем больше места, чтобы не терять качество.
    budget = min(1200, max(args.max_kb, len(icons) * 8))
    quality = args.quality
    while True:
        # Пишем во временный файл: прерванный запуск не оставит недописанную иконку.
        tmp_out = out.with_suffix(".tmp")
        icons[0].save(tmp_out, "WEBP", save_all=True, append_images=icons[1:], duration=round(1000 / args.fps),
                      loop=0, quality=quality, alpha_quality=80, method=6, minimize_size=True, allow_mixed=True)
        tmp_out.replace(out)
        kb = out.stat().st_size // 1024
        if kb <= budget or quality <= 40:
            break
        quality -= 10
    return f"{name:<24} {kb:>4} КБ  кадров {len(icons)}  q={quality}  квадрат {side}px из {args.work}"


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("heroes", nargs="*", help="короткие имена героев (по умолчанию — все)")
    p.add_argument("--size", type=int, default=160, help="сторона иконки, px")
    p.add_argument("--work", type=int, default=480, help="размер кадра для поиска границ, px")
    p.add_argument("--fps", type=int, default=10)
    p.add_argument("--quality", type=int, default=75)
    p.add_argument("--max-kb", type=int, default=300)
    p.add_argument("--portrait", type=float, default=0.65, help="доля роста героя в кадре (1.0 — во весь рост)")
    p.add_argument("--jobs", type=int, default=4)
    p.add_argument("--force", action="store_true")
    p.add_argument("--out", default=str(ROOT / "assets-repo"))
    p.add_argument("--cache", default=str(ROOT / "artifacts" / "hero-videos"))
    args = p.parse_args()

    if not shutil.which("ffmpeg"):
        sys.exit("ffmpeg не найден. Установите: winget install Gyan.FFmpeg")

    out_root = Path(args.out)
    out_dir = out_root / "heroes"
    cache = Path(args.cache)
    out_dir.mkdir(parents=True, exist_ok=True)
    cache.mkdir(parents=True, exist_ok=True)

    known = [h["name"] for h in json.loads((ROOT / "src/Dota2RPC/Resources/heroes.json").read_text(encoding="utf-8-sig"))]
    names = args.heroes or known

    failed = []

    def task(name):
        try:
            print(make(name, args, cache, out_dir), flush=True)
        except Exception as e:  # отдельный герой не должен останавливать остальные
            failed.append(name)
            print(f"{name:<24} ОШИБКА: {e}", flush=True)

    with ThreadPoolExecutor(max_workers=args.jobs) as pool:
        list(pool.map(task, names))

    available = sorted(f.stem for f in out_dir.glob("*.webp"))
    manifest = {"version": 1, "size": args.size, "heroes": available}
    (out_root / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    total = sum(f.stat().st_size for f in out_dir.glob("*.webp")) / 1024 / 1024
    print(f"\nГотово: файлов {len(available)}, {total:.1f} МБ; ошибок: {len(failed)} {' '.join(failed)}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
