//! Lyrics providers, and the policy that picks one result across them.
//!
//! Synced lyrics are timed against one particular release. When the player's
//! version is a few seconds longer or shorter (a remaster with more silence, a
//! single edit, a music video intro), every line lands early or late. So a
//! result is judged by how far its duration is from the track's:
//!
//! - within [`STRONG_GAP_S`]: the same recording. Used at once; later providers
//!   aren't asked.
//! - within [`SYNCED_EXACT_GAP_S`], with artist and title matching exactly:
//!   probably the same recording, used if no provider has a closer one.
//! - further out, synced lyrics are rejected: showing nothing beats lines that
//!   run steadily out of step. Plain (unsynced) lyrics and the instrumental flag
//!   don't depend on timing, so they keep the looser [`LOOSE_EXACT_GAP_S`].
//!
//! Synced lyrics whose last line starts after the track has ended are from a
//! longer version, whatever the reported duration says.
//!
//! [`Chain`] asks LRCLIB first, then NetEase Cloud Music, and stops at the first
//! strong match.

pub mod cache;
pub mod lrclib;
pub mod netease;

use crate::{Lyrics, Track};

/// Identifies the app to providers, as LRCLIB asks.
pub const USER_AGENT: &str = concat!("Overtone/", env!("CARGO_PKG_VERSION"), " (https://github.com/bunkhq/overtone)");

/// Same recording: accepted from any provider, and ends the search.
pub const STRONG_GAP_S: f64 = 2.0;
/// Synced lyrics this far off are used only for an exact artist + title match.
pub const SYNCED_EXACT_GAP_S: f64 = 4.0;
/// Plain lyrics and the instrumental flag, for an exact artist + title match.
pub const LOOSE_EXACT_GAP_S: f64 = 10.0;
/// Slack for the last synced line against the track's end (fade-outs, rounding).
const LAST_LINE_SLACK_MS: u64 = 2_000;

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

/// One result a provider found, with what the choice needs to know about it.
#[derive(Debug, Clone, PartialEq)]
pub struct Candidate {
    /// `Found` or `Instrumental`.
    pub result: LyricsResult,
    /// Seconds between the result's duration and the track's; `None` when either is unknown.
    pub gap_s: Option<f64>,
    /// Artist and title keys equal the track's (not merely contained in them).
    pub exact: bool,
}

impl Candidate {
    fn gap(&self) -> f64 {
        self.gap_s.unwrap_or(0.0)
    }

    fn synced(&self) -> bool {
        matches!(&self.result, LyricsResult::Found(l) if l.synced)
    }

    /// A synced result timed against the same recording as the track.
    pub fn is_strong(&self, duration_ms: Option<u64>) -> bool {
        self.synced() && self.gap() <= STRONG_GAP_S && fits(&self.result, duration_ms)
    }
}

/// Synced lines must end before the track does.
fn fits(result: &LyricsResult, duration_ms: Option<u64>) -> bool {
    match (result, duration_ms) {
        (LyricsResult::Found(l), Some(d)) if l.synced && d > 0 => l.lines.last().is_none_or(|x| x.start_ms <= d + LAST_LINE_SLACK_MS),
        _ => true,
    }
}

/// Picks the best candidate, in this order: a strong synced match; the
/// instrumental flag on the same recording; a close-enough synced match on exact
/// names; plain lyrics; the instrumental flag on exact names. Nearest duration
/// first within each tier, then the order given (provider priority).
pub fn choose(candidates: &[Candidate], duration_ms: Option<u64>) -> LyricsResult {
    let close = |c: &Candidate, exact_gap: f64| c.gap() <= STRONG_GAP_S || (c.exact && c.gap() <= exact_gap);
    let instrumental = |c: &&Candidate| c.result == LyricsResult::Instrumental;
    let plain = |c: &&Candidate| matches!(&c.result, LyricsResult::Found(l) if !l.synced);
    let tiers: [&dyn Fn(&&Candidate) -> bool; 5] = [
        &|c| c.is_strong(duration_ms),
        &|c| instrumental(c) && c.gap() <= STRONG_GAP_S,
        &|c| c.synced() && close(c, SYNCED_EXACT_GAP_S) && fits(&c.result, duration_ms),
        &|c| plain(c) && close(c, LOOSE_EXACT_GAP_S),
        &|c| instrumental(c) && close(c, LOOSE_EXACT_GAP_S),
    ];
    for tier in tiers {
        let best = candidates
            .iter()
            .filter(tier)
            // Half-second steps: providers round durations differently.
            .min_by_key(|c| (c.gap() * 2.0).round() as u64);
        if let Some(c) = best {
            return c.result.clone();
        }
    }
    LyricsResult::NotFound
}

pub trait LyricsProvider: Send + Sync {
    fn name(&self) -> &'static str;

    /// Everything plausibly matching the track, unfiltered by duration. Blocking.
    fn candidates(&self, track: &Track) -> Result<Vec<Candidate>, LyricsError>;

