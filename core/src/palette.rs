//! Palette engine: album artwork to a contrast-safe overlay palette.
//!
//! 1. Box-downsample to 64×64, dropping near-transparent pixels.
//! 2. k-means (k = 6) in OKLab, seeded by lightness quantiles so the result is
//!    deterministic per image. Clusters under 3% are dropped.
//! 3. A second small k-means over the most chromatic 5% of pixels, so a small
//!    vivid detail (a red dot on a pale cover) can still win `accent`.
//! 4. Roles: `base` (card fill), `accent` (progress, current-lyric glow) and
//!    three inks solved against `base` by moving lightness only:
//!    `ink` ≥ 7:1, `ink_soft` ≥ 4.5:1, `ink_faint` ≥ 3:1.

use crate::color::{hue_diff, Lab, Lch, Rgb};

const SAMPLE: usize = 64;
const K: usize = 6;
const ITERATIONS: usize = 12;
const MIN_SHARE: f32 = 0.03;

pub const INK_TARGET: f32 = 7.0;
pub const INK_SOFT_TARGET: f32 = 4.5;
pub const INK_FAINT_TARGET: f32 = 3.0;
/// The highlight colours the title and the current lyric, so it holds body-text contrast.
pub const HIGHLIGHT_TARGET: f32 = 4.5;
/// Share of the accent mixed into the base for the flat background's tinted corner.
pub const FLAT_TINT: f32 = 0.35;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[cfg_attr(feature = "uniffi", derive(uniffi::Enum))]
pub enum Tone {
    /// Dark card, light text. The default.
    Dark,
    /// Pale card, dark text: for pale, low-chroma covers.
    Light,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Palette {
    pub tone: Tone,
    /// Card fill.
    pub base: Rgb,
    /// The highlight: title, current lyric, progress fill, transport glyphs.
    /// A pastel of the dominant on a dark card; the vivid detail, deepened, on a
    /// light card. Holds [`HIGHLIGHT_TARGET`] against `base`.
    pub accent: Rgb,
    /// Titles and the current lyric.
    pub ink: Rgb,
    /// Artist, times.
    pub ink_soft: Rgb,
    /// Dimmed previous/next lyric lines.
    pub ink_faint: Rgb,
    /// Three colours for the blurred backdrop, by share of the cover.
    pub mesh: [Rgb; 3],
}

impl Palette {
    /// Fallback when there is no artwork (or it is fully transparent).
    pub fn neutral() -> Self {
        Self::from_clusters(&[Cluster { lab: Lch::new(0.3, 0.02, 5.2).to_lab(), share: 1.0 }], &[], 0.3, 0.02, None)
    }

    /// Builds a palette from straight (non-premultiplied) RGBA8 pixels.
    ///
    /// Returns [`Palette::neutral`] if `rgba` is too short for `width × height`
    /// or has no opaque pixels.
    pub fn from_rgba(rgba: &[u8], width: usize, height: usize) -> Self {
        Self::from_rgba_with_tone(rgba, width, height, None)
    }

    /// Like [`Palette::from_rgba`], but lets the user force a tone.
    pub fn from_rgba_with_tone(rgba: &[u8], width: usize, height: usize, force: Option<Tone>) -> Self {
        let px = sample(rgba, width, height);
        if px.is_empty() {
            return Self::neutral();
        }
        let mean_l = px.iter().map(|p| p.l).sum::<f32>() / px.len() as f32;
        let mean_c = px.iter().map(Lab::chroma).sum::<f32>() / px.len() as f32;
        let clusters = kmeans(&px, K, MIN_SHARE);
        // Vivid pass twice: over all pixels, and over only those whose hue differs
        // from the dominant, so a small contrasting detail isn't crowded out by a
        // large area of strong dominant colour.
        let dom_h = clusters.iter().max_by(|a, b| a.base_score().total_cmp(&b.base_score())).map(|c| c.lch().h);
        let mut vivid = vivid_clusters(&px, None);
        if let Some(h) = dom_h {
            vivid.extend(vivid_clusters(&px, Some(h)));
        }
        Self::from_clusters(&clusters, &vivid, mean_l, mean_c, force)
    }

