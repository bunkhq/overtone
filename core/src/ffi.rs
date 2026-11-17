//! C ABI for the native shells.
//!
//! The surface is deliberately small and flat: plain-old-data structs for the
//! hot paths (palette, backdrop, clock) and JSON strings for structured,
//! infrequent results (lyrics). C# calls it through P/Invoke; Swift can call
//! the same functions through a bridging header.
//!
//! Conventions: functions return `0` on success and a negative code on bad
//! input. Strings in are UTF-8, NUL-terminated. Strings out are owned by Rust
//! and must be released with [`ot_string_free`]. Colours are `0x00RRGGBB`.

use crate::clock::{ClockChange, PlaybackClock};
use crate::palette::{backdrop, Palette, Tone, BACKDROP_SIZE};
use crate::Rgb;
use std::ffi::{c_char, CStr, CString};

pub const OT_OK: i32 = 0;
pub const OT_ERR_NULL: i32 = -1;
pub const OT_ERR_SIZE: i32 = -2;

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct OtPalette {
    /// 0 = dark card, 1 = light card.
    pub tone: u32,
    pub base: u32,
    pub accent: u32,
    pub ink: u32,
    pub ink_soft: u32,
    pub ink_faint: u32,
    pub mesh: [u32; 3],
}

fn pack(c: Rgb) -> u32 {
    (c.r as u32) << 16 | (c.g as u32) << 8 | c.b as u32
}

impl From<&Palette> for OtPalette {
    fn from(p: &Palette) -> Self {
        Self {
            tone: matches!(p.tone, Tone::Light) as u32,
            base: pack(p.base),
            accent: pack(p.accent),
            ink: pack(p.ink),
            ink_soft: pack(p.ink_soft),
            ink_faint: pack(p.ink_faint),
            mesh: p.mesh.map(pack),
        }
    }
}

fn unpack(c: u32) -> Rgb {
    Rgb::new((c >> 16) as u8, (c >> 8) as u8, c as u8)
}

/// Mixes two `0x00RRGGBB` colours in OKLCH (`t` in 0..1). Used for palette
/// cross-fades, so both shells blend through the same colours.
#[no_mangle]
pub extern "C" fn ot_mix_oklch(a: u32, b: u32, t: f32) -> u32 {
    pack(crate::color::mix_oklch(unpack(a), unpack(b), t))
}

/// Tints `base` toward `toward` by `t`, keeping `base`'s lightness so inks solved
/// against it stay legible. Used for the flat background's warm corner.
#[no_mangle]
pub extern "C" fn ot_tint_oklch(base: u32, toward: u32, t: f32) -> u32 {
    pack(crate::color::tint_oklch(unpack(base), unpack(toward), t))
}

/// The tray icon's three wave colours (0x00RRGGBB, top to bottom) for a palette.
/// Pass the palette's `base`, `accent`, `mesh` as returned by
/// [`ot_palette_from_rgba`]; `dark_taskbar` is 1 when the taskbar is dark.
///
/// # Safety
/// `palette` must point to a readable [`OtPalette`]; `out` to three writable `u32`s.
#[no_mangle]
pub unsafe extern "C" fn ot_tray_colors(palette: *const OtPalette, dark_taskbar: u8, out: *mut u32) -> i32 {
    let (Some(p), false) = (palette.as_ref(), out.is_null()) else { return OT_ERR_NULL };
    let pal = Palette {
        tone: if p.tone == 1 { Tone::Light } else { Tone::Dark },
        base: unpack(p.base),
        accent: unpack(p.accent),
        ink: unpack(p.ink),
        ink_soft: unpack(p.ink_soft),
        ink_faint: unpack(p.ink_faint),
        mesh: p.mesh.map(unpack),
    };
    let colors = crate::palette::tray_colors(&pal, dark_taskbar != 0);
    for (i, c) in colors.iter().enumerate() {
        *out.add(i) = pack(*c);
    }
    OT_OK
}

/// `text` with filtered words replaced by asterisks (see `censor.rs`).
/// `level`: 0 off, 1 slurs only, 2 slurs and swears. Free with `ot_string_free`.
///
/// # Safety
/// `text` must be null or valid NUL-terminated UTF-8.
#[no_mangle]
pub unsafe extern "C" fn ot_censor(text: *const c_char, level: i32) -> *mut c_char {
    into_c(crate::censor::censor(&read_str(text), crate::censor::CensorLevel::from_i32(level)))
}

/// Strength of that tint, shared so both shells use the same value.
#[no_mangle]
pub extern "C" fn ot_flat_tint() -> f32 {
    crate::palette::FLAT_TINT
}

/// Library version as a static NUL-terminated string. Do not free.
#[no_mangle]
pub extern "C" fn ot_version() -> *const c_char {
    concat!(env!("CARGO_PKG_VERSION"), "\0").as_ptr().cast()
}

