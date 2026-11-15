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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
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
    /// Progress fill, current-lyric glow, tray tint.
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
        let vivid = vivid_clusters(&px);
        Self::from_clusters(&clusters, &vivid, mean_l, mean_c, force)
    }

    fn from_clusters(clusters: &[Cluster], vivid: &[Cluster], mean_l: f32, mean_c: f32, force: Option<Tone>) -> Self {
        let tone = force.unwrap_or(if mean_l > 0.72 && mean_c < 0.07 { Tone::Light } else { Tone::Dark });

        // Base: the dominant cluster, nudged toward colourful ones.
        let base_cl = clusters
            .iter()
            .max_by(|a, b| a.base_score().total_cmp(&b.base_score()))
            .expect("at least one cluster")
            .lch();
        let base = match tone {
            Tone::Dark => Lch::new((base_cl.l * 0.5).clamp(0.2, 0.28), base_cl.c.min(0.09), base_cl.h),
            Tone::Light => Lch::new(0.93, base_cl.c.min(0.035), base_cl.h),
        };

        // Accent: most chromatic cluster with a distinct hue (or much stronger chroma).
        let accent_src = clusters
            .iter()
            .chain(vivid)
            .map(|c| (c, c.lch()))
            .filter(|(_, l)| l.c > 0.045 && (hue_diff(l.h, base_cl.h) > 0.44 || l.c > base_cl.c * 1.6))
            .max_by(|(a, al), (b, bl)| (al.c * a.share.powf(0.3)).total_cmp(&(bl.c * b.share.powf(0.3))))
            .map(|(_, l)| l);
        let mut accent = accent_src.map_or(Lch::new(0.7, 0.14, base_cl.h), |l| Lch::new(l.l, l.c.max(0.12), l.h));
        accent.l = match tone {
            Tone::Dark => accent.l.max(0.72),
            Tone::Light => accent.l.min(0.5),
        };

        let base_rgb = base.to_rgb();
        let (ink_l, soft_l, faint_l) = match tone {
            Tone::Dark => (0.9, 0.7, 0.55),
            Tone::Light => (0.3, 0.5, 0.62),
        };
        let ink = solve_ink(&base_rgb, base.h, tone, INK_TARGET, ink_l, 0.02);
        let ink_soft = solve_ink(&base_rgb, base.h, tone, INK_SOFT_TARGET, soft_l, 0.04);
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

/// k = 2 over the most chromatic 5% of pixels. Shares are relative to the whole image.
fn vivid_clusters(px: &[Lab]) -> Vec<Cluster> {
    let mut by_chroma: Vec<Lab> = px.to_vec();
    by_chroma.sort_by(|a, b| b.chroma().total_cmp(&a.chroma()));
    let n = (px.len() / 20).max(12).min(px.len());
    let top = &by_chroma[..n];
    let mean_c = top.iter().map(Lab::chroma).sum::<f32>() / n as f32;
    if mean_c < 0.08 {
        return Vec::new();
    }
    let scale = n as f32 / px.len() as f32;
    kmeans(top, 2, 0.0).into_iter().map(|c| Cluster { lab: c.lab, share: c.share * scale }).collect()
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
    fn dusk_cover_is_dark_with_warm_accent() {
        // Violet field with a coral disc, like the concept board's "Paper Lanterns".
        let img = image(300, 300, |x, y| {
            let (dx, dy) = (x as f32 - 196.0, y as f32 - 188.0);
            if dx * dx + dy * dy < 70.0 * 70.0 { [255, 154, 98, 255] } else { [60, 25, 90, 255] }
        });
        let p = Palette::from_rgba(&img, 300, 300);
        assert_eq!(p.tone, Tone::Dark);
        assert!(p.accent.r > p.accent.b, "accent should be warm: {}", p.accent.hex());
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
            }
        }
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
    fn grey_cover_still_gets_an_accent() {
        let img = image(32, 32, |x, _| { let v = (x * 8) as u8; [v, v, v, 255] });
        let p = Palette::from_rgba(&img, 32, 32);
        assert!(p.accent.contrast(&p.base) > 1.5);
    }
}
