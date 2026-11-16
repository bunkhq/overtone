//! Public cover-art URLs, for places that need a link rather than pixels
//! (Discord Rich Presence). The OS hands the shell artwork bytes, not a URL.
//!
//! Sources, all free and keyless, tried in order of confidence:
//!
//! 1. Song search on iTunes and Deezer, accepted on an exact (normalised) title
//!    and matching artist; the nearest duration wins.
//! 2. Album search on iTunes and Deezer by the track's album name.
//! 3. The song results again, relaxed: one title containing the other
//!    ("Song" vs "Song (Acoustic)").
//! 4. The artist's photo on Deezer: not the cover, but better than nothing.
//!
//! A source that fails (offline, rate-limited) is skipped; it never blocks the rest.

use crate::lyrics::USER_AGENT;
use crate::matcher::{clean_artist, clean_title, key};
use crate::Track;
use serde::Deserialize;
use std::time::Duration;

const ITUNES: &str = "https://itunes.apple.com/search";
const DEEZER: &str = "https://api.deezer.com/search";
/// iTunes serves any square size by rewriting the file name.
const ITUNES_SIZE: &str = "600x600bb";

/// One search result, normalised across sources.
#[derive(Debug, Clone, PartialEq)]
struct Candidate {
    artist: String,
    /// Track or album title, depending on the search.
    title: String,
    url: String,
    duration_ms: Option<u64>,
}

/// How a cover was found, for logs and tests.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CoverSource {
    Song,
    Album,
    RelaxedSong,
    ArtistPhoto,
}

/// Looks up a public cover URL. `Ok(None)` when no source matches.
pub fn cover_url(track: &Track) -> Result<Option<String>, String> {
    Ok(cover(track)?.map(|(url, _)| url))
}

pub fn cover(track: &Track) -> Result<Option<(String, CoverSource)>, String> {
    if track.title.trim().is_empty() || track.artist.trim().is_empty() {
        return Ok(None);
    }
    let agent = ureq::AgentBuilder::new().timeout(Duration::from_secs(6)).user_agent(USER_AGENT).build();
    let (artist, title, album) = (clean_artist(&track.artist), clean_title(&track.title), clean_title(&track.album));

    let mut songs = itunes_songs(&agent, &artist, &title).unwrap_or_default();
    if let Some(url) = best_song(&songs, &artist, &title, track.duration_ms, false) {
        return Ok(Some((url, CoverSource::Song)));
    }
    let deezer = deezer_songs(&agent, &artist, &title).unwrap_or_default();
    if let Some(url) = best_song(&deezer, &artist, &title, track.duration_ms, false) {
        return Ok(Some((url, CoverSource::Song)));
    }
    songs.extend(deezer);

    if !album.is_empty() && key(&album) != key(&title) {
        for albums in [itunes_albums(&agent, &artist, &album), deezer_albums(&agent, &artist, &album)] {
            if let Some(url) = best_album(&albums.unwrap_or_default(), &artist, &album) {
                return Ok(Some((url, CoverSource::Album)));
            }
        }
    }

    if let Some(url) = best_song(&songs, &artist, &title, track.duration_ms, true) {
        return Ok(Some((url, CoverSource::RelaxedSong)));
    }
    Ok(deezer_artist_photo(&agent, &artist).ok().flatten().map(|u| (u, CoverSource::ArtistPhoto)))
}

/// Whether the track is a song a music catalog knows: an exact (normalised) title
/// and matching artist on iTunes or Deezer. Used to tell music from other media
/// (a random video in a browser tab) when the player gives no other signal.
/// `Err` when both catalogs were unreachable, so the caller can decide.
pub fn is_catalog_song(track: &Track) -> Result<bool, String> {
    if track.title.trim().is_empty() || track.artist.trim().is_empty() {
        return Ok(false);
    }
    let agent = ureq::AgentBuilder::new().timeout(Duration::from_secs(6)).user_agent(USER_AGENT).build();
    let (artist, title) = (clean_artist(&track.artist), clean_title(&track.title));
    let mut reached = false;
    for search in [itunes_songs as fn(&ureq::Agent, &str, &str) -> Result<Vec<Candidate>, String>, deezer_songs] {
        if let Ok(hits) = search(&agent, &artist, &title) {
            reached = true;
            if best_song(&hits, &artist, &title, None, false).is_some() {
                return Ok(true);
            }
        }
    }
    if reached { Ok(false) } else { Err("no catalog reachable".into()) }
}

