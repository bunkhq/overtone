//! Overtone core: the logic both native shells share.
//!
//! The shells own everything the OS draws or reports (windows, tray, the
//! now-playing session, artwork decoding). This crate owns everything that is
//! maths, text or network, so both apps render identical palettes and
//! identical lyric timing.
//!
//! - [`palette`]: album artwork (RGBA) to a contrast-safe overlay palette.
//! - [`lrc`]: LRC parsing and line lookup by playback position.
//! - [`clock`]: playback position interpolation between OS updates.
//! - [`matcher`]: track-name normalisation for lyrics lookups.
//! - [`lyrics`]: lyrics providers (LRCLIB), behind the `net` feature.

pub mod clock;
pub mod color;
pub mod lrc;
#[cfg(feature = "net")]
pub mod lyrics;
pub mod matcher;
pub mod palette;

pub use clock::PlaybackClock;
pub use color::Rgb;
pub use lrc::{LyricLine, Lyrics};
pub use palette::{Palette, Tone};

/// What the shell knows about the current track, as reported by the OS.
#[derive(Debug, Clone, PartialEq)]
pub struct Track {
    pub title: String,
    /// Empty when the source sends none (Nintendo Music, Amazon's desktop app).
    pub artist: String,
    pub album: String,
    /// `None` when the source reports no timeline.
    pub duration_ms: Option<u64>,
}
