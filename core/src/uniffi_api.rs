//! UniFFI exports: the surface the macOS shell calls (the Windows shell uses
//! the C ABI in `ffi.rs`; both wrap the same core functions).
//!
//! Swift bindings come from `uniffi-bindgen` (see `scripts/build-core-macos.sh`).
//! Types the shell sees are either the core's own plain structs (derived under
//! the `uniffi` feature) or thin wrappers here where the core type doesn't map
//! cleanly (fixed-size arrays, `&mut self` methods).

use crate::clock::{self, ClockChange};
use crate::{lrc, palette, LyricLine, Rgb, Tone};
use std::sync::{Arc, Mutex};

/// Colours for one track, ready to draw. See `docs/ARCHITECTURE.md` §7.
#[derive(Debug, Clone, PartialEq, uniffi::Record)]
pub struct OverlayPalette {
    pub tone: Tone,
    pub base: Rgb,
    pub accent: Rgb,
    pub ink: Rgb,
    pub ink_soft: Rgb,
    pub ink_faint: Rgb,
    /// Three stops for the blurred backdrop, most dominant first.
    pub mesh: Vec<Rgb>,
    /// The gradient background's top-left corner: `base` tinted toward `accent`
    /// with its luminance held, so inks keep their contrast (as on Windows).
    pub corner: Rgb,
}

impl From<palette::Palette> for OverlayPalette {
    fn from(p: palette::Palette) -> Self {
        let corner = crate::color::tint_oklch(p.base, p.accent, palette::FLAT_TINT);
        Self { tone: p.tone, base: p.base, accent: p.accent, ink: p.ink, ink_soft: p.ink_soft, ink_faint: p.ink_faint, mesh: p.mesh.to_vec(), corner }
    }
}

/// Palette from straight (non-premultiplied) RGBA pixels, row-major.
/// Pass a small image (64×64 is plenty); larger ones are sampled down anyway.
#[uniffi::export]
pub fn palette_from_rgba(rgba: Vec<u8>, width: u32, height: u32, force_tone: Option<Tone>) -> OverlayPalette {
    palette::Palette::from_rgba_with_tone(&rgba, width as usize, height as usize, force_tone).into()
}

/// `text` with filtered words replaced by asterisks, one per character.
#[uniffi::export]
pub fn censor(text: String, level: crate::censor::CensorLevel) -> String {
    crate::censor::censor(&text, level)
}

/// Three vivid colours for a coloured menu-bar / tray icon (the highlight first,
/// then the most different hues among the cover's colours), lit for the bar's
/// appearance and at least 3:1 against it. Same as the Windows tray icon.
#[uniffi::export]
pub fn tray_colors(palette: OverlayPalette, dark_bar: bool) -> Vec<Rgb> {
    let mesh = |i: usize| palette.mesh.get(i).or(palette.mesh.first()).copied().unwrap_or(palette.base);
    let p = palette::Palette {
        tone: palette.tone,
        base: palette.base,
        accent: palette.accent,
        ink: palette.ink,
        ink_soft: palette.ink_soft,
        ink_faint: palette.ink_faint,
        mesh: [mesh(0), mesh(1), mesh(2)],
    };
    palette::tray_colors(&p, dark_bar).to_vec()
}

/// The palette to show when there is no artwork.
#[uniffi::export]
pub fn neutral_palette() -> OverlayPalette {
    palette::Palette::neutral().into()
}

/// Parsed lyrics with position lookups.
#[derive(Debug, uniffi::Object)]
pub struct LyricsDoc(lrc::Lyrics);

/// Parses LRC text; `None` if it has no timed lines.
#[uniffi::export]
pub fn parse_lrc(src: String) -> Option<Arc<LyricsDoc>> {
    lrc::Lyrics::parse_lrc(&src).map(|l| Arc::new(LyricsDoc(l)))
}

/// Unsynced lyrics, one entry per non-empty line.
#[uniffi::export]
pub fn plain_lyrics(src: String) -> Arc<LyricsDoc> {
    Arc::new(LyricsDoc(lrc::Lyrics::plain(&src)))
}

#[uniffi::export]
impl LyricsDoc {
    pub fn lines(&self) -> Vec<LyricLine> {
        self.0.lines.clone()
    }

    pub fn synced(&self) -> bool {
        self.0.synced
    }

    /// Index of the line active at `position_ms`; `None` before the first line or when unsynced.
    pub fn line_at(&self, position_ms: u64) -> Option<u32> {
        self.0.line_at(position_ms).map(|i| i as u32)
    }

    /// Track position at which the active line next changes.
    pub fn next_change_ms(&self, position_ms: u64) -> Option<u64> {
        self.0.next_change_ms(position_ms)
    }
}

/// Playback position interpolation between OS reports. Thread-safe wrapper
/// around [`clock::PlaybackClock`]; times are milliseconds on one monotonic clock.
#[derive(Debug, uniffi::Object)]
pub struct PlaybackClock(Mutex<clock::PlaybackClock>);

#[uniffi::export]
impl PlaybackClock {
    #[uniffi::constructor]
    pub fn new(duration_ms: Option<u64>) -> Arc<Self> {
        Arc::new(Self(Mutex::new(clock::PlaybackClock::new(duration_ms))))
    }

    /// Feeds an OS report: `position_ms` was true at `reported_at_ms`.
    pub fn report(&self, position_ms: u64, reported_at_ms: u64, playing: bool, rate: f64, now_ms: u64) -> ClockChange {
        self.lock().report(position_ms, reported_at_ms, playing, rate, now_ms)
    }

