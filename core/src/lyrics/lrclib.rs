//! [LRCLIB](https://lrclib.net): free, keyless, community-run synced lyrics.
//!
//! Strategy: `GET /api/get` with exact artist/title/album/duration for each
//! query variant (LRCLIB itself only answers within ±2 s), stopping at the first
//! strong hit; then `GET /api/search`, whose hits must match the names. Every
//! hit becomes a [`Candidate`] with its duration gap, and [`super::choose`]
//! decides (the tolerances are in `lyrics/mod.rs`).
//!
//! Without an artist (a local file with no tags) the search runs on title and
//! album instead, and only an exact title on a matching album, at the same
//! duration, counts.

use super::{Candidate, LyricsError, LyricsProvider, LyricsResult, STRONG_GAP_S, USER_AGENT};
use crate::matcher::{clean_title, key, query_variants};
use crate::{Lyrics, Track};
use serde::Deserialize;
use std::time::Duration;

const BASE: &str = "https://lrclib.net/api";

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Record {
    track_name: Option<String>,
    artist_name: Option<String>,
    album_name: Option<String>,
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

    /// `artist` or `album` may be empty (then not sent).
    fn search(&self, artist: &str, title: &str, album: &str) -> Result<Vec<Record>, LyricsError> {
        let mut req = self.agent.get(&format!("{}/search", self.base)).query("track_name", title);
        if !artist.is_empty() {
            req = req.query("artist_name", artist);
        }
        if !album.is_empty() {
            req = req.query("album_name", album);
        }
        match req.call() {
            Ok(r) => r.into_json().map_err(|e| LyricsError::Malformed(e.to_string())),
            Err(ureq::Error::Status(404, _)) => Ok(Vec::new()),
            Err(e) => Err(LyricsError::Transport(e.to_string())),
        }
    }

    /// No artist: title and album must both match at the same duration, so a common
    /// title can't pull in a stranger's song.
    fn candidates_without_artist(&self, track: &Track, duration_s: Option<f64>) -> Result<Vec<Candidate>, LyricsError> {
        let (title, album) = (clean_title(&track.title), clean_title(&track.album));
        if track.album.trim().is_empty() || duration_s.is_none() {
            return Ok(Vec::new());
        }
        let (want_title, want_album) = (key(&title), key(&album));
        Ok(self
            .search("", &title, &album)?
            .into_iter()
            .filter(|r| {
                key(&clean_title(r.track_name.as_deref().unwrap_or_default())) == want_title
                    && key(&clean_title(r.album_name.as_deref().unwrap_or_default())) == want_album
                    && duration_gap(r, duration_s) <= STRONG_GAP_S
            })
            .flat_map(|r| candidates_of(&r, duration_s, false))
            .collect())
    }
}

fn to_result(r: &Record) -> Option<LyricsResult> {
    if r.instrumental {
        return Some(LyricsResult::Instrumental);
    }
    if let Some(l) = r.synced_lyrics.as_deref().and_then(Lyrics::parse_lrc) {
        return Some(LyricsResult::Found(l));
    }
    plain(r).map(LyricsResult::Found)
}

fn plain(r: &Record) -> Option<Lyrics> {
    r.plain_lyrics.as_deref().map(Lyrics::plain).filter(|l| !l.lines.is_empty())
}

/// The record's best result, plus its plain text when that is synced, so a synced
/// copy too far off in duration can still fall back to the words alone.
fn candidates_of(r: &Record, want_s: Option<f64>, exact: bool) -> Vec<Candidate> {
    let gap_s = want_s.zip(r.duration).map(|(w, h)| (h - w).abs());
    let Some(result) = to_result(r) else { return Vec::new() };
    let synced = matches!(&result, LyricsResult::Found(l) if l.synced);
    let mut out = vec![Candidate { result, gap_s, exact }];
    if synced {
        if let Some(l) = plain(r) {
            out.push(Candidate { result: LyricsResult::Found(l), gap_s, exact });
        }
    }
    out
}

/// Seconds between the record's duration and the track's (0 when either is unknown).
fn duration_gap(r: &Record, want_s: Option<f64>) -> f64 {
    match (r.duration, want_s) {
        (Some(have), Some(want)) => (have - want).abs(),
        _ => 0.0,
    }
}

impl LyricsProvider for Lrclib {
    fn name(&self) -> &'static str {
        "LRCLIB"
    }

    fn candidates(&self, track: &Track) -> Result<Vec<Candidate>, LyricsError> {
        if track.title.trim().is_empty() {
            return Ok(Vec::new());
        }
        let duration_s = track.duration_ms.map(|d| d as f64 / 1000.0);
        if track.artist.trim().is_empty() {
            return self.candidates_without_artist(track, duration_s);
        }
        let variants = query_variants(&track.artist, &track.title);
        let mut out = Vec::new();

        for (artist, title) in &variants {
            if let Some(rec) = self.get(artist, title, &track.album, duration_s)? {
                let found = candidates_of(&rec, duration_s, true);
                // /get answers on these names within ±2 s: synced lyrics or the
                // instrumental flag from it settle the question.
                let settled = found.iter().any(|c| c.is_strong(track.duration_ms) || c.result == LyricsResult::Instrumental);
                out.extend(found);
                if settled {
                    return Ok(out);
                }
            }
        }

        let (artist, title) = variants.last().expect("title is non-empty, so at least one variant");
        let (want_artist, want_title) = (key(artist), key(title));
        for r in self.search(artist, title, "")? {
            let a = key(r.artist_name.as_deref().unwrap_or_default());
            let t = key(r.track_name.as_deref().unwrap_or_default());
            let names = (a.contains(&want_artist) || want_artist.contains(&a)) && (t.contains(&want_title) || want_title.contains(&t));
            if names && !a.is_empty() && !t.is_empty() {
                out.extend(candidates_of(&r, duration_s, a == want_artist && t == want_title));
            }
        }
        Ok(out)
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
    fn candidates_carry_the_gap_and_a_plain_fallback() {
        let r = rec(r#"{"duration":221.0,"plainLyrics":"x\ny","syncedLyrics":"[00:01.00]x\n[00:02.00]y"}"#);
        let c = candidates_of(&r, Some(217.0), true);
        assert_eq!(c.len(), 2);
        assert_eq!(c[0].gap_s, Some(4.0));
        assert!(matches!(&c[1].result, LyricsResult::Found(l) if !l.synced));
        assert_eq!(candidates_of(&r, None, true)[0].gap_s, None);
        assert_eq!(duration_gap(&r, None), 0.0);
    }

    #[test]
    fn no_artist_and_no_album_skips_network() {
        let t = Track { title: "Song".into(), artist: "".into(), album: "".into(), duration_ms: Some(200_000) };
        // Unroutable base: would error if it tried the network.
        let p = Lrclib::new("http://127.0.0.1:9");
        assert_eq!(p.lookup(&t).unwrap(), LyricsResult::NotFound);
    }
}
