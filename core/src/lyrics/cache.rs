//! On-disk lyrics cache: one small JSON file per track.
//!
//! Keyed by normalised artist + title + duration bucket, so the same song
//! reported with "- Remastered" or slightly different durations hits the same
//! entry. "Not found" is cached too, for [`NOT_FOUND_TTL`], so missing tracks
//! aren't refetched on every play. Transport errors are never cached.
//!
//! A plain directory of files instead of SQLite: entries are tiny, written once
//! and read by key, so a database would add a dependency without adding much.

use super::LyricsResult;
use crate::matcher::{clean_artist, clean_title, key};
use crate::{LyricLine, Lyrics, Track};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const NOT_FOUND_TTL: Duration = Duration::from_secs(7 * 24 * 60 * 60);
/// Durations within the same bucket share an entry (releases differ by a second or two).
const DURATION_BUCKET_S: u64 = 4;
/// Bump when the entry format or matching changes, so old entries are ignored.
const VERSION: u32 = 1;

#[derive(Serialize, Deserialize)]
struct Entry {
    v: u32,
    /// Unix seconds when written.
    at: u64,
    kind: Kind,
    #[serde(default)]
    synced: bool,
    #[serde(default)]
    lines: Vec<(u64, String)>,
}

#[derive(Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
enum Kind {
    Found,
    Instrumental,
    NotFound,
}

pub struct LyricsCache {
    dir: PathBuf,
}

impl LyricsCache {
    pub fn new(dir: impl Into<PathBuf>) -> Self {
        Self { dir: dir.into() }
    }

    pub fn dir(&self) -> &Path {
        &self.dir
    }

    pub fn get(&self, track: &Track) -> Option<LyricsResult> {
        self.get_at(track, now_s())
    }

    pub fn put(&self, track: &Track, result: &LyricsResult) {
        self.put_at(track, result, now_s());
    }

    fn get_at(&self, track: &Track, now: u64) -> Option<LyricsResult> {
        let text = std::fs::read_to_string(self.path(track)).ok()?;
        let e: Entry = serde_json::from_str(&text).ok()?;
        if e.v != VERSION {
            return None;
        }
        Some(match e.kind {
            Kind::Found => LyricsResult::Found(Lyrics {
                lines: e.lines.into_iter().map(|(start_ms, text)| LyricLine { start_ms, text }).collect(),
                synced: e.synced,
            }),
            Kind::Instrumental => LyricsResult::Instrumental,
            Kind::NotFound if now.saturating_sub(e.at) < NOT_FOUND_TTL.as_secs() => LyricsResult::NotFound,
            Kind::NotFound => return None,
        })
    }

    fn put_at(&self, track: &Track, result: &LyricsResult, now: u64) {
        let (kind, synced, lines) = match result {
            LyricsResult::Found(l) => (Kind::Found, l.synced, l.lines.iter().map(|l| (l.start_ms, l.text.clone())).collect()),
            LyricsResult::Instrumental => (Kind::Instrumental, false, Vec::new()),
            LyricsResult::NotFound => (Kind::NotFound, false, Vec::new()),
        };
        let entry = Entry { v: VERSION, at: now, kind, synced, lines };
        let Ok(json) = serde_json::to_string(&entry) else { return };
        if std::fs::create_dir_all(&self.dir).is_err() {
            return;
        }
        // Write then rename, so a crash never leaves a half-written entry.
        let path = self.path(track);
        let tmp = path.with_extension("tmp");
        if std::fs::write(&tmp, json).is_ok() {
            let _ = std::fs::rename(&tmp, &path);
        }
    }

    fn path(&self, track: &Track) -> PathBuf {
        let bucket = track.duration_ms.map(|d| (d / 1000 + DURATION_BUCKET_S / 2) / DURATION_BUCKET_S);
        let id = format!("{}\u{1f}{}\u{1f}{:?}", key(&clean_artist(&track.artist)), key(&clean_title(&track.title)), bucket);
        self.dir.join(format!("{:016x}.json", fnv1a(id.as_bytes())))
    }
}

/// Stable across Rust releases, unlike `DefaultHasher`.
fn fnv1a(bytes: &[u8]) -> u64 {
    bytes.iter().fold(0xcbf2_9ce4_8422_2325, |h, b| (h ^ *b as u64).wrapping_mul(0x0000_0100_0000_01b3))
}

fn now_s() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp_dir(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("overtone-cache-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        d
    }

    fn track(title: &str, duration_ms: Option<u64>) -> Track {
        Track { title: title.into(), artist: "Artist".into(), album: String::new(), duration_ms }
    }

    #[test]
    fn round_trips_found_lyrics() {
        let c = LyricsCache::new(tmp_dir("found"));
        let lyrics = Lyrics { lines: vec![LyricLine { start_ms: 1200, text: "hello".into() }], synced: true };
        c.put(&track("Song", Some(200_000)), &LyricsResult::Found(lyrics.clone()));
        assert_eq!(c.get(&track("Song", Some(200_000))), Some(LyricsResult::Found(lyrics)));
    }

    #[test]
    fn normalised_names_and_nearby_durations_share_an_entry() {
        let c = LyricsCache::new(tmp_dir("norm"));
        c.put(&track("Song - Remastered 2011", Some(200_400)), &LyricsResult::Instrumental);
        assert_eq!(c.get(&track("Song", Some(201_000))), Some(LyricsResult::Instrumental));
        assert_eq!(c.get(&track("Song", Some(260_000))), None);
    }

    #[test]
    fn not_found_expires() {
        let c = LyricsCache::new(tmp_dir("ttl"));
        let t = track("Song", None);
        c.put_at(&t, &LyricsResult::NotFound, 1_000);
        assert_eq!(c.get_at(&t, 1_000 + 60), Some(LyricsResult::NotFound));
        assert_eq!(c.get_at(&t, 1_000 + NOT_FOUND_TTL.as_secs() + 1), None);
    }
}