    /// Blocking lookup; the shell calls it off the UI thread.
    fn lookup(&self, track: &Track) -> Result<LyricsResult, LyricsError> {
        Ok(choose(&self.candidates(track)?, track.duration_ms))
    }
}

/// Providers in priority order. The first strong match wins; otherwise the best
/// candidate across all of them. A provider that fails is skipped, but if the
/// outcome is "not found" the error is returned instead, so a transient failure
/// isn't cached as a miss.
pub struct Chain {
    providers: Vec<Box<dyn LyricsProvider>>,
}

impl Default for Chain {
    fn default() -> Self {
        Self::new(vec![Box::new(lrclib::Lrclib::default()), Box::new(netease::Netease::default())])
    }
}

impl Chain {
    pub fn new(providers: Vec<Box<dyn LyricsProvider>>) -> Self {
        Self { providers }
    }
}

impl LyricsProvider for Chain {
    fn name(&self) -> &'static str {
        "chain"
    }

    fn candidates(&self, track: &Track) -> Result<Vec<Candidate>, LyricsError> {
        let mut all = Vec::new();
        let mut error = None;
        for p in &self.providers {
            match p.candidates(track) {
                Ok(found) => {
                    let strong = found.iter().any(|c| c.is_strong(track.duration_ms));
                    all.extend(found);
                    if strong {
                        break;
                    }
                }
                Err(e) => error = Some(e),
            }
        }
        match error {
            Some(e) if choose(&all, track.duration_ms) == LyricsResult::NotFound => Err(e),
            _ => Ok(all),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::LyricLine;

    fn synced(last_ms: u64) -> LyricsResult {
        LyricsResult::Found(Lyrics { lines: vec![LyricLine { start_ms: 1_000, text: "a".into() }, LyricLine { start_ms: last_ms, text: "b".into() }], synced: true })
    }

    fn plain() -> LyricsResult {
        LyricsResult::Found(Lyrics::plain("a\nb"))
    }

    fn c(result: LyricsResult, gap: f64, exact: bool) -> Candidate {
        Candidate { result, gap_s: Some(gap), exact }
    }

    #[test]
    fn nearest_strong_synced_wins() {
        let a = c(synced(5_000), 1.5, true);
        let b = c(synced(6_000), 0.4, false);
        assert_eq!(choose(&[a, b.clone()], Some(200_000)), b.result);
    }

    #[test]
    fn far_synced_is_rejected_even_on_exact_names() {
        assert_eq!(choose(&[c(synced(5_000), 7.0, true)], Some(200_000)), LyricsResult::NotFound);
        assert_eq!(choose(&[c(synced(5_000), 3.5, false)], Some(200_000)), LyricsResult::NotFound);
        assert_eq!(choose(&[c(synced(5_000), 3.5, true)], Some(200_000)), synced(5_000));
    }

    #[test]
    fn far_synced_falls_back_to_plain() {
        let got = choose(&[c(synced(5_000), 7.0, true), c(plain(), 7.0, true)], Some(200_000));
        assert_eq!(got, plain());
    }

    #[test]
    fn lines_past_the_end_mean_another_version() {
        assert_eq!(choose(&[c(synced(205_000), 0.0, true)], Some(200_000)), LyricsResult::NotFound);
        assert_eq!(choose(&[c(synced(201_500), 0.0, true)], Some(200_000)), synced(201_500));
    }

    #[test]
    fn strong_synced_beats_instrumental_flag_elsewhere() {
        let got = choose(&[c(LyricsResult::Instrumental, 6.0, true), c(synced(5_000), 0.5, true)], Some(200_000));
        assert_eq!(got, synced(5_000));
    }

    struct Fixed(Result<Vec<Candidate>, ()>);
    impl LyricsProvider for Fixed {
        fn name(&self) -> &'static str {
            "fixed"
        }
        fn candidates(&self, _: &Track) -> Result<Vec<Candidate>, LyricsError> {
            self.0.clone().map_err(|_| LyricsError::Transport("down".into()))
        }
    }

    fn track() -> Track {
        Track { title: "t".into(), artist: "a".into(), album: String::new(), duration_ms: Some(200_000) }
    }

    #[test]
    fn chain_prefers_a_later_closer_match() {
        let chain = Chain::new(vec![Box::new(Fixed(Ok(vec![c(synced(5_000), 3.0, true)]))), Box::new(Fixed(Ok(vec![c(synced(6_000), 0.2, true)])))]);
        assert_eq!(chain.lookup(&track()).unwrap(), synced(6_000));
    }

    #[test]
    fn chain_reports_errors_only_when_nothing_was_found() {
        let down = || Box::new(Fixed(Err(()))) as Box<dyn LyricsProvider>;
        let chain = Chain::new(vec![down(), Box::new(Fixed(Ok(vec![c(synced(5_000), 0.0, true)])))]);
        assert_eq!(chain.lookup(&track()).unwrap(), synced(5_000));
        let chain = Chain::new(vec![down(), Box::new(Fixed(Ok(vec![])))]);
        assert!(chain.lookup(&track()).is_err());
    }
}
