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
//!
//! When the shell has the artwork itself, [`cover_like`] does better than names:
//! it gathers the same candidates (plus album searches, and title or album
//! searches for files with no artist), downloads each one's thumbnail and keeps
//! the one that looks most like the local cover. A single, a deluxe edition and
//! the album often share a title but not a sleeve; this picks the sleeve the
//! user sees. If nothing looks close, it says so, and the shell can show the
//! local cover some other way.

use crate::color::{Lab, Rgb};
use crate::lyrics::USER_AGENT;
use crate::matcher::{clean_artist, clean_title, key};
use crate::Track;
use serde::Deserialize;
use std::io::Read;
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
    /// A small rendition of the same cover, for comparing against the local artwork.
    thumb: String,
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

// ── Matching by appearance ───────────────────────────────────────────────────

/// A catalogue cover that looks like the local artwork.
#[derive(Debug, Clone, PartialEq)]
pub struct CoverMatch {
    pub url: String,
    pub source: CoverSource,
    /// Mean OKLab distance between the two fingerprints (0 = identical).
    pub distance: f32,
}

/// Fingerprint grid: cells per side. Coarse enough to ignore compression and
/// resampling, fine enough to tell two dark sleeves with different layouts apart.
const GRID: usize = 12;
/// At most this far apart, the catalogue cover is taken to be the same artwork.
/// Re-encodes and different sizes of one sleeve measure about 0.01-0.03; a
/// different sleeve, even a similar one, well over 0.08.
pub const MATCH_MAX: f32 = 0.06;
/// Close enough that no other candidate needs downloading.
const MATCH_SURE: f32 = 0.03;
/// Thumbnails downloaded per lookup, best-ranked first.
const MAX_THUMBS: usize = 8;

/// A centre-square `GRID`×`GRID` average of the image in OKLab. `None` for an empty image.
pub fn fingerprint(rgba: &[u8], width: usize, height: usize) -> Option<Vec<Lab>> {
    let side = width.min(height);
    if side == 0 || rgba.len() < width * height * 4 {
        return None;
    }
    let (x0, y0) = ((width - side) / 2, (height - side) / 2);
    let mut sums = vec![[0f32; 3]; GRID * GRID];
    let mut counts = vec![0u32; GRID * GRID];
    for y in 0..side {
        let gy = y * GRID / side;
        let row = (y0 + y) * width;
        for x in 0..side {
            let i = (row + x0 + x) * 4;
            let lab = Rgb::new(rgba[i], rgba[i + 1], rgba[i + 2]).to_oklab();
            let cell = gy * GRID + x * GRID / side;
            sums[cell][0] += lab.l;
            sums[cell][1] += lab.a;
            sums[cell][2] += lab.b;
            counts[cell] += 1;
        }
    }
    Some(
        sums.iter()
            .zip(&counts)
            .map(|(s, &n)| {
                let n = n.max(1) as f32;
                Lab { l: s[0] / n, a: s[1] / n, b: s[2] / n }
            })
            .collect(),
    )
}

/// Mean per-cell OKLab distance between two fingerprints.
pub fn fingerprint_distance(a: &[Lab], b: &[Lab]) -> f32 {
    if a.len() != b.len() || a.is_empty() {
        return f32::INFINITY;
    }
    a.iter().zip(b).map(|(x, y)| x.dist2(y).sqrt()).sum::<f32>() / a.len() as f32
}

/// The catalogue cover that looks most like `rgba` (the local artwork, straight
/// RGBA), if one is within [`MATCH_MAX`]. `Ok(None)` when none is close or no
/// candidate was found; `Err` only when every source was unreachable.
pub fn cover_like(track: &Track, rgba: &[u8], width: u32, height: u32) -> Result<Option<CoverMatch>, String> {
    let Some(local) = fingerprint(rgba, width as usize, height as usize) else { return Ok(None) };
    let agent = ureq::AgentBuilder::new().timeout(Duration::from_secs(6)).user_agent(USER_AGENT).build();
    let (candidates, reached) = look_alike_candidates(&agent, track);
    if !reached {
        return Err("no catalog reachable".into());
    }

    let mut best: Option<CoverMatch> = None;
    for (c, source) in candidates.into_iter().take(MAX_THUMBS) {
        let Some(fp) = download_fingerprint(&agent, &c.thumb) else { continue };
        let distance = fingerprint_distance(&local, &fp);
        if best.as_ref().is_none_or(|b| distance < b.distance) {
            best = Some(CoverMatch { url: c.url, source, distance });
        }
        if distance <= MATCH_SURE {
            break;
        }
    }
    Ok(best.filter(|b| b.distance <= MATCH_MAX))
}

