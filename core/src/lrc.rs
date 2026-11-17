//! LRC lyrics: parsing and position lookup.
//!
//! Handles multiple timestamps per line (`[00:12.00][01:30.50]chorus`),
//! `mm:ss`, `mm:ss.x`, `mm:ss.xx`, `mm:ss.xxx` and `mm:ss:xx`, the `[offset:]`
//! tag, metadata tags (ignored), and enhanced-LRC word stamps (`<00:12.40>`,
//! stripped). Empty lines are kept: they mark instrumental gaps.

#[derive(Debug, Clone, PartialEq, Eq)]
#[cfg_attr(feature = "uniffi", derive(uniffi::Record))]
pub struct LyricLine {
    pub start_ms: u64,
    /// Empty for an instrumental gap.
    pub text: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Lyrics {
    /// Sorted by `start_ms`. For plain lyrics every `start_ms` is 0.
    pub lines: Vec<LyricLine>,
    pub synced: bool,
}

impl Lyrics {
    /// Parses LRC text. Returns `None` if it contains no timed lines.
    pub fn parse_lrc(src: &str) -> Option<Self> {
        let mut offset_ms: i64 = 0;
        let mut timed: Vec<(i64, String)> = Vec::new();
        for raw in src.lines() {
            let mut rest = raw.trim();
            let mut stamps = Vec::new();
            while let Some(stripped) = rest.strip_prefix('[') {
                let Some(end) = stripped.find(']') else { break };
                let tag = &stripped[..end];
                rest = &stripped[end + 1..];
                if let Some(t) = parse_time(tag) {
                    stamps.push(t);
                } else if let Some(v) = tag.strip_prefix("offset:") {
                    offset_ms = v.trim().parse().unwrap_or(0);
                }
            }
            if stamps.is_empty() {
                continue;
            }
            let text = strip_word_stamps(rest).trim().to_string();
            for t in stamps {
                timed.push((t, text.clone()));
            }
        }
        if timed.is_empty() {
            return None;
        }
        // A positive offset shows lyrics sooner.
        let mut lines: Vec<LyricLine> = timed
            .into_iter()
            .map(|(t, text)| LyricLine { start_ms: (t - offset_ms).max(0) as u64, text })
            .collect();
        lines.sort_by_key(|l| l.start_ms); // stable: keeps file order for equal stamps
        // Collapse leading gaps and repeated empty lines; they carry no information.
        lines.dedup_by(|b, a| a.text.is_empty() && b.text.is_empty());
        while lines.first().is_some_and(|l| l.text.is_empty()) {
            lines.remove(0);
        }
        if lines.is_empty() {
            return None;
        }
        Some(Self { lines, synced: true })
    }

    /// Plain (unsynced) lyrics, one entry per non-empty line.
    pub fn plain(src: &str) -> Self {
        let lines = src
            .lines()
            .map(str::trim)
            .filter(|l| !l.is_empty())
            .map(|t| LyricLine { start_ms: 0, text: t.to_string() })
            .collect();
        Self { lines, synced: false }
    }

    /// Index of the line active at `position_ms`, or `None` before the first line.
    pub fn line_at(&self, position_ms: u64) -> Option<usize> {
        if !self.synced {
            return None;
        }
        match self.lines.partition_point(|l| l.start_ms <= position_ms) {
            0 => None,
            n => Some(n - 1),
        }
    }

