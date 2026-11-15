//! Playback clock: interpolates position between OS timeline updates.
//!
//! The OS reports `position` plus the moment it was true. Some players (Spotify
//! on Windows) only refresh that every few seconds, and their reports jitter by
//! a few hundred ms. The clock extrapolates between reports and ignores small
//! corrections so the progress bar and lyrics never visibly jump backwards.
//!
//! Times are plain milliseconds on any monotonic clock the shell chooses, so
//! the core never reads the system clock itself.

/// Corrections smaller than this are treated as jitter while playing.
pub const JITTER_TOLERANCE_MS: f64 = 300.0;

#[derive(Debug, Clone, PartialEq)]
pub struct PlaybackClock {
    anchor_position_ms: f64,
    anchor_time_ms: f64,
    rate: f64,
    playing: bool,
    duration_ms: Option<f64>,
}

/// What changed after [`PlaybackClock::report`], so the shell knows whether to
/// re-arm its progress animation and lyric timer.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ClockChange {
    /// Within jitter tolerance; keep running animations as they are.
    None,
    /// Play state, rate, or a real seek. Re-anchor animations.
    Reanchored,
}

impl Default for PlaybackClock {
    fn default() -> Self {
        Self { anchor_position_ms: 0.0, anchor_time_ms: 0.0, rate: 1.0, playing: false, duration_ms: None }
    }
}

impl PlaybackClock {
    pub fn new(duration_ms: Option<u64>) -> Self {
        Self { duration_ms: duration_ms.map(|d| d as f64), ..Self::default() }
    }

    pub fn playing(&self) -> bool {
        self.playing
    }

    pub fn rate(&self) -> f64 {
        self.rate
    }

    pub fn duration_ms(&self) -> Option<u64> {
        self.duration_ms.map(|d| d as u64)
    }

    /// Feeds an OS report: `position_ms` was true at `reported_at_ms`.
    pub fn report(&mut self, position_ms: u64, reported_at_ms: u64, playing: bool, rate: f64, now_ms: u64) -> ClockChange {
        let rate = if rate.is_finite() && rate > 0.0 { rate } else { 1.0 };
        let reported_at = (reported_at_ms as f64).min(now_ms as f64);
        let observed_now = position_ms as f64 + if playing { (now_ms as f64 - reported_at) * rate } else { 0.0 };
        let predicted_now = self.position_at_f(now_ms as f64);

        let state_changed = playing != self.playing || (rate - self.rate).abs() > 1e-3;
        let drift = (observed_now - predicted_now).abs();
        if !state_changed && playing && drift < JITTER_TOLERANCE_MS {
            return ClockChange::None;
        }
        self.anchor_position_ms = observed_now;
        self.anchor_time_ms = now_ms as f64;
        self.playing = playing;
        self.rate = rate;
        if !state_changed && !playing && drift < 1.0 { ClockChange::None } else { ClockChange::Reanchored }
    }

    /// Position at `now_ms`, clamped to the track's duration when known.
    pub fn position_at(&self, now_ms: u64) -> u64 {
        self.position_at_f(now_ms as f64).round() as u64
    }

    /// Fraction 0..=1 for the progress bar, or `None` without a duration.
    pub fn progress_at(&self, now_ms: u64) -> Option<f64> {
        let d = self.duration_ms.filter(|d| *d > 0.0)?;
        Some((self.position_at_f(now_ms as f64) / d).clamp(0.0, 1.0))
    }

    /// Milliseconds of wall time until the track ends, for a single linear
    /// compositor animation. `None` when paused or without a duration.
    pub fn remaining_wall_ms(&self, now_ms: u64) -> Option<u64> {
        if !self.playing {
            return None;
        }
        let d = self.duration_ms?;
        Some(((d - self.position_at_f(now_ms as f64)).max(0.0) / self.rate).round() as u64)
    }

    /// Wall time until playback reaches `target_ms` (e.g. the next lyric line).
    pub fn wall_ms_until(&self, target_ms: u64, now_ms: u64) -> Option<u64> {
        if !self.playing {
            return None;
        }
        let ahead = target_ms as f64 - self.position_at_f(now_ms as f64);
        Some((ahead.max(0.0) / self.rate).ceil() as u64)
    }

    fn position_at_f(&self, now_ms: f64) -> f64 {
        let mut p = self.anchor_position_ms;
        if self.playing {
            p += (now_ms - self.anchor_time_ms).max(0.0) * self.rate;
        }
        match self.duration_ms {
            Some(d) => p.clamp(0.0, d),
            None => p.max(0.0),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn extrapolates_while_playing() {
        let mut c = PlaybackClock::new(Some(200_000));
        assert_eq!(c.report(10_000, 1_000, true, 1.0, 1_000), ClockChange::Reanchored);
        assert_eq!(c.position_at(4_000), 13_000);
        assert_eq!(c.progress_at(4_000), Some(0.065));
    }

    #[test]
    fn stale_report_is_projected_forward() {
        // Position 10s was true 2s ago.
        let mut c = PlaybackClock::new(None);
        c.report(10_000, 1_000, true, 1.0, 3_000);
        assert_eq!(c.position_at(3_000), 12_000);
    }

    #[test]
    fn small_jitter_is_ignored_seek_is_not() {
        let mut c = PlaybackClock::new(Some(200_000));
        c.report(0, 0, true, 1.0, 0);
        assert_eq!(c.report(5_150, 5_000, true, 1.0, 5_000), ClockChange::None);
        assert_eq!(c.position_at(5_000), 5_000);
        assert_eq!(c.report(60_000, 6_000, true, 1.0, 6_000), ClockChange::Reanchored);
        assert_eq!(c.position_at(6_000), 60_000);
    }

    #[test]
    fn pause_freezes_and_reanchors() {
        let mut c = PlaybackClock::new(Some(200_000));
        c.report(0, 0, true, 1.0, 0);
        assert_eq!(c.report(4_000, 4_000, false, 1.0, 4_000), ClockChange::Reanchored);
        assert_eq!(c.position_at(9_000), 4_000);
        assert_eq!(c.remaining_wall_ms(9_000), None);
    }

    #[test]
    fn clamps_to_duration_and_handles_rate() {
        let mut c = PlaybackClock::new(Some(10_000));
        c.report(8_000, 0, true, 2.0, 0);
        assert_eq!(c.position_at(500), 9_000);
        assert_eq!(c.position_at(50_000), 10_000);
        assert_eq!(c.remaining_wall_ms(0), Some(1_000));
        assert_eq!(c.wall_ms_until(9_000, 0), Some(500));
    }

    #[test]
    fn bad_rate_defaults_to_one() {
        let mut c = PlaybackClock::new(None);
        c.report(0, 0, true, f64::NAN, 0);
        assert_eq!(c.position_at(1_000), 1_000);
    }
}
