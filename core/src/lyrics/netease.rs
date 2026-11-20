//! NetEase Cloud Music: a large catalogue of synced lyrics, used as the fallback
//! when LRCLIB has no copy timed against the track's recording. Its public web
//! endpoints need no key (only a `Referer`), and every search hit carries the
//! release's duration, so the same timing rules apply as for LRCLIB.
//!
//! Search on cleaned artist + title, keep hits whose names match, fetch lyrics
//! for the closest few. Credit lines NetEase prepends ("作词 : …", "Producer: …")
//! are dropped; its "pure music" marker means instrumental.

use super::{Candidate, LyricsError, LyricsProvider, LyricsResult, USER_AGENT};
use crate::matcher::{clean_artist, clean_title, key};
use crate::{Lyrics, Track};
use serde::Deserialize;
use std::time::Duration;

const BASE: &str = "https://music.163.com/api";
/// Lyric fetches per lookup: the nearest durations among name matches.
const MAX_FETCHES: usize = 3;

#[derive(Debug, Deserialize)]
struct SearchResponse {
    result: Option<SearchResult>,
}

#[derive(Debug, Deserialize)]
struct SearchResult {
    #[serde(default)]
    songs: Vec<Song>,
}

#[derive(Debug, Deserialize)]
struct Song {
    id: u64,
    name: Option<String>,
    /// Milliseconds.
    duration: Option<u64>,
    #[serde(default)]
    artists: Vec<Artist>,
}

#[derive(Debug, Deserialize)]
struct Artist {
    name: Option<String>,
}

#[derive(Debug, Deserialize)]
struct LyricResponse {
    lrc: Option<LyricBody>,
    #[serde(default)]
    nolyric: bool,
    #[serde(default)]
    uncollected: bool,
}

#[derive(Debug, Deserialize)]
struct LyricBody {
    lyric: Option<String>,
}

pub struct Netease {
    agent: ureq::Agent,
    base: String,
}

impl Default for Netease {
    fn default() -> Self {
        Self::new(BASE)
    }
}

impl Netease {
    pub fn new(base: &str) -> Self {
        let agent = ureq::AgentBuilder::new().timeout(Duration::from_secs(6)).user_agent(USER_AGENT).build();
        Self { agent, base: base.trim_end_matches('/').to_string() }
    }

    fn call<T: serde::de::DeserializeOwned>(&self, path: &str, query: &[(&str, &str)]) -> Result<T, LyricsError> {
        let mut req = self.agent.get(&format!("{}{path}", self.base)).set("Referer", "https://music.163.com/");
        for (k, v) in query {
            req = req.query(k, v);
        }
        req.call()
            .map_err(|e| LyricsError::Transport(e.to_string()))?
            .into_json()
            .map_err(|e| LyricsError::Malformed(e.to_string()))
    }

    fn search(&self, artist: &str, title: &str) -> Result<Vec<Song>, LyricsError> {
        let r: SearchResponse = self.call("/search/get", &[("s", &format!("{artist} {title}")), ("type", "1"), ("limit", "15")])?;
        Ok(r.result.map(|r| r.songs).unwrap_or_default())
    }

    fn lyrics(&self, id: u64) -> Result<Option<LyricsResult>, LyricsError> {
        let r: LyricResponse = self.call("/song/lyric", &[("id", &id.to_string()), ("lv", "1"), ("kv", "1"), ("tv", "-1")])?;
        if r.nolyric {
            return Ok(Some(LyricsResult::Instrumental));
        }
        if r.uncollected {
            return Ok(None);
        }
        Ok(r.lrc.and_then(|b| b.lyric).as_deref().and_then(parse))
    }
}

/// NetEase's marker for an instrumental track.
const PURE_MUSIC: &str = "纯音乐，请欣赏";

/// Words that open a credit line: "作词 : X", "Composer: Y".
const CREDITS: &[&str] = &[
    "作词", "作曲", "编曲", "制作", "监制", "混音", "母带", "和声", "录音", "吉他", "贝斯", "鼓", "键盘", "弦乐", "出品", "发行", "企划",
    "统筹", "人声", "后期", "lyricist", "lyrics", "composer", "composed", "written", "arranger", "arranged", "producer",
    "produced", "mixing", "mixed", "mastering", "mastered", "vocal", "guitar", "bass", "drums", "keyboard", "recording", "engineer",
];