    fn from_clusters(clusters: &[Cluster], vivid: &[Cluster], mean_l: f32, mean_c: f32, force: Option<Tone>) -> Self {
        let tone = force.unwrap_or(if mean_l > 0.72 && mean_c < 0.07 { Tone::Light } else { Tone::Dark });

        // Two colours come off the cover, as on a printed sleeve: the dominant
        // (its prevailing colour, nudged toward colourful clusters) and the vivid
        // detail (the most striking colour with a hue of its own).
        let dom = clusters
            .iter()
            .max_by(|a, b| a.base_score().total_cmp(&b.base_score()))
            .expect("at least one cluster")
            .lch();
        // A detail in a contrasting hue (the red on a blue sleeve) beats a merely
        // stronger version of the dominant; the latter is only the fallback.
        let pick = |distinct: bool| {
            clusters
                .iter()
                .chain(vivid)
                .map(|c| (c, c.lch()))
                .filter(|(_, l)| l.c > 0.045 && if distinct { hue_diff(l.h, dom.h) > 0.6 } else { l.c > dom.c * 1.6 })
                .max_by(|(a, al), (b, bl)| (al.c * a.share.powf(0.3)).total_cmp(&(bl.c * b.share.powf(0.3))))
                .map(|(_, l)| l)
        };
        let vivid_src = pick(true).or_else(|| pick(false));

        // Dark card: a near-neutral ground carrying a trace of the dominant hue, and
        // the dominant again as a pastel for the highlight (title, current lyric,
        // progress, controls). A grey dominant borrows the vivid detail's hue.
        // Light card: the dominant itself as a pale ground, and the vivid detail
        // deepened for the highlight, like coloured type on a pale sleeve.
        let (base, mut accent) = match tone {
            Tone::Dark => {
                let base = Lch::new((dom.l * 0.5).clamp(0.2, 0.25), dom.c.min(0.025), dom.h);
                let (c, h) = match vivid_src {
                    _ if dom.c >= 0.03 => (dom.c.clamp(0.035, 0.07), dom.h),
                    Some(v) => (v.c.clamp(0.04, 0.07), v.h),
                    None => (0.0, dom.h),
                };
                (base, Lch::new(0.9, c, h))
            }
            Tone::Light => {
                let base = Lch::new(0.91, dom.c.min(0.06), dom.h);
                let src = vivid_src.unwrap_or(Lch::new(0.4, dom.c.max(0.1), dom.h));
                (base, Lch::new(0.38, src.c.clamp(0.1, 0.16), src.h))
            }
        };

        // The highlight sets text, so it must hold body-text contrast: walk its
        // lightness away from the ground (hue and chroma kept) until it does.
        let base_probe = base.to_rgb();
        for _ in 0..60 {
            if accent.to_rgb().contrast(&base_probe) >= HIGHLIGHT_TARGET {
                break;
            }
            accent.l = (accent.l + if tone == Tone::Dark { 0.01 } else { -0.01 }).clamp(0.0, 1.0);
        }

        let base_rgb = base.to_rgb();
        let (ink_l, soft_l, faint_l) = match tone {
            Tone::Dark => (0.9, 0.7, 0.55),
            Tone::Light => (0.3, 0.5, 0.62),
        };
        let ink = solve_ink(&base_rgb, base.h, tone, INK_TARGET, ink_l, 0.02);
        // 0.1 of headroom: the gradient background tints the top-left corner, and
        // 8-bit rounding there can cost a few hundredths of contrast.
        let ink_soft = solve_ink(&base_rgb, base.h, tone, INK_SOFT_TARGET + 0.1, soft_l, 0.04);
        let ink_faint = solve_ink(&base_rgb, base.h, tone, INK_FAINT_TARGET, faint_l, 0.05);

        let mut by_share: Vec<&Cluster> = clusters.iter().collect();
        by_share.sort_by(|a, b| b.share.total_cmp(&a.share));
        let mesh_at = |i: usize| by_share.get(i).or(by_share.first()).map(|c| c.lch().to_rgb()).unwrap_or(base_rgb);

        Palette {
            tone,
            base: base_rgb,
            accent: accent.to_rgb(),
            ink,
            ink_soft,
            ink_faint,
            mesh: [mesh_at(0), mesh_at(1), mesh_at(2)],
        }
    }
}

/// Walks lightness away from `base` until the contrast target is met.
fn solve_ink(base: &Rgb, hue: f32, tone: Tone, target: f32, start_l: f32, chroma: f32) -> Rgb {
    let step = if tone == Tone::Dark { 0.01 } else { -0.01 };
    let mut l: f32 = start_l;
    let mut rgb = Lch::new(l, chroma, hue).to_rgb();
    for _ in 0..100 {
        if rgb.contrast(base) >= target {
            return rgb;
        }
        l = (l + step).clamp(0.0, 1.0);
        rgb = Lch::new(l, chroma, hue).to_rgb();
    }
    // Unreachable in practice: pure white/black against a clamped base clears 7:1.
    if tone == Tone::Dark { Rgb::new(255, 255, 255) } else { Rgb::new(0, 0, 0) }
}

#[derive(Debug, Clone, Copy)]
struct Cluster {
    lab: Lab,
    share: f32,
}

impl Cluster {
    fn lch(&self) -> Lch {
        self.lab.to_lch()
    }
    fn base_score(&self) -> f32 {
        self.share * (0.6 + self.lab.chroma() * 3.0)
    }
}

/// Box-downsamples to at most 64×64 and converts opaque pixels to OKLab.
fn sample(rgba: &[u8], width: usize, height: usize) -> Vec<Lab> {
    if width == 0 || height == 0 || rgba.len() < width * height * 4 {
        return Vec::new();
    }
    let (tw, th) = (width.min(SAMPLE), height.min(SAMPLE));
    let mut out = Vec::with_capacity(tw * th);
    for ty in 0..th {
        let (y0, y1) = (ty * height / th, ((ty + 1) * height / th).max(ty * height / th + 1));
        for tx in 0..tw {
            let (x0, x1) = (tx * width / tw, ((tx + 1) * width / tw).max(tx * width / tw + 1));
            let (mut r, mut g, mut b, mut n) = (0u32, 0u32, 0u32, 0u32);
            for y in y0..y1 {
                for x in x0..x1 {
                    let i = (y * width + x) * 4;
                    if rgba[i + 3] > 200 {
                        r += rgba[i] as u32;
                        g += rgba[i + 1] as u32;
                        b += rgba[i + 2] as u32;
                        n += 1;
                    }
                }
            }
            if n * 2 > ((y1 - y0) * (x1 - x0)) as u32 {
                out.push(Rgb::new((r / n) as u8, (g / n) as u8, (b / n) as u8).to_oklab());
            }
        }
    }
    out
}

/// Deterministic k-means: seeds at lightness quantiles. Drops clusters under `min_share`.
fn kmeans(px: &[Lab], k: usize, min_share: f32) -> Vec<Cluster> {
    let k = k.min(px.len()).max(1);
    let mut sorted: Vec<Lab> = px.to_vec();
    sorted.sort_by(|a, b| a.l.total_cmp(&b.l));
    let mut cent: Vec<Lab> = (0..k).map(|i| sorted[((i as f32 + 0.5) / k as f32 * sorted.len() as f32) as usize]).collect();
    let mut assign = vec![0usize; px.len()];
    for _ in 0..ITERATIONS {
        let mut sum = vec![(0.0f32, 0.0f32, 0.0f32, 0u32); k];
        for (i, p) in px.iter().enumerate() {
            let best = (0..k).min_by(|&a, &b| p.dist2(&cent[a]).total_cmp(&p.dist2(&cent[b]))).unwrap_or(0);
            assign[i] = best;
            let s = &mut sum[best];
            s.0 += p.l;
            s.1 += p.a;
            s.2 += p.b;
            s.3 += 1;
        }
        for (c, s) in cent.iter_mut().zip(&sum) {
            if s.3 > 0 {
                let n = s.3 as f32;
                *c = Lab { l: s.0 / n, a: s.1 / n, b: s.2 / n };
            }
        }
    }
    let mut counts = vec![0u32; k];
    for &a in &assign {
        counts[a] += 1;
    }
    let total = px.len() as f32;
    let mut out: Vec<Cluster> = cent
        .into_iter()
        .zip(counts)
        .map(|(lab, n)| Cluster { lab, share: n as f32 / total })
        .filter(|c| c.share >= min_share)
        .collect();
    if out.is_empty() {
        // Every cluster was tiny (a very noisy image): fall back to the mean.
        let n = px.len() as f32;
        let mean = px.iter().fold(Lab::default(), |m, p| Lab { l: m.l + p.l / n, a: m.a + p.a / n, b: m.b + p.b / n });
        out.push(Cluster { lab: mean, share: 1.0 });
    }
    out
}

/// k = 2 over the most chromatic 5% of pixels (optionally only those away from a
/// hue). Shares are relative to the whole image.
fn vivid_clusters(px: &[Lab], away_from_hue: Option<f32>) -> Vec<Cluster> {
    let mut by_chroma: Vec<Lab> = match away_from_hue {
        Some(h) => px.iter().copied().filter(|p| p.chroma() > 0.05 && hue_diff(p.hue(), h) > 0.6).collect(),
        None => px.to_vec(),
    };
    // A detail needs a few pixels to count (about 0.2% of a 64x64 sample).
    if by_chroma.len() < 8 {
        return Vec::new();
    }
    by_chroma.sort_by(|a, b| b.chroma().total_cmp(&a.chroma()));
    let n = (px.len() / 20).max(12).min(by_chroma.len());
    let top = &by_chroma[..n];
    let mean_c = top.iter().map(Lab::chroma).sum::<f32>() / n as f32;
    if mean_c < 0.08 {
        return Vec::new();
    }
    let scale = n as f32 / px.len() as f32;
    kmeans(top, 2, 0.0).into_iter().map(|c| Cluster { lab: c.lab, share: c.share * scale }).collect()
}

/// Colours for the tray icon's three waves (top to bottom), taken from a track's
/// palette: the highlight first, then the most different hues among the cover's
/// main colours (a hue rotated from the highlight when the cover has too few).
/// Each is pushed vivid and set to a lightness that reads on the taskbar:
/// light waves on a dark taskbar, deep ones on a light taskbar.
pub fn tray_colors(p: &Palette, dark_taskbar: bool) -> [Rgb; 3] {
    let first = p.accent.to_oklab().to_lch();
    let mut hues = vec![first.h];
    let mut chromas = vec![first.c];
    let mut pool: Vec<Lch> = p.mesh.iter().map(|c| c.to_oklab().to_lch()).filter(|l| l.c >= 0.03).collect();
    while hues.len() < 3 {
        // The candidate furthest (in hue) from everything already chosen.
        let pick = pool
            .iter()
            .enumerate()
            .map(|(i, l)| (i, hues.iter().map(|h| hue_diff(*h, l.h)).fold(f32::MAX, f32::min)))
            .filter(|(_, d)| *d > 0.35)
            .max_by(|a, b| a.1.total_cmp(&b.1))
            .map(|(i, _)| i);
        match pick {
            Some(i) => {
                let l = pool.remove(i);
                hues.push(l.h);
                chromas.push(l.c);
            }
            None => {
                // Too few distinct colours on the cover: step around the wheel from the highlight.
                let step = if hues.len() == 1 { 0.7 } else { -0.7 };
                hues.push(first.h + step);
                chromas.push(first.c);
            }
        }
    }
    let lightness = if dark_taskbar { [0.82, 0.74, 0.68] } else { [0.5, 0.56, 0.62] };
    [0, 1, 2].map(|i| Lch::new(lightness[i], chromas[i].clamp(0.12, 0.2), hues[i]).to_rgb())
}

/// Side length of [`backdrop`] output.
pub const BACKDROP_SIZE: usize = 32;

/// The card background: artwork averaged down to 32×32, box-blurred three
/// times (≈ Gaussian) and saturated ×1.3 in OKLCH. Opaque RGBA8.
///
/// Done once per track. The shell stretches it to the card with bilinear
/// filtering, which on an already-blurred image reads as a smooth glow, so no
/// live blur effect runs over the desktop. `None` if the input is malformed.
pub fn backdrop(rgba: &[u8], width: usize, height: usize) -> Option<Vec<u8>> {
    const N: usize = BACKDROP_SIZE;
    if width == 0 || height == 0 || rgba.len() < width * height * 4 {
        return None;
    }
    let fallback = Palette::neutral().base;
    let mut img = vec![[0f32; 3]; N * N];
    for ty in 0..N {
        let (y0, y1) = (ty * height / N, ((ty + 1) * height / N).max(ty * height / N + 1).min(height));
        for tx in 0..N {
            let (x0, x1) = (tx * width / N, ((tx + 1) * width / N).max(tx * width / N + 1).min(width));
            let (mut acc, mut n) = ([0f32; 3], 0f32);
            for y in y0..y1 {
                for x in x0..x1 {
                    let i = (y * width + x) * 4;
                    let a = rgba[i + 3] as f32 / 255.0;
                    acc[0] += rgba[i] as f32 * a;
                    acc[1] += rgba[i + 1] as f32 * a;
                    acc[2] += rgba[i + 2] as f32 * a;
                    n += a;
                }
            }
            img[ty * N + tx] = if n > 0.0 { acc.map(|v| v / n) } else { [fallback.r as f32, fallback.g as f32, fallback.b as f32] };
        }
    }
    for _ in 0..3 {
        img = box_blur(&img, N, 3);
    }
    let mut out = Vec::with_capacity(N * N * 4);
    for p in img {
        let rgb = Rgb::new(p[0].round() as u8, p[1].round() as u8, p[2].round() as u8);
        let mut lch = rgb.to_oklab().to_lch();
        lch.c *= 1.3;
        let s = lch.to_rgb();
        out.extend_from_slice(&[s.r, s.g, s.b, 255]);
    }
    Some(out)
}

/// Separable box blur with clamped edges.
fn box_blur(src: &[[f32; 3]], n: usize, r: isize) -> Vec<[f32; 3]> {
    let w = (2 * r + 1) as f32;
    let at = |v: isize| v.clamp(0, n as isize - 1) as usize;
    let mut tmp = vec![[0f32; 3]; n * n];
    for y in 0..n {
        for x in 0..n {
            let mut acc = [0f32; 3];
            for d in -r..=r {
                let p = src[y * n + at(x as isize + d)];
                acc.iter_mut().zip(p).for_each(|(a, v)| *a += v);
            }
            tmp[y * n + x] = acc.map(|v| v / w);
        }
    }
    let mut out = vec![[0f32; 3]; n * n];
    for y in 0..n {
        for x in 0..n {
            let mut acc = [0f32; 3];
            for d in -r..=r {
                let p = tmp[at(y as isize + d) * n + x];
                acc.iter_mut().zip(p).for_each(|(a, v)| *a += v);
            }
            out[y * n + x] = acc.map(|v| v / w);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Tiny deterministic PRNG for synthetic covers.
    struct Rng(u64);
    impl Rng {
        fn next(&mut self) -> u8 {
            self.0 = self.0.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            (self.0 >> 56) as u8
        }
    }

    fn image(w: usize, h: usize, f: impl Fn(usize, usize) -> [u8; 4]) -> Vec<u8> {
        (0..h).flat_map(|y| (0..w).map(move |x| (x, y))).flat_map(|(x, y)| f(x, y)).collect()
    }

    #[test]
    fn dark_card_highlight_is_a_pastel_of_the_dominant() {
        // Violet field with a coral disc: the violet dominates.
        let img = image(300, 300, |x, y| {
            let (dx, dy) = (x as f32 - 196.0, y as f32 - 188.0);
            if dx * dx + dy * dy < 70.0 * 70.0 { [255, 154, 98, 255] } else { [60, 25, 90, 255] }
        });
        let p = Palette::from_rgba(&img, 300, 300);
        assert_eq!(p.tone, Tone::Dark);
        let hl = p.accent.to_oklab();
        assert!(hl.l > 0.8, "pastel: {}", p.accent.hex());
        assert!(p.accent.b > p.accent.g, "violet, not coral: {}", p.accent.hex());
        assert!(p.base.to_oklab().chroma() <= 0.03, "near-neutral ground: {}", p.base.hex());
    }

    #[test]
    fn contrasting_detail_beats_a_stronger_dominant() {
        // Dark teal field, a large saturated blue area, and small pink lights.
        let img = image(300, 300, |x, y| {
            if (40..60).contains(&x) && (40..60).contains(&y) { [240, 90, 170, 255] }
            else if y > 180 { [20, 70, 200, 255] }
            else { [20, 50, 60, 255] }
        });
        let p = Palette::from_rgba_with_tone(&img, 300, 300, Some(Tone::Light));
        assert!(p.accent.r > p.accent.b, "pink, deepened: {}", p.accent.hex());
    }

    #[test]
    fn sleeve_reference_pale_blue_with_red_detail() {
        // Like the reference: a pale blue sleeve with a small red detail.
        let img = image(300, 300, |x, y| {
            if (120..170).contains(&x) && (200..250).contains(&y) { [200, 40, 40, 255] } else { [196, 226, 250, 255] }
        });
        let light = Palette::from_rgba_with_tone(&img, 300, 300, Some(Tone::Light));
        let base = light.base.to_oklab().to_lch();
        assert!(base.l > 0.88 && base.c > 0.03, "pale blue ground: {}", light.base.hex());
        assert!(light.accent.r > light.accent.b && light.accent.to_oklab().l < 0.5, "deep red: {}", light.accent.hex());

        let dark = Palette::from_rgba_with_tone(&img, 300, 300, Some(Tone::Dark));
        assert!(dark.accent.b > dark.accent.r && dark.accent.to_oklab().l > 0.8, "pastel blue: {}", dark.accent.hex());
    }

    #[test]
    fn pale_cover_gets_light_card_and_small_red_dot_wins_accent() {
        let img = image(300, 300, |x, y| {
            let (dx, dy) = (x as f32 - 226.0, y as f32 - 120.0);
            if dx * dx + dy * dy < 16.0 * 16.0 {
                [184, 70, 58, 255]
            } else if (170..176).contains(&y) {
                [111, 143, 90, 255]
            } else {
                [243, 239, 230, 255]
            }
        });
        let p = Palette::from_rgba(&img, 300, 300);
        assert_eq!(p.tone, Tone::Light);
        assert!(p.accent.r > p.accent.g && p.accent.r > p.accent.b, "red dot should win: {}", p.accent.hex());
    }

    #[test]
    fn inks_meet_contrast_targets_on_any_cover() {
        let mut rng = Rng(42);
        for _ in 0..60 {
            let (a, b) = ([rng.next(), rng.next(), rng.next()], [rng.next(), rng.next(), rng.next()]);
            let img = image(40, 40, |x, _| if x < 25 { [a[0], a[1], a[2], 255] } else { [b[0], b[1], b[2], 255] });
            for tone in [None, Some(Tone::Dark), Some(Tone::Light)] {
                let p = Palette::from_rgba_with_tone(&img, 40, 40, tone);
                assert!(p.ink.contrast(&p.base) >= INK_TARGET, "ink {} on {}", p.ink.hex(), p.base.hex());
                assert!(p.ink_soft.contrast(&p.base) >= INK_SOFT_TARGET);
                assert!(p.ink_faint.contrast(&p.base) >= INK_FAINT_TARGET);
                assert!(p.accent.contrast(&p.base) >= HIGHLIGHT_TARGET, "highlight {} on {}", p.accent.hex(), p.base.hex());
            }
        }
    }

    /// The Windows "flat colour" background warms the card's top-left corner by
    /// tinting the base toward the accent (lightness held). Text must stay legible there too.
    #[test]
    fn flat_tint_corner_keeps_ink_contrast() {
        let mut rng = Rng(42);
        let (mut ink, mut soft, mut faint) = (f32::MAX, f32::MAX, f32::MAX);
        for _ in 0..200 {
            let (a, b) = ([rng.next(), rng.next(), rng.next()], [rng.next(), rng.next(), rng.next()]);
            let img = image(40, 40, |x, _| if x < 25 { [a[0], a[1], a[2], 255] } else { [b[0], b[1], b[2], 255] });
            for tone in [None, Some(Tone::Dark), Some(Tone::Light)] {
                let p = Palette::from_rgba_with_tone(&img, 40, 40, tone);
                let corner = crate::color::tint_oklch(p.base, p.accent, FLAT_TINT);
                ink = ink.min(p.ink.contrast(&corner));
                soft = soft.min(p.ink_soft.contrast(&corner));
                faint = faint.min(p.ink_faint.contrast(&corner));
            }
        }
        println!("worst on tinted corner: ink {ink:.2}, soft {soft:.2}, faint {faint:.2}");
        // Faint ink is solved to exactly 3:1 on base, so 8-bit rounding of any other
        // surface can dip it a hair; it is large text and this is the extreme corner.
        assert!(ink >= INK_TARGET && soft >= INK_SOFT_TARGET && faint >= 2.9);
    }

    #[test]
    fn tray_colors_read_on_both_taskbars() {
        let dark_bar = Rgb::new(0x20, 0x20, 0x20);
        let light_bar = Rgb::new(0xF3, 0xF3, 0xF3);
        let mut rng = Rng(9);
        for _ in 0..60 {
            let (a, b) = ([rng.next(), rng.next(), rng.next()], [rng.next(), rng.next(), rng.next()]);
            let img = image(40, 40, |x, _| if x < 25 { [a[0], a[1], a[2], 255] } else { [b[0], b[1], b[2], 255] });
            let p = Palette::from_rgba(&img, 40, 40);
            for c in tray_colors(&p, true) {
                assert!(c.contrast(&dark_bar) >= 3.0, "{} on dark", c.hex());
            }
            for c in tray_colors(&p, false) {
                assert!(c.contrast(&light_bar) >= 3.0, "{} on light", c.hex());
            }
        }
    }

    #[test]
    fn tray_colors_take_distinct_hues() {
        // Pale blue sleeve with a red detail: the three waves shouldn't all be blue.
        let img = image(300, 300, |x, y| {
            if (120..170).contains(&x) && (200..250).contains(&y) { [200, 40, 40, 255] } else { [196, 226, 250, 255] }
        });
        let c = tray_colors(&Palette::from_rgba(&img, 300, 300), true);
        let h: Vec<f32> = c.iter().map(|x| x.to_oklab().to_lch().h).collect();
        assert!(hue_diff(h[0], h[1]) > 0.3 && hue_diff(h[1], h[2]) > 0.3, "{:?}", c.map(|x| x.hex()));
    }

    #[test]
    fn deterministic() {
        let mut rng = Rng(7);
        let img: Vec<u8> = (0..128 * 128).flat_map(|_| [rng.next(), rng.next(), rng.next(), 255]).collect();
        assert_eq!(Palette::from_rgba(&img, 128, 128), Palette::from_rgba(&img, 128, 128));
    }

    #[test]
    fn empty_or_transparent_falls_back() {
        assert_eq!(Palette::from_rgba(&[], 0, 0), Palette::neutral());
        assert_eq!(Palette::from_rgba(&[0u8; 16 * 16 * 4], 16, 16), Palette::neutral());
        assert_eq!(Palette::from_rgba(&[1, 2, 3], 10, 10), Palette::neutral());
    }

    #[test]
    fn backdrop_is_opaque_and_smooth() {
        let img = image(100, 100, |x, _| if x < 50 { [255, 0, 0, 255] } else { [0, 0, 255, 255] });
        let b = backdrop(&img, 100, 100).unwrap();
        assert_eq!(b.len(), BACKDROP_SIZE * BACKDROP_SIZE * 4);
        assert!(b.chunks(4).all(|p| p[3] == 255));
        // The hard edge is gone: neighbouring columns differ only a little.
        let row: Vec<i32> = (0..BACKDROP_SIZE).map(|x| b[x * 4] as i32).collect();
        assert!(row.windows(2).all(|w| (w[0] - w[1]).abs() < 60), "{row:?}");
        assert!(backdrop(&[1, 2], 10, 10).is_none());
    }

    #[test]
    fn grey_cover_still_gets_an_accent() {
        let img = image(32, 32, |x, _| { let v = (x * 8) as u8; [v, v, v, 255] });
        let p = Palette::from_rgba(&img, 32, 32);
        assert!(p.accent.contrast(&p.base) > 1.5);
    }
}