    /// When the active line next changes, so the shell can arm one timer
    /// instead of running a frame loop.
    pub fn next_change_ms(&self, position_ms: u64) -> Option<u64> {
        if !self.synced {
            return None;
        }
        let n = self.lines.partition_point(|l| l.start_ms <= position_ms);
        self.lines.get(n).map(|l| l.start_ms)
    }
}

/// `mm:ss`, `mm:ss.f{1,3}`, `mm:ss:ff`. Minutes may exceed 59.
fn parse_time(tag: &str) -> Option<i64> {
    let (min, rest) = tag.split_once(':')?;
    let min: i64 = min.trim().parse().ok()?;
    let (sec, frac) = match rest.split_once(['.', ':']) {
        Some((s, f)) => (s, Some(f)),
        None => (rest, None),
    };
    let sec: i64 = sec.trim().parse().ok()?;
    if !(0..60).contains(&sec) || min < 0 {
        return None;
    }
    let frac_ms = match frac {
        None => 0,
        Some(f) if !f.is_empty() && f.len() <= 3 && f.bytes().all(|b| b.is_ascii_digit()) => {
            let v: i64 = f.parse().ok()?;
            v * 10i64.pow(3 - f.len() as u32)
        }
        Some(_) => return None,
    };
    Some(min * 60_000 + sec * 1000 + frac_ms)
}

fn strip_word_stamps(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut rest = s;
    while let Some(i) = rest.find('<') {
        out.push_str(&rest[..i]);
        match rest[i..].find('>') {
            Some(j) if parse_time(&rest[i + 1..i + j]).is_some() => rest = &rest[i + j + 1..],
            _ => {
                out.push('<');
                rest = &rest[i + 1..];
            }
        }
    }
    out.push_str(rest);
    // Word stamps usually sit between words; collapse the doubled spaces they leave.
    out.split_whitespace().collect::<Vec<_>>().join(" ")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_basic_and_metadata() {
        let l = Lyrics::parse_lrc("[ar:Someone]\n[ti:Song]\n[00:01.50]First\n[00:04.00]Second\n").unwrap();
        assert_eq!(l.lines.len(), 2);
        assert_eq!(l.lines[0], LyricLine { start_ms: 1500, text: "First".into() });
        assert!(l.synced);
    }

    #[test]
    fn multiple_stamps_and_sorting() {
        let l = Lyrics::parse_lrc("[00:10.00][00:30.00]Chorus\n[00:20.00]Verse").unwrap();
        let t: Vec<_> = l.lines.iter().map(|l| (l.start_ms, l.text.as_str())).collect();
        assert_eq!(t, [(10_000, "Chorus"), (20_000, "Verse"), (30_000, "Chorus")]);
    }

    #[test]
    fn fraction_formats() {
        assert_eq!(parse_time("01:02"), Some(62_000));
        assert_eq!(parse_time("01:02.5"), Some(62_500));
        assert_eq!(parse_time("01:02.50"), Some(62_500));
        assert_eq!(parse_time("01:02.505"), Some(62_505));
        assert_eq!(parse_time("01:02:50"), Some(62_500));
        assert_eq!(parse_time("12:00.00"), Some(720_000));
        assert_eq!(parse_time("ar:Name"), None);
        assert_eq!(parse_time("00:61.00"), None);
    }

    #[test]
    fn offset_shifts_earlier() {
        let l = Lyrics::parse_lrc("[offset:+500]\n[00:02.00]a\n[00:00.20]b").unwrap();
        assert_eq!(l.lines[0].start_ms, 0); // clamped
        assert_eq!(l.lines[1].start_ms, 1500);
    }

    #[test]
    fn strips_word_stamps_keeps_angle_text() {
        let l = Lyrics::parse_lrc("[00:01.00]<00:01.00>Hello <00:01.50>world\n[00:03.00]a <b> c").unwrap();
        assert_eq!(l.lines[0].text, "Hello world");
        assert_eq!(l.lines[1].text, "a <b> c");
    }

    #[test]
    fn gaps_kept_but_collapsed() {
        let l = Lyrics::parse_lrc("[00:00.00]\n[00:01.00]one\n[00:05.00]\n[00:06.00]\n[00:09.00]two").unwrap();
        let t: Vec<_> = l.lines.iter().map(|l| l.text.as_str()).collect();
        assert_eq!(t, ["one", "", "two"]);
    }

    #[test]
    fn lookup_and_next_change() {
        let l = Lyrics::parse_lrc("[00:01.00]a\n[00:03.00]b\n[00:07.00]c").unwrap();
        assert_eq!(l.line_at(0), None);
        assert_eq!(l.line_at(1000), Some(0));
        assert_eq!(l.line_at(6999), Some(1));
        assert_eq!(l.line_at(60_000), Some(2));
        assert_eq!(l.next_change_ms(0), Some(1000));
        assert_eq!(l.next_change_ms(3000), Some(7000));
        assert_eq!(l.next_change_ms(7000), None);
    }

    #[test]
    fn no_timed_lines_is_none() {
        assert_eq!(Lyrics::parse_lrc("just words\nno stamps"), None);
        assert_eq!(Lyrics::parse_lrc("[00:01.00]\n[00:02.00]"), None);
    }

    #[test]
    fn plain_lyrics_have_no_position() {
        let l = Lyrics::plain("one\n\n two \n");
        assert_eq!(l.lines.len(), 2);
        assert!(!l.synced);
        assert_eq!(l.line_at(5000), None);
    }
}