fn is_credit(text: &str) -> bool {
    let Some(i) = text.find([':', '：']) else { return false };
    let label = text[..i].trim().to_lowercase();
    label.chars().count() <= 24 && CREDITS.iter().any(|c| label.contains(c))
}

fn parse(src: &str) -> Option<LyricsResult> {
    if src.contains(PURE_MUSIC) {
        return Some(LyricsResult::Instrumental);
    }
    let mut lyrics = Lyrics::parse_lrc(src)?;
    // Credits sit at the top; stop at the first real line so a lyric with a colon survives.
    let first = lyrics.lines.iter().position(|l| !l.text.is_empty() && !is_credit(&l.text)).unwrap_or(lyrics.lines.len());
    lyrics.lines.drain(..first);
    while lyrics.lines.first().is_some_and(|l| l.text.is_empty()) {
        lyrics.lines.remove(0);
    }
    // NetEase pads untimed songs with every line at 00:00: that isn't synced.
    if lyrics.lines.len() > 1 && lyrics.lines.iter().all(|l| l.start_ms == lyrics.lines[0].start_ms) {
        lyrics.synced = false;
    }
    (!lyrics.lines.is_empty()).then_some(LyricsResult::Found(lyrics))
}

impl LyricsProvider for Netease {
    fn name(&self) -> &'static str {
        "NetEase"
    }

    fn candidates(&self, track: &Track) -> Result<Vec<Candidate>, LyricsError> {
        if track.title.trim().is_empty() || track.artist.trim().is_empty() {
            return Ok(Vec::new());
        }
        let (artist, title) = (clean_artist(&track.artist), clean_title(&track.title));
        let (want_artist, want_title) = (key(&artist), key(&title));
        let mut hits: Vec<(bool, Option<f64>, u64)> = self
            .search(&artist, &title)?
            .into_iter()
            .filter_map(|s| {
                let t = key(&clean_title(s.name.as_deref().unwrap_or_default()));
                let artists: Vec<String> = s.artists.iter().filter_map(|a| a.name.as_deref()).map(key).filter(|a| !a.is_empty()).collect();
                let artist_ok = artists.iter().any(|a| a.contains(&want_artist) || want_artist.contains(a.as_str()));
                let title_ok = !t.is_empty() && (t.contains(&want_title) || want_title.contains(&t));
                if !artist_ok || !title_ok {
                    return None;
                }
                let exact = t == want_title && artists.contains(&want_artist);
                let gap = track.duration_ms.zip(s.duration).map(|(w, h)| w.abs_diff(h) as f64 / 1000.0);
                Some((exact, gap, s.id))
            })
            .collect();
        // Exact names first, then nearest duration.
        hits.sort_by_key(|(exact, gap, _)| (!exact, gap.map_or(u64::MAX, |g| (g * 2.0).round() as u64)));

        let mut out = Vec::new();
        for (exact, gap_s, id) in hits.into_iter().take(MAX_FETCHES) {
            if let Some(result) = self.lyrics(id)? {
                let c = Candidate { result, gap_s, exact };
                let strong = c.is_strong(track.duration_ms);
                out.push(c);
                if strong {
                    break;
                }
            }
        }
        Ok(out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strips_credits_but_keeps_lyrics_with_colons() {
        let src = "[00:00.000] 作词 : Someone\n[00:01.000] 作曲 : Other\n[00:02.00] Producer: X\n[00:10.00]Note: this is a lyric\n[00:12.00]Next";
        match parse(src) {
            Some(LyricsResult::Found(l)) => {
                assert!(l.synced);
                assert_eq!(l.lines[0].text, "Note: this is a lyric");
                assert_eq!(l.lines.len(), 2);
            }
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn pure_music_is_instrumental() {
        assert_eq!(parse("[00:00.00]纯音乐，请欣赏"), Some(LyricsResult::Instrumental));
    }

    #[test]
    fn all_zero_stamps_are_unsynced() {
        match parse("[00:00.00]a\n[00:00.00]b") {
            Some(LyricsResult::Found(l)) => assert!(!l.synced),
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn search_response_parses() {
        let r: SearchResponse = serde_json::from_str(r#"{"result":{"songs":[{"id":1,"name":"Get Lucky","duration":248476,"artists":[{"name":"Daft Punk"}]}]}}"#).unwrap();
        assert_eq!(r.result.unwrap().songs[0].duration, Some(248_476));
        let empty: SearchResponse = serde_json::from_str(r#"{"result":{},"code":200}"#).unwrap();
        assert!(empty.result.unwrap().songs.is_empty());
    }
}