/// Palette from straight RGBA8 pixels. `force_tone`: -1 auto, 0 dark, 1 light.
/// `rgba` may be null (or `len` 0) for the neutral palette.
///
/// # Safety
/// `rgba` must point to `len` readable bytes; `out` must be writable.
#[no_mangle]
pub unsafe extern "C" fn ot_palette_from_rgba(rgba: *const u8, len: usize, width: u32, height: u32, force_tone: i32, out: *mut OtPalette) -> i32 {
    if out.is_null() {
        return OT_ERR_NULL;
    }
    let force = match force_tone {
        0 => Some(Tone::Dark),
        1 => Some(Tone::Light),
        _ => None,
    };
    let palette = if rgba.is_null() || len == 0 {
        Palette::neutral()
    } else {
        let px = std::slice::from_raw_parts(rgba, len);
        Palette::from_rgba_with_tone(px, width as usize, height as usize, force)
    };
    *out = OtPalette::from(&palette);
    OT_OK
}

/// Side length of the backdrop written by [`ot_backdrop_from_rgba`].
#[no_mangle]
pub extern "C" fn ot_backdrop_size() -> u32 {
    BACKDROP_SIZE as u32
}

/// Writes a blurred, slightly saturated `BACKDROP_SIZE²` RGBA8 (premultiplied,
/// opaque) image for the card background. `out_len` must be at least
/// `BACKDROP_SIZE * BACKDROP_SIZE * 4`.
///
/// # Safety
/// `rgba` must point to `len` readable bytes; `out` to `out_len` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn ot_backdrop_from_rgba(rgba: *const u8, len: usize, width: u32, height: u32, out: *mut u8, out_len: usize) -> i32 {
    if rgba.is_null() || out.is_null() {
        return OT_ERR_NULL;
    }
    if out_len < BACKDROP_SIZE * BACKDROP_SIZE * 4 {
        return OT_ERR_SIZE;
    }
    let px = std::slice::from_raw_parts(rgba, len);
    match backdrop(px, width as usize, height as usize) {
        Some(img) => {
            std::ptr::copy_nonoverlapping(img.as_ptr(), out, img.len());
            OT_OK
        }
        None => OT_ERR_SIZE,
    }
}

// ── Playback clock ───────────────────────────────────────────────────────────

/// New clock. `duration_ms` < 0 means unknown. Free with [`ot_clock_free`].
#[no_mangle]
pub extern "C" fn ot_clock_new(duration_ms: i64) -> *mut PlaybackClock {
    Box::into_raw(Box::new(PlaybackClock::new(u64::try_from(duration_ms).ok())))
}

/// # Safety
/// `clock` must come from [`ot_clock_new`] and not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn ot_clock_free(clock: *mut PlaybackClock) {
    if !clock.is_null() {
        drop(Box::from_raw(clock));
    }
}

/// Feeds an OS timeline report. Returns 1 if animations should re-anchor,
/// 0 if the report was within jitter tolerance, negative on error.
///
/// # Safety
/// `clock` must be a live pointer from [`ot_clock_new`].
#[no_mangle]
pub unsafe extern "C" fn ot_clock_report(clock: *mut PlaybackClock, position_ms: u64, reported_at_ms: u64, playing: bool, rate: f64, now_ms: u64) -> i32 {
    match clock.as_mut() {
        Some(c) => match c.report(position_ms, reported_at_ms, playing, rate, now_ms) {
            ClockChange::Reanchored => 1,
            ClockChange::None => 0,
        },
        None => OT_ERR_NULL,
    }
}

/// Current position in ms (0 for a null clock).
///
/// # Safety
/// `clock` must be a live pointer from [`ot_clock_new`] or null.
#[no_mangle]
pub unsafe extern "C" fn ot_clock_position(clock: *const PlaybackClock, now_ms: u64) -> u64 {
    clock.as_ref().map_or(0, |c| c.position_at(now_ms))
}

/// Wall-clock ms until playback reaches `target_ms`; -1 when paused.
///
/// # Safety
/// `clock` must be a live pointer from [`ot_clock_new`] or null.
#[no_mangle]
pub unsafe extern "C" fn ot_clock_wall_ms_until(clock: *const PlaybackClock, target_ms: u64, now_ms: u64) -> i64 {
    clock.as_ref().and_then(|c| c.wall_ms_until(target_ms, now_ms)).map_or(-1, |v| v as i64)
}

// ── Lyrics ───────────────────────────────────────────────────────────────────

/// Frees a string returned by this library.
///
/// # Safety
/// `s` must come from this library and not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn ot_string_free(s: *mut c_char) {
    if !s.is_null() {
        drop(CString::from_raw(s));
    }
}

unsafe fn read_str(p: *const c_char) -> String {
    if p.is_null() { String::new() } else { CStr::from_ptr(p).to_string_lossy().into_owned() }
}

fn into_c(s: String) -> *mut c_char {
    CString::new(s.replace('\0', "")).map_or(std::ptr::null_mut(), CString::into_raw)
}