/// Candidates worth comparing, most likely first, without repeats: songs with the
/// exact title (nearest duration first), albums with the track's album name, then
/// songs whose title merely contains the track's. Without an artist, title and
/// album searches stand in, and only exact names count.
fn look_alike_candidates(agent: &ureq::Agent, track: &Track) -> (Vec<(Candidate, CoverSource)>, bool) {
    let (artist, title, album) = (clean_artist(&track.artist), clean_title(&track.title), clean_title(&track.album));
    let (want_title, want_album) = (key(&title), key(&album));
    let mut reached = false;
    let mut collect = |r: Result<Vec<Candidate>, String>| match r {
        Ok(v) => {
            reached = true;
            v
        }
        Err(_) => Vec::new(),
    };

    let mut songs = Vec::new();
    let mut albums = Vec::new();
    if !want_title.is_empty() {
        songs.extend(collect(itunes_songs(agent, &artist, &title)));
        songs.extend(collect(deezer_songs(agent, &artist, &title)));
    }
    if !want_album.is_empty() {
        albums.extend(collect(itunes_albums(agent, &artist, &album)));
        albums.extend(collect(deezer_albums(agent, &artist, &album)));
    }

    let artist_ok = |c: &Candidate| artist.is_empty() || artist_matches(&c.artist, &artist);
    let title_key = |c: &Candidate| key(&clean_title(&c.title));
    let gap = |c: &Candidate| match (c.duration_ms, track.duration_ms) {
        (Some(h), Some(w)) => h.abs_diff(w),
        _ => u64::MAX / 2,
    };

    let mut exact: Vec<&Candidate> = songs.iter().filter(|c| artist_ok(c) && title_key(c) == want_title).collect();
    exact.sort_by_key(|c| gap(c));
    let album_hits = albums.iter().filter(|c| {
        let t = title_key(c);
        artist_ok(c) && !t.is_empty() && if artist.is_empty() { t == want_album } else { t == want_album || t.contains(&want_album) || want_album.contains(&t) }
    });
    let relaxed = songs.iter().filter(|c| {
        let t = title_key(c);
        !artist.is_empty() && artist_ok(c) && !t.is_empty() && t != want_title && (t.contains(&want_title) || want_title.contains(&t))
    });

    let mut out: Vec<(Candidate, CoverSource)> = Vec::new();
    let ranked = exact
        .into_iter()
        .map(|c| (c, CoverSource::Song))
        .chain(album_hits.map(|c| (c, CoverSource::Album)))
        .chain(relaxed.map(|c| (c, CoverSource::RelaxedSong)));
    for (c, source) in ranked {
        // The same sleeve turns up from several searches and both catalogues.
        if !out.iter().any(|(o, _)| o.thumb == c.thumb) {
            out.push((c.clone(), source));
        }
    }
    (out, reached)
}

fn download_fingerprint(agent: &ureq::Agent, url: &str) -> Option<Vec<Lab>> {
    let mut bytes = Vec::new();
    agent.get(url).call().ok()?.into_reader().take(4 << 20).read_to_end(&mut bytes).ok()?;
    let (rgba, w, h) = decode_jpeg(&bytes)?;
    fingerprint(&rgba, w, h)
}

/// JPEG to straight RGBA (catalogue covers are JPEGs).
pub fn decode_jpeg(bytes: &[u8]) -> Option<(Vec<u8>, usize, usize)> {
    use zune_jpeg::zune_core::{bytestream::ZCursor, colorspace::ColorSpace, options::DecoderOptions};
    let options = DecoderOptions::default().jpeg_set_out_colorspace(ColorSpace::RGBA);
    let mut decoder = zune_jpeg::JpegDecoder::new_with_options(ZCursor::new(bytes), options);
    let px = decoder.decode().ok()?;
    let (w, h) = decoder.dimensions()?;
    (px.len() >= w * h * 4).then_some((px, w, h))
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
    Ok(from_itunes(itunes(agent, format!("{artist} {title}").trim(), "song")?, false))
}