fn artist_matches(have: &str, want: &str) -> bool {
    let (a, w) = (key(have), key(want));
    !a.is_empty() && (a.contains(&w) || w.contains(&a))
}

/// Exact: normalised titles equal. Relaxed: one contains the other.
fn best_song(c: &[Candidate], artist: &str, title: &str, duration_ms: Option<u64>, relaxed: bool) -> Option<String> {
    let want = key(title);
    c.iter()
        .filter(|h| artist_matches(&h.artist, artist))
        .filter(|h| {
            let t = key(&clean_title(&h.title));
            !t.is_empty() && if relaxed { t.contains(&want) || want.contains(&t) } else { t == want }
        })
        .min_by_key(|h| match (h.duration_ms, duration_ms) {
            (Some(have), Some(want)) => have.abs_diff(want),
            _ => u64::MAX / 2,
        })
        .map(|h| h.url.clone())
}

fn best_album(c: &[Candidate], artist: &str, album: &str) -> Option<String> {
    let want = key(album);
    c.iter()
        .find(|h| artist_matches(&h.artist, artist) && key(&clean_title(&h.title)) == want)
        .map(|h| h.url.clone())
}

// ── iTunes ───────────────────────────────────────────────────────────────────

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ItunesHit {
    artist_name: Option<String>,
    track_name: Option<String>,
    collection_name: Option<String>,
    artwork_url100: Option<String>,
    track_time_millis: Option<u64>,
}

#[derive(Debug, Deserialize)]
struct ItunesResults {
    #[serde(default)]
    results: Vec<ItunesHit>,
}

fn itunes(agent: &ureq::Agent, term: &str, entity: &str) -> Result<Vec<ItunesHit>, String> {
    let r: ItunesResults = agent
        .get(ITUNES)
        .query("term", term)
        .query("entity", entity)
        .query("limit", "15")
        .call()
        .map_err(|e| e.to_string())?
        .into_json()
        .map_err(|e| e.to_string())?;
    Ok(r.results)
}

fn itunes_url(u: &str) -> String {
    u.replace("100x100bb", ITUNES_SIZE)
}

fn itunes_songs(agent: &ureq::Agent, artist: &str, title: &str) -> Result<Vec<Candidate>, String> {
    Ok(from_itunes(itunes(agent, &format!("{artist} {title}"), "song")?, false))
}

fn itunes_albums(agent: &ureq::Agent, artist: &str, album: &str) -> Result<Vec<Candidate>, String> {
    Ok(from_itunes(itunes(agent, &format!("{artist} {album}"), "album")?, true))
}

fn from_itunes(hits: Vec<ItunesHit>, albums: bool) -> Vec<Candidate> {
    hits.into_iter()
        .filter_map(|h| {
            let title = if albums { h.collection_name } else { h.track_name }?;
            Some(Candidate { artist: h.artist_name?, title, url: itunes_url(&h.artwork_url100?), duration_ms: h.track_time_millis })
        })
        .collect()
}

// ── Deezer ───────────────────────────────────────────────────────────────────

#[derive(Debug, Deserialize)]
struct DeezerArtist {
    name: Option<String>,
    picture_xl: Option<String>,
}

#[derive(Debug, Deserialize)]
struct DeezerAlbum {
    title: Option<String>,
    cover_xl: Option<String>,
    artist: Option<DeezerArtist>,
}

#[derive(Debug, Deserialize)]
struct DeezerTrack {
    title: Option<String>,
    /// Seconds.
    duration: Option<u64>,
    artist: Option<DeezerArtist>,
    album: Option<DeezerAlbum>,
}

#[derive(Debug, Deserialize)]
struct DeezerResults<T> {
    #[serde(default = "Vec::new")]
    data: Vec<T>,
}