/// Blocking lyrics lookup (call off the UI thread). Returns JSON, never null:
///
/// ```json
/// {"status":"found","synced":true,"lines":[{"t":35180,"text":"..."}]}
/// {"status":"instrumental"} | {"status":"not_found"} | {"status":"error","error":"..."}
/// ```
///
/// `duration_ms` < 0 means unknown.
///
/// # Safety
/// String arguments must be null or valid NUL-terminated UTF-8.
#[cfg(feature = "net")]
#[no_mangle]
pub unsafe extern "C" fn ot_lyrics_lookup(title: *const c_char, artist: *const c_char, album: *const c_char, duration_ms: i64) -> *mut c_char {
    use crate::lyrics::{lrclib::Lrclib, LyricsProvider, LyricsResult};
    use serde_json::json;

    let track = crate::Track { title: read_str(title), artist: read_str(artist), album: read_str(album), duration_ms: u64::try_from(duration_ms).ok() };
    let value = match Lrclib::default().lookup(&track) {
        Ok(LyricsResult::Found(l)) => json!({
            "status": "found",
            "synced": l.synced,
            "lines": l.lines.iter().map(|x| json!({ "t": x.start_ms, "text": x.text })).collect::<Vec<_>>(),
        }),
        Ok(LyricsResult::Instrumental) => json!({ "status": "instrumental" }),
        Ok(LyricsResult::NotFound) => json!({ "status": "not_found" }),
        Err(e) => json!({ "status": "error", "error": e.to_string() }),
    };
    into_c(value.to_string())
}

/// Public cover-art URL for the track (iTunes Search), for Discord Rich Presence.
/// Blocking: call off the UI thread. Returns an empty string when nothing matches
/// confidently or the lookup fails; never null.
///
/// # Safety
/// String arguments must be null or valid NUL-terminated UTF-8.
#[cfg(feature = "net")]
#[no_mangle]
pub unsafe extern "C" fn ot_cover_url(title: *const c_char, artist: *const c_char, album: *const c_char, duration_ms: i64) -> *mut c_char {
    let track = crate::Track { title: read_str(title), artist: read_str(artist), album: read_str(album), duration_ms: u64::try_from(duration_ms).ok() };
    into_c(crate::artwork::cover_url(&track).ok().flatten().unwrap_or_default())
}

/// Whether a music catalog (iTunes, Deezer) knows this song: 1 yes, 0 no, -1 when
/// no catalog could be reached. Blocking: call off the UI thread.
///
/// # Safety
/// String arguments must be null or valid NUL-terminated UTF-8.
#[cfg(feature = "net")]
#[no_mangle]
pub unsafe extern "C" fn ot_is_catalog_song(title: *const c_char, artist: *const c_char) -> i32 {
    let track = crate::Track { title: read_str(title), artist: read_str(artist), album: String::new(), duration_ms: None };
    match crate::artwork::is_catalog_song(&track) {
        Ok(true) => 1,
        Ok(false) => 0,
        Err(_) => -1,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn palette_round_trip() {
        let px = [200u8, 40, 60, 255].repeat(16 * 16);
        let mut out = OtPalette::default();
        assert_eq!(unsafe { ot_palette_from_rgba(px.as_ptr(), px.len(), 16, 16, -1, &mut out) }, OT_OK);
        assert_ne!(out.base, out.ink);
        assert_eq!(unsafe { ot_palette_from_rgba(px.as_ptr(), px.len(), 16, 16, -1, std::ptr::null_mut()) }, OT_ERR_NULL);
        assert_eq!(unsafe { ot_palette_from_rgba(std::ptr::null(), 0, 0, 0, -1, &mut out) }, OT_OK);
    }

    #[test]
    fn backdrop_checks_sizes() {
        let px = [10u8, 20, 30, 255].repeat(8 * 8);
        let mut out = vec![0u8; BACKDROP_SIZE * BACKDROP_SIZE * 4];
        assert_eq!(unsafe { ot_backdrop_from_rgba(px.as_ptr(), px.len(), 8, 8, out.as_mut_ptr(), out.len()) }, OT_OK);
        assert_eq!(out[3], 255);
        assert_eq!(unsafe { ot_backdrop_from_rgba(px.as_ptr(), px.len(), 8, 8, out.as_mut_ptr(), 4) }, OT_ERR_SIZE);
    }

    #[test]
    fn clock_round_trip() {
        unsafe {
            let c = ot_clock_new(10_000);
            assert_eq!(ot_clock_report(c, 1_000, 0, true, 1.0, 0), 1);
            assert_eq!(ot_clock_position(c, 500), 1_500);
            assert_eq!(ot_clock_wall_ms_until(c, 2_000, 0), 1_000);
            ot_clock_free(c);
            assert_eq!(ot_clock_report(std::ptr::null_mut(), 0, 0, true, 1.0, 0), OT_ERR_NULL);
        }
    }

    #[test]
    fn version_is_c_string() {
        let v = unsafe { CStr::from_ptr(ot_version()) };
        assert_eq!(v.to_str().unwrap(), env!("CARGO_PKG_VERSION"));
    }
}