    pub fn playing(&self) -> bool {
        self.lock().playing()
    }

    pub fn rate(&self) -> f64 {
        self.lock().rate()
    }

    pub fn duration_ms(&self) -> Option<u64> {
        self.lock().duration_ms()
    }

    pub fn position_at(&self, now_ms: u64) -> u64 {
        self.lock().position_at(now_ms)
    }

    pub fn progress_at(&self, now_ms: u64) -> Option<f64> {
        self.lock().progress_at(now_ms)
    }

    pub fn remaining_wall_ms(&self, now_ms: u64) -> Option<u64> {
        self.lock().remaining_wall_ms(now_ms)
    }

    pub fn wall_ms_until(&self, target_ms: u64, now_ms: u64) -> Option<u64> {
        self.lock().wall_ms_until(target_ms, now_ms)
    }
}

impl PlaybackClock {
    fn lock(&self) -> std::sync::MutexGuard<'_, clock::PlaybackClock> {
        // The clock has no invariants a panic could break; recover from poisoning.
        self.0.lock().unwrap_or_else(|e| e.into_inner())
    }
}

#[cfg(feature = "net")]
pub use net::*;

#[cfg(feature = "net")]
mod net {
    use super::LyricsDoc;
    use crate::lyrics::{cache::LyricsCache, Chain, LyricsError, LyricsProvider, LyricsResult};
    use crate::Track;
    use std::sync::Arc;

    #[derive(Debug, uniffi::Enum)]
    pub enum LyricsOutcome {
        Found { lyrics: Arc<LyricsDoc> },
        /// The provider marks the track as instrumental.
        Instrumental,
        NotFound,
    }

    #[derive(Debug, uniffi::Error)]
    pub enum LyricsFetchError {
        /// Network or HTTP failure: worth retrying later. Not cached.
        Transport { message: String },
        Malformed { message: String },
    }

    impl std::fmt::Display for LyricsFetchError {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            match self {
                Self::Transport { message } => write!(f, "lyrics request failed: {message}"),
                Self::Malformed { message } => write!(f, "lyrics response malformed: {message}"),
            }
        }
    }

    impl std::error::Error for LyricsFetchError {}

    impl From<LyricsError> for LyricsFetchError {
        fn from(e: LyricsError) -> Self {
            match e {
                LyricsError::Transport(message) => Self::Transport { message },
                LyricsError::Malformed(message) => Self::Malformed { message },
            }
        }
    }

    fn outcome(r: LyricsResult) -> LyricsOutcome {
        match r {
            LyricsResult::Found(l) => LyricsOutcome::Found { lyrics: Arc::new(LyricsDoc(l)) },
            LyricsResult::Instrumental => LyricsOutcome::Instrumental,
            LyricsResult::NotFound => LyricsOutcome::NotFound,
        }
    }

    /// A public URL for the track's cover (iTunes, then Deezer), for Discord,
    /// which can't take pixels. Blocking (network): call it off the UI thread.
    #[uniffi::export]
    pub fn cover_url(track: Track) -> Option<String> {
        crate::artwork::cover_url(&track).ok().flatten()
    }

    /// Whether iTunes or Deezer knows this song ("music only": a random video in a
    /// browser doesn't pass, an official music video does). `None` when neither
    /// catalog could be reached. Blocking (network): call it off the UI thread.
    #[uniffi::export]
    pub fn is_catalog_song(track: Track) -> Option<bool> {
        crate::artwork::is_catalog_song(&track).ok()
    }

    /// Lyrics lookups (LRCLIB, then NetEase) with an optional on-disk cache.
    #[derive(uniffi::Object)]
    pub struct LyricsClient {
        provider: Chain,
        cache: Option<LyricsCache>,
    }

    #[uniffi::export]
    impl LyricsClient {
        /// `cache_dir`: where to keep cached lookups, or `None` for no cache.
        #[uniffi::constructor]
        pub fn new(cache_dir: Option<String>) -> Arc<Self> {
            Arc::new(Self { provider: Chain::default(), cache: cache_dir.map(LyricsCache::new) })
        }

        /// Blocking (network): call it off the UI thread.
        pub fn lookup(&self, track: Track) -> Result<LyricsOutcome, LyricsFetchError> {
            if let Some(hit) = self.cache.as_ref().and_then(|c| c.get(&track)) {
                return Ok(outcome(hit));
            }
            let result = self.provider.lookup(&track)?;
            if let Some(c) = &self.cache {
                c.put(&track, &result);
            }
            Ok(outcome(result))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lyrics_doc_lookups() {
        let doc = parse_lrc("[00:01.00]a\n[00:03.00]b".into()).unwrap();
        assert!(doc.synced());
        assert_eq!(doc.line_at(500), None);
        assert_eq!(doc.line_at(1500), Some(0));
        assert_eq!(doc.next_change_ms(1500), Some(3000));
        assert!(parse_lrc("no stamps".into()).is_none());
    }

    #[test]
    fn palette_mesh_has_three_stops() {
        let p = palette_from_rgba([200, 40, 60, 255].repeat(16), 4, 4, None);
        assert_eq!(p.mesh.len(), 3);
        assert_eq!(neutral_palette().mesh.len(), 3);
    }

    #[test]
    fn clock_wrapper_interpolates() {
        let c = PlaybackClock::new(Some(10_000));
        c.report(1_000, 0, true, 1.0, 0);
        assert_eq!(c.position_at(500), 1_500);
        assert_eq!(c.remaining_wall_ms(500), Some(8_500));
    }
}
