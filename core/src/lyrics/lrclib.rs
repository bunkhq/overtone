//! [LRCLIB](https://lrclib.net): free, keyless, community-run synced lyrics.
//!
//! Strategy: `GET /api/get` with exact artist/title/album/duration for each
//! query variant; if none hit, `GET /api/search`. Search hits must match the
//! names; a hit within ±2 s of the track's duration is accepted outright, and
//! one within ±10 s only when artist and title match exactly (releases of the
//! same recording often differ by a few seconds). Nearest duration wins, then
//! synced over plain.

use super::{LyricsError, LyricsProvider, LyricsResult, USER_AGENT};
use crate::matcher::{key, query_variants};
use crate::{Lyrics, Track};
use serde::Deserialize;
use std::time::Duration;

const BASE: &str = "https://lrclib.net/api";
const DURATION_TOLERANCE_S: f64 = 2.0;
/// Looser window, used only for exact artist + title matches.
const DURATION_TOLERANCE_EXACT_S: f64 = 10.0;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Record {
    track_name: Option<String>,
    artist_name: Option<String>,
    duration: Option<f64>,
    #[serde(default)]
    instrumental: bool,
    plain_lyrics: Option<String>,
    synced_lyrics: Option<String>,
}

pub struct Lrclib {
    agent: ureq::Agent,
    base: String,
}

impl Default for Lrclib {
    fn default() -> Self {
        Self::new(BASE)
    }
}

impl Lrclib {
    pub fn new(base: &str) -> Self {
        let agent = ureq::AgentBuilder::new().timeout(Duration::from_secs(8)).user_agent(USER_AGENT).build();
        Self { agent, base: base.trim_end_matches('/').to_string() }
    }

    fn get(&self, artist: &str, title: &str, album: &str, duration_s: Option<f64>) -> Result<Option<Record>, LyricsError> {
        let mut req = self.agent.get(&format!("{}/get", self.base)).query("artist_name", artist).query("track_name", title);
        if !album.is_empty() {
            req = req.query("album_name", album);
        }
        if let Some(d) = duration_s {
            req = req.query("duration", &format!("{}", d.round() as u64));
        }
        match req.call() {
            Ok(r) => r.into_json().map(Some).map_err(|e| LyricsError::Malformed(e.to_string())),
            Err(ureq::Error::Status(404, _)) => Ok(None),
            Err(e) => Err(LyricsError::Transport(e.to_string())),
        }
    }

    fn search(&self, artist: &str, title: &str) -> Result<Vec<Record>, LyricsError> {
        let req = self.agent.get(&format!("{}/search", self.base)).query("track_name", title).query("artist_name", artist);
        match req.call() {
            Ok(r) => r.into_json().map_err(|e| LyricsError::Malformed(e.to_string())),
            Err(ureq::Error::Status(404, _)) => Ok(Vec::new()),
            Err(e) => Err(LyricsError::Transport(e.to_string())),
        }
    }
}

fn to_result(r: &Record) -> Option<LyricsResult> {
    if r.instrumental {
        return Some(LyricsResult::Instrumental);
    }
    if let Some(l) = r.synced_lyrics.as_deref().and_then(Lyrics::parse_lrc) {
        return Some(LyricsResult::Found(l));
    }
    r.plain_lyrics.as_deref().map(Lyrics::plain).filter(|l| !l.lines.is_empty()).map(LyricsResult::Found)
}

/// Seconds between the record's duration and the track's (0 when either is unknown).
fn duration_gap(r: &Record, want_s: Option<f64>) -> f64 {
    match (r.duration, want_s) {
        (Some(have), Some(want)) => (have - want).abs(),
        _ => 0.0,
    }
}

/// Whether a search hit is the same song. `exact` = artist and title keys are equal.
fn acceptable(gap_s: f64, exact: bool) -> bool {
    gap_s <= DURATION_TOLERANCE_S || (exact && gap_s <= DURATION_TOLERANCE_EXACT_S)
}

impl LyricsProvider for Lrclib {
    fn name(&self) -> &'static str {
        "LRCLIB"
    }

    fn lookup(&self, track: &Track) -> Result<LyricsResult, LyricsError> {
        // Without an artist the match would be a guess; better to show nothing.
        if track.title.trim().is_empty() || track.artist.trim().is_empty() {
            return Ok(LyricsResult::NotFound);
        }
        let duration_s = track.duration_ms.map(|d| d as f64 / 1000.0);
        let variants = query_variants(&track.artist, &track.title);

        for (artist, title) in &variants {
            if let Some(rec) = self.get(artist, title, &track.album, duration_s)? {
                if let Some(res) = to_result(&rec) {
                    return Ok(res);
                }
            }
        }

        let (artist, title) = variants.last().expect("title is non-empty, so at least one variant");
        let (want_artist, want_title) = (key(artist), key(title));
        let mut hits: Vec<(f64, Record)> = self
            .search(artist, title)?
            .into_iter()
            .filter_map(|r| {
                let a = key(r.artist_name.as_deref().unwrap_or_default());
                let t = key(r.track_name.as_deref().unwrap_or_default());
                let names = (a.contains(&want_artist) || want_artist.contains(&a)) && (t.contains(&want_title) || want_title.contains(&t));
                let gap = duration_gap(&r, duration_s);
                (names && !a.is_empty() && !t.is_empty() && acceptable(gap, a == want_artist && t == want_title)).then_some((gap, r))
            })
            .collect();
        // Nearest duration first (whole seconds), then synced, then plain.
        hits.sort_by_key(|(gap, r)| (gap.round() as u64, r.synced_lyrics.is_none(), r.plain_lyrics.is_none()));
        Ok(hits.iter().find_map(|(_, r)| to_result(r)).unwrap_or(LyricsResult::NotFound))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rec(json: &str) -> Record {
        serde_json::from_str(json).unwrap()
    }

    #[test]
    fn prefers_synced_over_plain() {
        let r = rec(r#"{"trackName":"a","artistName":"b","duration":100,"instrumental":false,
            "plainLyrics":"x\ny","syncedLyrics":"[00:01.00]x\n[00:02.00]y"}"#);
        match to_result(&r) {
            Some(LyricsResult::Found(l)) => assert!(l.synced),
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn instrumental_and_empty() {
        assert_eq!(to_result(&rec(r#"{"instrumental":true}"#)), Some(LyricsResult::Instrumental));
        assert_eq!(to_result(&rec(r#"{"instrumental":false,"plainLyrics":"  \n"}"#)), None);
    }

    #[test]
    fn duration_window() {
        let r = rec(r#"{"duration":221.0}"#);
        assert!(acceptable(duration_gap(&r, Some(219.5)), false));
        assert!(!acceptable(duration_gap(&r, Some(217.0)), false));
        assert!(acceptable(duration_gap(&r, Some(217.0)), true)); // same song, different master
        assert!(!acceptable(duration_gap(&r, Some(200.0)), true));
        assert_eq!(duration_gap(&r, None), 0.0);
    }

    #[test]
    fn no_artist_skips_network() {
        let t = Track { title: "Song".into(), artist: "".into(), album: "".into(), duration_ms: None };
        // Unroutable base: would error if it tried the network.
        let p = Lrclib::new("http://127.0.0.1:9");
        assert_eq!(p.lookup(&t).unwrap(), LyricsResult::NotFound);
    }
}
