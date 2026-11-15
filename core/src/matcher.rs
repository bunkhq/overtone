//! Track-name normalisation for lyrics lookups.
//!
//! Players decorate titles ("Song - Remastered 2011", "Song (Official Video)",
//! "Artist - Topic"). Lyrics databases mostly store the bare title. Lookups try
//! the raw names first, then progressively cleaner variants.

use unicode_normalization::UnicodeNormalization;

/// Bracketed or dash suffixes containing any of these are decoration.
const NOISE: &[&str] = &[
    "remaster", "remastered", "official", "video", "audio", "lyric", "lyrics", "visualizer", "visualiser",
    "explicit", "clean", "radio edit", "single version", "album version", "mono", "stereo", "hd", "hq", "4k",
    "live", "bonus track", "deluxe", "feat.", "feat ", "ft.", "ft ", "featuring", "with ", "prod.", "prod ",
];

/// Removes decoration from a title: `Song (feat. X) - 2011 Remaster` → `Song`.
pub fn clean_title(title: &str) -> String {
    let mut s = title.trim().to_string();
    loop {
        let before = s.clone();
        s = strip_noisy_brackets(&s);
        if let Some(i) = s.rfind(" - ") {
            if is_noise(&s[i + 3..]) {
                s.truncate(i);
            }
        }
        s = s.trim().trim_end_matches(['-', '–', '—']).trim().to_string();
        if s == before {
            break;
        }
    }
    if s.is_empty() { title.trim().to_string() } else { s }
}

/// Primary artist only: drops YouTube "- Topic" / "VEVO" and featured artists.
pub fn clean_artist(artist: &str) -> String {
    let mut s = artist.trim();
    for suffix in [" - Topic", "VEVO", "Vevo"] {
        if let Some(stripped) = s.strip_suffix(suffix) {
            s = stripped.trim();
        }
    }
    let lower = s.to_lowercase();
    let mut cut = s.len();
    for sep in [", ", " & ", " feat. ", " feat ", " ft. ", " featuring ", " x ", " × ", "; ", " / "] {
        if let Some(i) = lower.find(sep) {
            cut = cut.min(i);
        }
    }
    s[..cut].trim().to_string()
}

/// Case-, accent- and punctuation-insensitive key for comparing names.
pub fn key(s: &str) -> String {
    s.nfkd()
        .filter(|c| !unicode_normalization::char::is_combining_mark(*c))
        .flat_map(char::to_lowercase)
        .map(|c| if c.is_alphanumeric() { c } else { ' ' })
        .collect::<String>()
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
}

/// `(artist, title)` pairs to try, most specific first, without duplicates.
pub fn query_variants(artist: &str, title: &str) -> Vec<(String, String)> {
    let candidates = [
        (artist.trim().to_string(), title.trim().to_string()),
        (clean_artist(artist), title.trim().to_string()),
        (clean_artist(artist), clean_title(title)),
    ];
    let mut out: Vec<(String, String)> = Vec::new();
    for c in candidates {
        if !c.1.is_empty() && !out.iter().any(|o| key(&o.0) == key(&c.0) && key(&o.1) == key(&c.1)) {
            out.push(c);
        }
    }
    out
}

fn is_noise(s: &str) -> bool {
    let l = format!("{} ", s.to_lowercase());
    NOISE.iter().any(|n| l.contains(n)) || (l.trim().len() == 4 && l.trim().bytes().all(|b| b.is_ascii_digit()))
}

fn strip_noisy_brackets(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut rest = s;
    while let Some(i) = rest.find(['(', '[']) {
        let close = if rest.as_bytes()[i] == b'(' { ')' } else { ']' };
        match rest[i + 1..].find(close) {
            Some(j) if is_noise(&rest[i + 1..i + 1 + j]) => {
                out.push_str(&rest[..i]);
                rest = &rest[i + 1 + j + 1..];
            }
            _ => {
                out.push_str(&rest[..=i]);
                rest = &rest[i + 1..];
            }
        }
    }
    out.push_str(rest);
    out.split_whitespace().collect::<Vec<_>>().join(" ")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cleans_titles() {
        assert_eq!(clean_title("Here Comes the Sun - Remastered 2009"), "Here Comes the Sun");
        assert_eq!(clean_title("Song (feat. Someone) [Official Video]"), "Song");
        assert_eq!(clean_title("Song - 2015 Remaster"), "Song");
        assert_eq!(clean_title("Song - Live at Wembley"), "Song");
        assert_eq!(clean_title("Song (Official Music Video) (HD)"), "Song");
        assert_eq!(clean_title("Song - 1999"), "Song");
    }

    #[test]
    fn keeps_meaningful_parts() {
        assert_eq!(clean_title("Love Story (Taylor's Version)"), "Love Story (Taylor's Version)");
        assert_eq!(clean_title("Part I - The Beginning"), "Part I - The Beginning");
        assert_eq!(clean_title("(Don't Fear) The Reaper"), "(Don't Fear) The Reaper");
        assert_eq!(clean_title("[Official Video]"), "[Official Video]");
    }

    #[test]
    fn cleans_artists() {
        assert_eq!(clean_artist("The Buttertones - Topic"), "The Buttertones");
        assert_eq!(clean_artist("Artist feat. Other"), "Artist");
        assert_eq!(clean_artist("A, B & C"), "A");
        assert_eq!(clean_artist("Simon & Garfunkel"), "Simon");
        assert_eq!(clean_artist("TaylorSwiftVEVO"), "TaylorSwift");
    }

    #[test]
    fn keys_fold_case_accents_punctuation() {
        assert_eq!(key("Beyoncé – Déjà Vu!"), "beyonce deja vu");
        assert_eq!(key("  AC/DC "), "ac dc");
    }

    #[test]
    fn variants_dedupe() {
        let v = query_variants("Band", "Song");
        assert_eq!(v, [("Band".to_string(), "Song".to_string())]);
        let v = query_variants("Band - Topic", "Song (Official Video)");
        assert_eq!(v.len(), 3);
        assert_eq!(v[2], ("Band".to_string(), "Song".to_string()));
    }
}
