//! Colour maths: sRGB, OKLab/OKLCH, gamut mapping and WCAG contrast.

/// An 8-bit sRGB colour.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Rgb {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl Rgb {
    pub const fn new(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b }
    }

    /// `#rrggbb`
    pub fn hex(&self) -> String {
        format!("#{:02x}{:02x}{:02x}", self.r, self.g, self.b)
    }

    /// WCAG relative luminance.
    pub fn luminance(&self) -> f32 {
        let [r, g, b] = [self.r, self.g, self.b].map(|c| srgb_to_linear(c as f32 / 255.0));
        0.2126 * r + 0.7152 * g + 0.0722 * b
    }

    /// WCAG contrast ratio against `other` (1.0 to 21.0).
    pub fn contrast(&self, other: &Rgb) -> f32 {
        let (a, b) = (self.luminance(), other.luminance());
        let (hi, lo) = if a > b { (a, b) } else { (b, a) };
        (hi + 0.05) / (lo + 0.05)
    }

    pub fn to_oklab(&self) -> Lab {
        let [r, g, b] = [self.r, self.g, self.b].map(|c| srgb_to_linear(c as f32 / 255.0));
        linear_to_oklab(r, g, b)
    }
}

/// OKLab coordinates.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Lab {
    pub l: f32,
    pub a: f32,
    pub b: f32,
}

impl Lab {
    pub fn chroma(&self) -> f32 {
        self.a.hypot(self.b)
    }
    pub fn hue(&self) -> f32 {
        self.b.atan2(self.a)
    }
    pub fn to_lch(&self) -> Lch {
        Lch { l: self.l, c: self.chroma(), h: self.hue() }
    }
    pub fn dist2(&self, o: &Lab) -> f32 {
        (self.l - o.l).powi(2) + (self.a - o.a).powi(2) + (self.b - o.b).powi(2)
    }
}

/// OKLCH: lightness 0..1, chroma 0..~0.37, hue in radians.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Lch {
    pub l: f32,
    pub c: f32,
    pub h: f32,
}

impl Lch {
    pub const fn new(l: f32, c: f32, h: f32) -> Self {
        Self { l, c, h }
    }

    pub fn to_lab(&self) -> Lab {
        Lab { l: self.l, a: self.c * self.h.cos(), b: self.c * self.h.sin() }
    }

    /// Converts to sRGB, lowering chroma (never lightness or hue) until in gamut.
    pub fn to_rgb(&self) -> Rgb {
        let mut c = self.c;
        for _ in 0..32 {
            let lin = oklab_to_linear(Lch::new(self.l, c, self.h).to_lab());
            if lin.iter().all(|v| (-1e-4..=1.0001).contains(v)) {
                return linear_to_rgb(lin);
            }
            c *= 0.86;
        }
        linear_to_rgb(oklab_to_linear(Lab { l: self.l, a: 0.0, b: 0.0 }))
    }
}

/// Smallest angle between two hues, in radians (0..π).
pub fn hue_diff(a: f32, b: f32) -> f32 {
    let d = (a - b).abs() % std::f32::consts::TAU;
    if d > std::f32::consts::PI { std::f32::consts::TAU - d } else { d }
}

fn srgb_to_linear(c: f32) -> f32 {
    if c <= 0.04045 { c / 12.92 } else { ((c + 0.055) / 1.055).powf(2.4) }
}

fn linear_to_srgb(c: f32) -> f32 {
    if c <= 0.003_130_8 { 12.92 * c } else { 1.055 * c.powf(1.0 / 2.4) - 0.055 }
}

fn linear_to_rgb(lin: [f32; 3]) -> Rgb {
    let [r, g, b] = lin.map(|v| (linear_to_srgb(v.clamp(0.0, 1.0)) * 255.0).round() as u8);
    Rgb { r, g, b }
}

fn linear_to_oklab(r: f32, g: f32, b: f32) -> Lab {
    let l = (0.412_221_47 * r + 0.536_332_55 * g + 0.051_445_99 * b).cbrt();
    let m = (0.211_903_5 * r + 0.680_699_5 * g + 0.107_396_96 * b).cbrt();
    let s = (0.088_302_46 * r + 0.281_718_85 * g + 0.629_978_7 * b).cbrt();
    Lab {
        l: 0.210_454_26 * l + 0.793_617_8 * m - 0.004_072_047 * s,
        a: 1.977_998_5 * l - 2.428_592_2 * m + 0.450_593_7 * s,
        b: 0.025_904_037 * l + 0.782_771_77 * m - 0.808_675_77 * s,
    }
}

fn oklab_to_linear(c: Lab) -> [f32; 3] {
    let l = (c.l + 0.396_337_78 * c.a + 0.215_803_76 * c.b).powi(3);
    let m = (c.l - 0.105_561_346 * c.a - 0.063_854_17 * c.b).powi(3);
    let s = (c.l - 0.089_484_18 * c.a - 1.291_485_5 * c.b).powi(3);
    [
        4.076_741_7 * l - 3.307_711_6 * m + 0.230_969_94 * s,
        -1.268_438 * l + 2.609_757_4 * m - 0.341_319_38 * s,
        -0.004_196_086_3 * l - 0.703_418_6 * m + 1.707_614_7 * s,
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn oklab_round_trips() {
        for rgb in [Rgb::new(255, 0, 0), Rgb::new(12, 200, 90), Rgb::new(250, 250, 250), Rgb::new(0, 0, 0)] {
            let back = rgb.to_oklab().to_lch().to_rgb();
            assert!((rgb.r as i32 - back.r as i32).abs() <= 1, "{rgb:?} -> {back:?}");
            assert!((rgb.g as i32 - back.g as i32).abs() <= 1, "{rgb:?} -> {back:?}");
            assert!((rgb.b as i32 - back.b as i32).abs() <= 1, "{rgb:?} -> {back:?}");
        }
    }

    #[test]
    fn contrast_matches_wcag() {
        let c = Rgb::new(0, 0, 0).contrast(&Rgb::new(255, 255, 255));
        assert!((c - 21.0).abs() < 0.01);
    }

    #[test]
    fn out_of_gamut_lowers_chroma_only() {
        let rgb = Lch::new(0.9, 0.4, 2.0).to_rgb();
        let back = rgb.to_oklab();
        assert!((back.l - 0.9).abs() < 0.02);
    }
}