fn itunes_albums(agent: &ureq::Agent, artist: &str, album: &str) -> Result<Vec<Candidate>, String> {
    Ok(from_itunes(itunes(agent, format!("{artist} {album}").trim(), "album")?, true))
}

fn from_itunes(hits: Vec<ItunesHit>, albums: bool) -> Vec<Candidate> {
    hits.into_iter()
        .filter_map(|h| {
            let title = if albums { h.collection_name } else { h.track_name }?;
            let thumb = h.artwork_url100?;
            Some(Candidate { artist: h.artist_name?, title, url: itunes_url(&thumb), thumb, duration_ms: h.track_time_millis })
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
    cover_medium: Option<String>,
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
/// An empty value leaves the field out.
fn field(name: &str, value: &str) -> String {
    if value.trim().is_empty() {
        return String::new();
    }
    format!("{name}:\"{}\"", value.replace('"', ""))
}

fn deezer_songs(agent: &ureq::Agent, artist: &str, title: &str) -> Result<Vec<Candidate>, String> {
    let hits: Vec<DeezerTrack> = deezer(agent, "", format!("{} {}", field("artist", artist), field("track", title)).trim())?;
    Ok(hits
        .into_iter()
        .filter_map(|h| {
            let album = h.album?;
            let url = album.cover_xl?;
            Some(Candidate {
                artist: h.artist?.name?,
                title: h.title?,
                thumb: album.cover_medium.unwrap_or_else(|| url.clone()),
                url,
                duration_ms: h.duration.map(|s| s * 1000),
            })
        })
        .collect())
}

fn deezer_albums(agent: &ureq::Agent, artist: &str, album: &str) -> Result<Vec<Candidate>, String> {
    let hits: Vec<DeezerAlbum> = deezer(agent, "/album", format!("{} {}", field("artist", artist), field("album", album)).trim())?;
    Ok(hits
        .into_iter()
        .filter_map(|h| {
            let url = h.cover_xl?;
            Some(Candidate { artist: h.artist?.name?, title: h.title?, thumb: h.cover_medium.unwrap_or_else(|| url.clone()), url, duration_ms: None })
        })
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
        Candidate { artist: artist.into(), title: title.into(), url: url.into(), thumb: url.into(), duration_ms: ms }
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
    fn fingerprints_tell_sleeves_apart() {
        // A red sleeve with a white square, the same at another size, and the square moved.
        let draw = |side: usize, sq: (usize, usize)| {
            let mut px = Vec::with_capacity(side * side * 4);
            for y in 0..side {
                for x in 0..side {
                    let inside = (sq.0 * side / 10..sq.1 * side / 10).contains(&x) && (sq.0 * side / 10..sq.1 * side / 10).contains(&y);
                    px.extend_from_slice(if inside { &[250, 250, 250, 255] } else { &[200, 30, 40, 255] });
                }
            }
            px
        };
        let a = fingerprint(&draw(120, (3, 7)), 120, 120).unwrap();
        let resized = fingerprint(&draw(48, (3, 7)), 48, 48).unwrap();
        let moved = fingerprint(&draw(120, (0, 4)), 120, 120).unwrap();
        assert!(fingerprint_distance(&a, &resized) < MATCH_MAX / 2.0);
        assert!(fingerprint_distance(&a, &moved) > MATCH_MAX);
        assert!(fingerprint(&[], 0, 0).is_none());
    }

    #[test]
    fn wide_images_are_centre_cropped() {
        // A 16:9 thumbnail with black bars compares on its middle square.
        let (w, h) = (160, 90);
        let mut px = Vec::with_capacity(w * h * 4);
        for _ in 0..h {
            for x in 0..w {
                px.extend_from_slice(if (35..125).contains(&x) { &[20, 120, 220, 255] } else { &[0, 0, 0, 255] });
            }
        }
        let square = [20u8, 120, 220, 255].repeat(90 * 90);
        let d = fingerprint_distance(&fingerprint(&px, w, h).unwrap(), &fingerprint(&square, 90, 90).unwrap());
        assert!(d < 0.01, "{d}");
    }

    #[test]
    fn no_artist_no_lookup() {
        let t = Track { title: "Song".into(), artist: "".into(), album: "".into(), duration_ms: None };
        assert_eq!(cover_url(&t), Ok(None));
    }
}
