//! Lyrics providers.

pub mod lrclib;

use crate::{Lyrics, Track};

/// Identifies the app to providers, as LRCLIB asks.
pub const USER_AGENT: &str = concat!("Overtone/", env!("CARGO_PKG_VERSION"), " (https://github.com/bunkhq/overtone)");

#[derive(Debug)]
pub enum LyricsError {
    /// Network or HTTP failure (not "no lyrics"): worth retrying later.
    Transport(String),
    /// The provider answered with something unparseable.
    Malformed(String),
}

impl std::fmt::Display for LyricsError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Transport(e) => write!(f, "lyrics request failed: {e}"),
            Self::Malformed(e) => write!(f, "lyrics response malformed: {e}"),
        }
    }
}

impl std::error::Error for LyricsError {}

#[derive(Debug, Clone, PartialEq)]
pub enum LyricsResult {
    Found(Lyrics),
    /// The provider marks this track as instrumental.
    Instrumental,
    /// Nothing matched. Cache this too, so the track isn't refetched every play.
    NotFound,
}

pub trait LyricsProvider: Send + Sync {
    fn name(&self) -> &'static str;
    /// Blocking lookup; the shell calls it off the UI thread.
    fn lookup(&self, track: &Track) -> Result<LyricsResult, LyricsError>;
}