fn deezer<T: serde::de::DeserializeOwned>(agent: &ureq::Agent, path: &str, q: &str) -> Result<Vec<T>, String> {
    let r: DeezerResults<T> = agent
        .get(&format!("{DEEZER}{path}"))
        .query("q", q)
        .query("limit", "15")
        .call()
        .map_err(|e| e.to_string())?
        .into_json()
        .map_err(|e| e.to_string())?;
    Ok(r.data)
}

/// Deezer's advanced search syntax; quotes inside names would end the field early.
fn field(name: &str, value: &str) -> String {
    format!("{name}:\"{}\"", value.replace('"', ""))
}

fn deezer_songs(agent: &ureq::Agent, artist: &str, title: &str) -> Result<Vec<Candidate>, String> {
    let hits: Vec<DeezerTrack> = deezer(agent, "", &format!("{} {}", field("artist", artist), field("track", title)))?;
    Ok(hits
        .into_iter()
        .filter_map(|h| {
            Some(Candidate {
                artist: h.artist?.name?,
                title: h.title?,
                url: h.album?.cover_xl?,
                duration_ms: h.duration.map(|s| s * 1000),
            })
        })
        .collect())
}

fn deezer_albums(agent: &ureq::Agent, artist: &str, album: &str) -> Result<Vec<Candidate>, String> {
    let hits: Vec<DeezerAlbum> = deezer(agent, "/album", &format!("{} {}", field("artist", artist), field("album", album)))?;
    Ok(hits
        .into_iter()
        .filter_map(|h| Some(Candidate { artist: h.artist?.name?, title: h.title?, url: h.cover_xl?, duration_ms: None }))
        .collect())
}

fn deezer_artist_photo(agent: &ureq::Agent, artist: &str) -> Result<Option<String>, String> {
    let hits: Vec<DeezerArtist> = deezer(agent, "/artist", artist)?;
    let want = key(artist);
    Ok(hits
        .into_iter()
        .find(|a| a.name.as_deref().is_some_and(|n| key(n) == want))
        .and_then(|a| a.picture_xl)
        // Deezer returns a grey silhouette for artists without a photo.
        .filter(|u| !u.contains("/artist//")))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn c(artist: &str, title: &str, url: &str, ms: Option<u64>) -> Candidate {
        Candidate { artist: artist.into(), title: title.into(), url: url.into(), duration_ms: ms }
    }

    #[test]
    fn exact_song_nearest_duration() {
        let hits = [c("Other", "Butterfly", "a", Some(237_000)), c("LOONA", "Butterfly", "b", Some(250_000)), c("LOONA", "Butterfly", "c", Some(237_500))];
        assert_eq!(best_song(&hits, "LOONA", "Butterfly", Some(237_000), false).as_deref(), Some("c"));
    }

    #[test]
    fn exact_rejects_variants_relaxed_accepts() {
        let hits = [c("Panchiko", "Untitled Acoustic Song (Demo)", "x", None)];
        assert_eq!(best_song(&hits, "Panchiko", "Untitled Acoustic Song", None, false), None);
        assert_eq!(best_song(&hits, "Panchiko", "Untitled Acoustic Song", None, true).as_deref(), Some("x"));
    }

    #[test]
    fn decoration_is_ignored_on_both_sides() {
        // "- Live" and "(Live)" are decoration; the titles match exactly once cleaned.
        let hits = [c("Panchiko", "Untitled Acoustic Song (Live)", "y", None)];
        assert_eq!(best_song(&hits, "Panchiko", &clean_title("Untitled Acoustic Song - Live"), None, false).as_deref(), Some("y"));
    }

    #[test]
    fn album_needs_artist_and_title() {
        let hits = [c("Someone Else", "LIVE", "a", None), c("Panchiko", "Live", "b", None)];
        assert_eq!(best_album(&hits, "Panchiko", "LIVE").as_deref(), Some("b"));
    }

    #[test]
    fn itunes_size_rewrite() {
        assert_eq!(itunes_url("https://x/a/100x100bb.jpg"), "https://x/a/600x600bb.jpg");
    }

    #[test]
    fn no_artist_no_lookup() {
        let t = Track { title: "Song".into(), artist: "".into(), album: "".into(), duration_ms: None };
        assert_eq!(cover_url(&t), Ok(None));
    }
}
